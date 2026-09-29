using System.Globalization;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Wallet;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The owner's reports (slice S5): days and hours in the club's zone (Asia/Tashkent, UTC+5) — a charge at 00:30 local
/// counts for the local day although its UTC date is the day before; time revenue = charges − refunds; <c>days</c> 0 → 1,
/// above 90 → 90, not a number → 7; <c>topGames</c> — distinct players of successful launches in the period;
/// <c>topProducts</c> empty in v1.
/// </summary>
public sealed class ReportsTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private const string Zone = "Asia/Tashkent";

    [Fact]
    public async Task Revenue_heat_and_top_games_follow_the_club_calendar()
    {
        // Local noon of tomorrow, so every instant below is in the past and on a known local day.
        var start = Server.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(ClubTime.Local(start, Zone)).AddDays(1);
        Server.Clock.Advance(ClubTime.Utc(today.ToDateTime(new TimeOnly(12, 0)), Zone) - start);
        DateTimeOffset At(int dayOffset, int hour, int minute) => ClubTime.Utc(today.AddDays(dayOffset).ToDateTime(new TimeOnly(hour, minute)), Zone);

        var owner = await LoginAsync(Server, OwnerPin);
        var player = await Players.CreateAsync(Server);
        var other = await Players.CreateAsync(Server);
        var agent = await TestAgent.CreateAsync(Server);
        var clubId = await Players.ScalarAsync<Guid>(Server, "SELECT id FROM clubs");
        await PostAsync(player.Id, At(0, 0, 30), new LedgerLine("charge", -1_000_000, "Сеанс", clubId)); // UTC: yesterday 19:30
        await PostAsync(player.Id, At(0, 1, 0), new LedgerLine("refund", 200_000, "Возврат", clubId));
        await PostAsync(player.Id, At(-1, 23, 30), new LedgerLine("topUp", 5_000_000, "Пополнение", clubId, Method: "cash"));
        await PostAsync(player.Id, At(-8, 12, 0), new LedgerLine("charge", -700_000, "Давно", clubId));

        // One session at the counter from 12:00 to 13:30 local, 120 minutes bought: two seat-hours, a refund of the rest.
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/open", owner, new { openingCash = 0 });
        var opened = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 120 });
        Server.Clock.Advance(TimeSpan.FromMinutes(90));
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId });
        var (charged, refunded) = (opened.GetProperty("charged").GetProperty("amount").GetInt64(), ended.GetProperty("refunded").GetProperty("amount").GetInt64());
        Assert.True(refunded > 0);

        var games = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var (game, title) in games.Zip(new[] { "Dota 2", "CS2", "Broken", "Old" }))
        {
            await Players.ExecuteAsync(Server, "INSERT INTO games (id, club_id, title, data, updated_at) VALUES (@game, @clubId, @title, '{}', now())", new { game, clubId, title });
        }

        foreach (var (game, user, ok, at) in new[]
        {
            (games[0], player.Id, true, At(0, 12, 5)), (games[0], other.Id, true, At(-2, 18, 0)), (games[0], player.Id, true, At(-1, 18, 0)),
            (games[1], other.Id, true, At(-3, 18, 0)), (games[2], player.Id, false, At(0, 12, 10)), (games[3], other.Id, true, At(-10, 18, 0)),
        })
        {
            await Players.ExecuteAsync(Server,
                """
                INSERT INTO launch_reports (id, club_id, pc_id, game_id, user_id, phase, started_at, data, created_at)
                VALUES (@id, @clubId, @PcId, @game, @user, 'launch', @at, jsonb_build_object('result', jsonb_build_object('ok', @ok)), @at)
                """,
                new { id = Guid.NewGuid(), clubId, agent.PcId, game, user, at, ok });
        }

        var report = await ExpectAsync(Server, 200, HttpMethod.Get, "/reports", owner);
        Assert.Equal(7, report.GetProperty("days").GetInt32());
        var byDay = report.GetProperty("byDay").EnumerateArray().ToList();
        Assert.Equal(7, byDay.Count);
        Assert.Equal(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), byDay[^1].GetProperty("date").GetString());
        Assert.Equal(today.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), byDay[0].GetProperty("date").GetString());
        Assert.Equal(1_000_000 - 200_000 + charged - refunded, byDay[^1].GetProperty("sessions").GetInt64());
        Assert.Equal(5_000_000, byDay[^2].GetProperty("topUps").GetInt64());
        Assert.Equal(0, byDay[^1].GetProperty("topUps").GetInt64());
        var totals = report.GetProperty("totals");
        Assert.Equal((1_000_000 - 200_000 + charged - refunded, 0, 5_000_000, 1),
            (totals.GetProperty("sessions").GetInt64(), totals.GetProperty("shop").GetInt64(), totals.GetProperty("topUps").GetInt64(), totals.GetProperty("sessionsCount").GetInt32()));

        var heat = report.GetProperty("heat");
        var dow = (int)today.DayOfWeek;
        Assert.Equal((1, 1, 0), (heat[dow][12].GetInt32(), heat[dow][13].GetInt32(), heat[dow][14].GetInt32()));
        Assert.Equal(2, heat.EnumerateArray().Sum(d => d.EnumerateArray().Sum(h => h.GetInt32())));

        Assert.Equal([("Dota 2", 2), ("CS2", 1)], report.GetProperty("topGames").EnumerateArray().Select(g => (g.GetProperty("title").GetString(), g.GetProperty("players").GetInt32())));
        Assert.Empty(report.GetProperty("topProducts").EnumerateArray());
        Assert.Single(report.GetProperty("shifts").EnumerateArray());

        foreach (var (days, expected) in new[] { ("0", 1), ("1000", 90), ("abc", 7), ("30", 30) })
        {
            var r = await ExpectAsync(Server, 200, HttpMethod.Get, $"/reports?days={days}", owner);
            Assert.Equal((expected, expected), (r.GetProperty("days").GetInt32(), r.GetProperty("byDay").GetArrayLength()));
        }

        var one = await ExpectAsync(Server, 200, HttpMethod.Get, "/reports?days=1", owner);
        Assert.Equal(0, one.GetProperty("totals").GetProperty("topUps").GetInt64());
        Assert.Equal(["Dota 2"], one.GetProperty("topGames").EnumerateArray().Select(g => g.GetProperty("title").GetString()));
        var long30 = await ExpectAsync(Server, 200, HttpMethod.Get, "/reports?days=30", owner);
        Assert.Equal(1_000_000 - 200_000 + charged - refunded + 700_000, long30.GetProperty("totals").GetProperty("sessions").GetInt64());

        using var refused = await Server.Http.SendAsync(Request(HttpMethod.Get, "/reports", await LoginAsync(Server, CashierPin)));
        await Contract.ReadErrorAsync(refused, 403, "forbidden", "ownerOnly");
    }

    private async Task PostAsync(Guid userId, DateTimeOffset at, LedgerLine line)
    {
        await using var c = await Server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await Ledger.PostAsync(c, tx, userId, allowOverdraft: false, at, line);
        await tx.CommitAsync();
    }
}
