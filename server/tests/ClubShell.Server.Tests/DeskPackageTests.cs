using ClubShell.Server.Infrastructure;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Packages at the desk (cash desk part 2, D-38, D-39): <c>adminQuote</c> names the tariff's own refusal (<c>rule</c>) and the
/// minutes the price is for, so the desk shows a package outside its window disabled; a package sells its own minutes
/// whatever <c>minutes</c> says, on open and on extend (the journal shows the minutes actually bought); an extend with an
/// hourly tariff switches the session to it, and an early end refunds only that hourly part (a package is never refunded).
/// </summary>
public sealed class DeskPackageTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task A_package_sells_its_minutes_and_the_quote_names_the_rule_outside_its_window()
    {
        // Local noon of tomorrow: the seeded Night Pack (22:00–08:00) is out of its window.
        const string zone = "Asia/Tashkent";
        var start = Server.Clock.GetUtcNow();
        var tomorrow = DateOnly.FromDateTime(ClubTime.Local(start, zone)).AddDays(1);
        Server.Clock.Advance(ClubTime.Utc(tomorrow.ToDateTime(new TimeOnly(12, 0)), zone) - start);

        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        var member = await Players.CreateAsync(Server, balance: 20_000_000);

        var night = await ExpectAsync(Server, 200, HttpMethod.Post, "/quote", cashier, new { tariffId = Players.NightPack, pcId = agent.PcId, userId = member.Id });
        Assert.Equal(("tariffTime", 300, 4_000_000L), (night.GetProperty("rule").GetString(), night.GetProperty("minutes").GetInt32(), Amount(night.GetProperty("total"))));
        Assert.Equal("tariffTime", (await ExpectAsync(Server, 403, HttpMethod.Post, "/sessions", cashier,
            new { pcId = agent.PcId, userId = member.Id, tariffId = Players.NightPack, minutes = 60 })).GetProperty("error").GetProperty("details").GetProperty("rule").GetString());
        var hourly = await ExpectAsync(Server, 200, HttpMethod.Post, "/quote", cashier, new { tariffId = Players.Standard, pcId = agent.PcId, minutes = 90 });
        Assert.Equal(((string?)null, 90), (hourly.GetProperty("rule").GetString(), hourly.GetProperty("minutes").GetInt32()));

        // A package without windows: no rule, its own minutes.
        var package = (await ExpectAsync(Server, 200, HttpMethod.Post, "/tariffs", owner, new
        {
            name = "Пакет 3ч", pricePerHour = 0, minMinutes = 30, maxMinutes = (int?)null, zones = Array.Empty<string>(), timeWindows = Array.Empty<object>(),
            isPackage = true, packageMinutes = 180, packagePrice = 2_500_000,
        })).GetProperty("tariff").GetProperty("id").GetGuid();
        var quote = await ExpectAsync(Server, 200, HttpMethod.Post, "/quote", cashier, new { tariffId = package, pcId = agent.PcId, userId = member.Id });
        Assert.Equal(((string?)null, 180, 2_500_000L), (quote.GetProperty("rule").GetString(), quote.GetProperty("minutes").GetInt32(), Amount(quote.GetProperty("total"))));

        // Open and extend send minutes 60 (the contract requires it); the package's 180 are sold each time.
        var opened = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = member.Id, tariffId = package, minutes = 60 });
        Assert.Equal((10_800, 2_500_000L), (opened.GetProperty("session").GetProperty("secondsLeft").GetInt32(), Amount(opened.GetProperty("charged"))));
        var id = opened.GetProperty("session").GetProperty("id").GetGuid();
        var more = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/extend", cashier, new { sessionId = id, minutes = 60 });
        Assert.Equal((21_600, 2_500_000L), (more.GetProperty("session").GetProperty("secondsLeft").GetInt32(), Amount(more.GetProperty("charged"))));
        Assert.Equal("+180 180", await Players.ScalarAsync<string>(Server,
            "SELECT detail || ' ' || (meta ->> 'minutes') FROM audit_entries WHERE action = 'sessionExtend' AND meta ->> 'sessionId' = @id", new { id = id.ToString() }));

        // An hourly extension switches the tariff; ended at once, only its 30 minutes come back.
        var switched = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/extend", cashier, new { sessionId = id, minutes = 30, tariffId = Players.Standard });
        Assert.Equal((Players.Standard, 23_400, 600_000L), (switched.GetProperty("session").GetProperty("tariffId").GetGuid(),
            switched.GetProperty("session").GetProperty("secondsLeft").GetInt32(), Amount(switched.GetProperty("charged"))));
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { sessionId = id });
        Assert.Equal(600_000, Amount(ended.GetProperty("refunded")));
        Assert.Equal(20_000_000 - 5_000_000, Amount(ended.GetProperty("balance")));
    }
}
