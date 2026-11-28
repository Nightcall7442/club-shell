using System.Text.Json;
using ClubShell.Server.Sessions;
using ClubShell.Server.Wallet;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The minors' night curfew (D-108) with the fixture's clock: nothing starts or extends in the forbidden hours, a purchase
/// that would run into them is refused with the minutes that still fit (<c>400 minutes curfew {maxMinutes}</c>); a minor's
/// session running when they start is warned 10 and 5 minutes before and ended at the start by the tick — unused time
/// refunded, the bonus account first (D-106), the PC told to end and the player signed out; an adult is never touched.
/// </summary>
public sealed class CurfewTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private SessionTickWorker Tick => Server.Services.GetRequiredService<SessionTickWorker>();

    [Fact]
    public async Task A_purchase_that_would_run_into_the_curfew_is_refused_with_what_still_fits()
    {
        AlignToMinute();
        var (agent, minor) = await Players.SignedInAsync(Server);
        await Players.BirthDateAsync(Server, minor, Players.BornYearsAgo(Server, 14));
        var (adultAgent, adult) = await Players.SignedInAsync(Server);
        await Players.CurfewAsync(Server, fromMinutes: 45, toMinutes: 400);
        try
        {
            var (status, body) = await Players.StartAsync(agent, minor, minutes: 60);
            Assert.Equal(400, status);
            var details = PurchaseRulesTests.Details(body);
            Assert.Equal(("minutes", "curfew", 45), (details.GetProperty("field").GetString(), details.GetProperty("reason").GetString(), details.GetProperty("maxMinutes").GetInt32()));
            Assert.Equal(201, (await Players.StartAsync(adultAgent, adult, minutes: 60)).Status);

            var (started, session) = await Players.StartAsync(agent, minor, minutes: 30);
            Assert.Equal(201, started);
            using (var extend = await agent.PostAsync($"/api/v1/sessions/{session.GetProperty("id").GetGuid()}/extend", new { minutes = 30 }, Guid.NewGuid()))
            {
                var refused = await Players.ReadAsync(extend, 400);
                Assert.Equal(15, PurchaseRulesTests.Details(refused).GetProperty("maxMinutes").GetInt32());
            }

            // The desk's extension: the same rule.
            var owner = await LoginAsync(Server, OwnerPin);
            await OpenShiftAsync(Server, owner);
            var desk = await ExpectAsync(Server, 400, HttpMethod.Post, "/sessions/extend", owner, new { pcId = agent.PcId, minutes = 30 });
            Assert.Equal("curfew", PurchaseRulesTests.Details(desk).GetProperty("reason").GetString());
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/extend", owner, new { pcId = agent.PcId, minutes = 15 });

            // In the forbidden hours nothing starts: the session is ended at their start first.
            Server.Clock.Advance(TimeSpan.FromMinutes(45));
            await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
            await Players.ReadAsync(await adultAgent.HeartbeatAsync(), 200);
            await Tick.RunOnceAsync();
            Assert.Equal("ended", await StateAsync(session.GetProperty("id").GetGuid()));
            var denied = await ExpectAsync(Server, 403, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = minor.Id, tariffId = Players.Standard, minutes = 30 });
            Assert.Equal("minorCurfew", PurchaseRulesTests.Details(denied).GetProperty("rule").GetString());
            Assert.StartsWith("Несовершеннолетним нельзя играть с ", denied.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM sessions WHERE user_id = @Id AND state <> 'ended'", new { adult.Id }));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task A_running_session_of_a_minor_is_warned_then_ended_at_the_start_with_a_refund()
    {
        AlignToMinute();
        var (agent, minor) = await Players.SignedInAsync(Server);
        var (unknownAgent, unknown) = await Players.SignedInAsync(Server);
        var (adultAgent, adult) = await Players.SignedInAsync(Server);
        await GiveBonusAsync(minor.Id, 600_000);

        // Bought before the owner moved the hours: 60 minutes at 12 000 sum, the bonus first.
        var id = (await Players.StartAsync(agent, minor, minutes: 60)).Body.GetProperty("id").GetGuid();
        var unknownId = (await Players.StartAsync(unknownAgent, unknown, minutes: 60)).Body.GetProperty("id").GetGuid();
        var adultId = (await Players.StartAsync(adultAgent, adult, minutes: 60)).Body.GetProperty("id").GetGuid();
        await Players.BirthDateAsync(Server, unknown, null);
        await Players.BirthDateAsync(Server, minor, Players.BornYearsAgo(Server, 16));
        Assert.Equal((9_400_000L, 0L), (await Players.BalanceAsync(Server, minor.Id), await Players.BonusAsync(Server, minor.Id)));
        await Players.CurfewAsync(Server, fromMinutes: 15, toMinutes: 300);
        try
        {
            await AdvanceAsync(TimeSpan.FromMinutes(5), agent, unknownAgent, adultAgent); // 10 minutes before
            await AdvanceAsync(TimeSpan.FromSeconds(30), agent, unknownAgent, adultAgent);
            Assert.Equal(new[] { 10 }, await WarnedAsync(id));
            await AdvanceAsync(TimeSpan.FromSeconds(270), agent, unknownAgent, adultAgent); // 5 minutes before
            Assert.Equal(new[] { 10, 5 }, await WarnedAsync(id));
            Assert.Equal(new[] { 10, 5 }, await WarnedAsync(unknownId));
            Assert.Empty(await WarnedAsync(adultId));
            Assert.Equal("active", await StateAsync(id));

            // A fresh worker (a restart) does not warn again.
            await ActivatorUtilities.CreateInstance<SessionTickWorker>(Server.Services).RunOnceAsync();
            Assert.Equal(new[] { 10, 5 }, await WarnedAsync(id));

            await AdvanceAsync(TimeSpan.FromSeconds(301), agent, unknownAgent, adultAgent); // the start, a second late
            Assert.Equal(("ended", "admin", 900), (await StateAsync(id),
                await Players.ScalarAsync<string>(Server, "SELECT end_reason FROM sessions WHERE id = @id", new { id }),
                await Players.ScalarAsync<int>(Server, "SELECT used_before_sec FROM sessions WHERE id = @id", new { id })));
            Assert.Equal("ended", await StateAsync(unknownId));
            Assert.Equal("active", await StateAsync(adultId));

            // 45 of 60 minutes back (9 000 sum): to the bonus account what the session took from it, the rest to the balance.
            Assert.Equal((9_700_000L, 600_000L), (await Players.BalanceAsync(Server, minor.Id), await Players.BonusAsync(Server, minor.Id)));
            Assert.Equal(1, await Players.ScalarAsync<int>(Server,
                "SELECT count(*)::int FROM session_events WHERE session_id = @id AND source = 'server' AND type = 'curfewEnd'", new { id }));

            // The PC ends it as after a desk end, and the player is signed out of it.
            var command = JsonElement.Parse(await Players.ScalarAsync<string>(Server,
                "SELECT payload::text FROM agent_commands WHERE pc_id = @PcId AND name = 'endSession' ORDER BY created_at DESC LIMIT 1", new { agent.PcId }));
            Assert.Equal((id, "admin"), (command.GetProperty("sessionId").GetGuid(), command.GetProperty("reason").GetString()));
            Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE user_id = @Id", new { minor.Id }));

            // The desk's seat note.
            var owner = await LoginAsync(Server, OwnerPin);
            var seats = (await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", owner)).GetProperty("seats").EnumerateArray().ToList();
            var seat = seats.Single(s => s.GetProperty("pc").GetProperty("id").GetGuid() == agent.PcId);
            Assert.True(seat.GetProperty("curfew").GetProperty("ended").GetBoolean());
            Assert.Equal(minor.Username, seat.GetProperty("curfew").GetProperty("userName").GetString());
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task The_desk_sees_the_minor_and_the_curfew_coming_on_the_seat()
    {
        AlignToMinute();
        var (agent, minor) = await Players.SignedInAsync(Server);
        await Players.BirthDateAsync(Server, minor, Players.BornYearsAgo(Server, 12));
        await Players.CurfewAsync(Server, fromMinutes: 40, toMinutes: 300);
        await Players.StartAsync(agent, minor, minutes: 30);
        try
        {
            var owner = await LoginAsync(Server, OwnerPin);
            var seat = (await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", owner)).GetProperty("seats").EnumerateArray()
                .Single(s => s.GetProperty("pc").GetProperty("id").GetGuid() == agent.PcId);
            Assert.True(seat.GetProperty("user").GetProperty("minor").GetBoolean());
            Assert.False(seat.GetProperty("curfew").GetProperty("ended").GetBoolean());
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    /// <summary>The next whole minute: the forbidden hours are set in whole minutes around now.</summary>
    private void AlignToMinute()
    {
        var now = Server.Clock.GetUtcNow();
        Server.Clock.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(now.Ticks % TimeSpan.TicksPerMinute));
    }

    private async Task AdvanceAsync(TimeSpan by, params TestAgent[] agents)
    {
        Server.Clock.Advance(by);
        foreach (var agent in agents)
        {
            await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        }

        await Tick.RunOnceAsync();
    }

    private async Task GiveBonusAsync(Guid userId, long amount)
    {
        await using var c = await Server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await Ledger.PostAsync(c, tx, userId, allowOverdraft: false, Server.Clock.GetUtcNow(), new LedgerLine("bonus", amount, "Бонус: тест"));
        await tx.CommitAsync();
    }

    private async Task<int[]> WarnedAsync(Guid id) =>
        JsonSerializer.Deserialize<int[]>(await Players.ScalarAsync<string>(Server,
            "SELECT coalesce(jsonb_agg((data ->> 'minutes')::int ORDER BY id), '[]')::text FROM session_events WHERE session_id = @id AND type = 'curfewWarning'", new { id }))!;

    private Task<string> StateAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id });
}
