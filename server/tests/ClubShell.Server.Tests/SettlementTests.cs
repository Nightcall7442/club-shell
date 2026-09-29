using System.Text.Json;
using static ClubShell.Server.Tests.PurchaseRulesTests;

namespace ClubShell.Server.Tests;

/// <summary>
/// Settlement on <c>POST /sessions/{id}/end</c> (DESIGN §5.7): the refund is pro rata to what was actually paid (a
/// discounted session gets its discounted money back, not the base price), only for admin/error on an hourly tariff;
/// postpaid pays <c>ceil(used / 60)</c> minutes at the frozen price; a repeated end is 409 with <c>details.session</c>.
/// </summary>
public sealed class SettlementTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task Admin_end_refunds_the_unused_part_of_what_was_paid()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        await Players.ProfileAsync(Server, player, groupId: "staff"); // seed group: -50 %
        var (_, created) = await Players.StartAsync(agent, player);
        Assert.Equal(600_000, created.GetProperty("cost").GetProperty("amount").GetInt64());

        Server.Clock.Advance(TimeSpan.FromMinutes(15));
        var result = await EndAsync(agent, created, "admin");
        Assert.Equal(450_000, result.GetProperty("refunded").GetProperty("amount").GetInt64());
        Assert.Equal(0, result.GetProperty("charged").GetProperty("amount").GetInt64());
        var session = result.GetProperty("session");
        Assert.Equal(("ended", 900, 150_000L), (session.GetProperty("state").GetString(), session.GetProperty("secondsUsed").GetInt32(), session.GetProperty("cost").GetProperty("amount").GetInt64()));
        Assert.Equal(10_000_000 - 150_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(150_000, await Players.ScalarAsync<long>(Server, "SELECT lifetime_spent FROM wallets WHERE user_id = @Id", new { player.Id }));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("timeUp")]
    [InlineData("idle")]
    public async Task Other_reasons_refund_nothing(string reason)
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        Server.Clock.Advance(TimeSpan.FromMinutes(5));
        var result = await EndAsync(agent, created, reason);
        Assert.Equal(0, result.GetProperty("refunded").GetProperty("amount").GetInt64());
        Assert.Equal(reason, await Players.ScalarAsync<string>(Server, "SELECT end_reason FROM sessions WHERE id = @id", new { id = created.GetProperty("id").GetGuid() }));
    }

    [Fact]
    public async Task Package_is_never_refunded()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var package = Guid.NewGuid();
        await Players.ExecuteAsync(Server,
            "INSERT INTO tariffs (id, club_id, name, price_per_hour, min_minutes, max_minutes, is_package, package_minutes, package_price) SELECT @package, id, 'Pack 2h', 600000, 120, 120, true, 120, 1000000 FROM clubs",
            new { package });
        var (status, created) = await Players.StartAsync(agent, player, tariff: package, minutes: null);
        Assert.Equal(201, status);
        Assert.Equal(7200, created.GetProperty("secondsLeft").GetInt32());
        var result = await EndAsync(agent, created, "admin");
        Assert.Equal(0, result.GetProperty("refunded").GetProperty("amount").GetInt64());
    }

    /// <summary>
    /// Refundability is per purchase, not by the last tariff: the unused time is the last bought. Package 300 min
    /// (4 000 000) + Standard 60 min (1 200 000), admin end at 600 s: the unused hour is refunded, the package is not.
    /// </summary>
    [Theory]
    [InlineData(true, 1_200_000)]
    [InlineData(false, 1_000_000)] // Standard first, then the package: 3 000 of its 3 600 s unused
    public async Task Admin_end_refunds_only_the_unused_hourly_purchases(bool packageFirst, long refund)
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var package = Guid.NewGuid();
        await Players.ExecuteAsync(Server,
            "INSERT INTO tariffs (id, club_id, name, price_per_hour, min_minutes, max_minutes, is_package, package_minutes, package_price) SELECT @package, id, 'Pack 5h', 800000, 300, 300, true, 300, 4000000 FROM clubs",
            new { package });
        var (_, created) = packageFirst ? await Players.StartAsync(agent, player, tariff: package, minutes: null) : await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        Server.Clock.Advance(TimeSpan.FromSeconds(1)); // purchases are ordered by time
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/extend", new { minutes = 60, tariffId = packageFirst ? Players.Standard : package }, Guid.NewGuid()), 200);
        Assert.Equal(10_000_000 - 5_200_000, await Players.BalanceAsync(Server, player.Id));

        Server.Clock.Advance(TimeSpan.FromSeconds(599));
        var result = await EndAsync(agent, created, "admin");
        Assert.Equal(refund, result.GetProperty("refunded").GetProperty("amount").GetInt64());
        Assert.Equal(10_000_000 - 5_200_000 + refund, await Players.BalanceAsync(Server, player.Id));
    }

    [Fact]
    public async Task Postpaid_pays_ceiled_minutes_at_the_frozen_price_with_overdraft()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 20_000);
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        Server.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(40_000, (await CurrentCostAsync(agent)));

        // The club raises the price mid-session: the session keeps the price it started with.
        await Players.ExecuteAsync(Server, "UPDATE tariffs SET price_per_hour = 9900000 WHERE id = @Standard", new { Players.Standard });
        try
        {
            var result = await EndAsync(agent, created, "user");
            Assert.Equal(40_000, result.GetProperty("charged").GetProperty("amount").GetInt64());
            Assert.Equal(-20_000, await Players.BalanceAsync(Server, player.Id));
            Assert.True(await Players.ScalarAsync<bool>(Server, "SELECT overdraft FROM ledger_entries WHERE session_id = @id", new { id = created.GetProperty("id").GetGuid() }));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE tariffs SET price_per_hour = 1200000 WHERE id = @Standard", new { Players.Standard });
        }
    }

    [Fact]
    public async Task Repeated_end_is_409_session_not_active_with_the_session()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        await EndAsync(agent, created, "user");
        using var again = await agent.PostAsync($"/api/v1/sessions/{created.GetProperty("id").GetGuid()}/end", new { reason = "user", secondsUsed = 1 });
        var error = await Contract.ReadErrorAsync(again, 409, "sessionNotActive");
        Assert.Equal("ended", Details(error).GetProperty("session").GetProperty("state").GetString());
        Contract.AssertMatches("Session", Details(error).GetProperty("session"));
    }

    [Fact]
    public async Task End_of_another_pcs_session_is_403_and_of_an_unknown_one_404()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var stranger = await TestAgent.CreateAsync(Server);
        using (var foreign = await stranger.PostAsync($"/api/v1/sessions/{created.GetProperty("id").GetGuid()}/end", new { reason = "user", secondsUsed = 1 }))
        {
            await Contract.ReadErrorAsync(foreign, 403, "forbidden", "pcMismatch");
        }

        using (var unknown = await stranger.PostAsync($"/api/v1/sessions/{Guid.NewGuid()}/end", new { reason = "user", secondsUsed = 1 }))
        {
            await Contract.ReadErrorAsync(unknown, 404, "notFound");
        }

        using var bad = await agent.PostAsync($"/api/v1/sessions/{created.GetProperty("id").GetGuid()}/end", new { reason = "bogus", secondsUsed = 1 });
        await Contract.ReadErrorAsync(bad, 400, "validation");
    }

    private async Task<JsonElement> EndAsync(TestAgent agent, JsonElement session, string reason)
    {
        using var response = await agent.PostAsync($"/api/v1/sessions/{session.GetProperty("id").GetGuid()}/end",
            new { reason, secondsUsed = 1, endedAt = Server.Clock.GetUtcNow() });
        return await Players.ReadAsync(response, 200);
    }

    private static async Task<long> CurrentCostAsync(TestAgent agent)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/sessions/current?pcId={agent.PcId}");
        return (await Players.ReadAsync(response, 200)).GetProperty("cost").GetProperty("amount").GetInt64();
    }
}
