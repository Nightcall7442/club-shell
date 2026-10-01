using System.Text.Json;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Postpaid for guests, the club's choice (<c>settings.limits.guestPostpaid</c>, beyond the contract): off by default
/// (<c>PurchaseRulesTests.Guest_may_not_play_postpaid</c>); on, a guest with an empty wallet plays and the bill becomes a
/// guest debt on the counter (<c>adminOverview.guestDebts</c>) that a cash top-up clears; <c>limits.guestDebtLimit</c>
/// stops the session at the minute boundary like D-10 does for members. Standard costs 20 000 tiyin a minute.
/// </summary>
public sealed class GuestPostpaidTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private SessionTickWorker Tick => Server.Services.GetRequiredService<SessionTickWorker>();

    [Fact]
    public async Task A_guest_plays_postpaid_and_the_bill_is_taken_at_the_counter()
    {
        await LimitsAsync("""{"minorAge":18,"minorCurfew":"22:00","guestPostpaid":true}""");
        try
        {
            var (agent, guest) = await GuestAsync();
            var (status, session) = await Players.StartAsync(agent, guest, prepaid: false);
            Assert.True(status == 201, $"expected 201, got {status}: {session}");

            // No limit set: the tick does not stop a guest whose bill already exceeds the (empty) wallet.
            await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(10));
            var id = session.GetProperty("id").GetGuid();
            Assert.Equal("active", await StateAsync(id));

            var owner = await LoginAsync(Server, OwnerPin);
            var settled = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
            Assert.Equal(200_000, settled.GetProperty("charged").GetProperty("amount").GetInt64());
            Assert.Equal(-200_000, await Players.BalanceAsync(Server, guest.Id));

            var debt = Assert.Single(await DebtsAsync(owner), d => d.GetProperty("userId").GetGuid() == guest.Id);
            Assert.Equal(200_000, debt.GetProperty("debt").GetProperty("amount").GetInt64());
            Assert.False(string.IsNullOrEmpty(debt.GetProperty("pc").GetString()));

            await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = guest.Id, amount = 200_000, method = "cash" });
            Assert.Equal(0, await Players.BalanceAsync(Server, guest.Id));
            Assert.DoesNotContain(await DebtsAsync(owner), d => d.GetProperty("userId").GetGuid() == guest.Id);
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    /// <summary>A 50 000 limit pays 2 minutes (40 000); the 3rd (60 000) is not started, as D-10 for a member's balance.</summary>
    [Fact]
    public async Task The_guest_debt_limit_stops_the_session_at_the_last_minute_it_covers()
    {
        await LimitsAsync("""{"minorAge":18,"minorCurfew":"22:00","guestPostpaid":true,"guestDebtLimit":50000}""");
        try
        {
            var (agent, guest) = await GuestAsync();
            var (_, session) = await Players.StartAsync(agent, guest, prepaid: false);
            var id = session.GetProperty("id").GetGuid();

            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(119));
            Assert.Equal("active", await StateAsync(id));

            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
            Assert.Equal("ended", await StateAsync(id));
            Assert.Equal(-40_000, await Players.BalanceAsync(Server, guest.Id));

            // A limit below the first minute refuses the start, as an empty wallet does for a member (402).
            await LimitsAsync("""{"minorAge":18,"minorCurfew":"22:00","guestPostpaid":true,"guestDebtLimit":10000}""");
            var (second, secondGuest) = await GuestAsync();
            var (status, body) = await Players.StartAsync(second, secondGuest, prepaid: false);
            Assert.Equal(402, status);
            Contract.AssertError(body, "insufficientFunds");
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task The_owner_turns_it_on_in_the_club_settings()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        try
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", guestPostpaid = true, guestDebtLimit = 5_000_000 } });
            var limits = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("limits");
            Assert.True(limits.GetProperty("guestPostpaid").GetBoolean());
            Assert.Equal(5_000_000, limits.GetProperty("guestDebtLimit").GetInt64());

            var (agent, guest) = await GuestAsync();
            var (status, _) = await Players.StartAsync(agent, guest, prepaid: false);
            Assert.Equal(201, status);
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    private Task LimitsAsync(string json) =>
        Players.ExecuteAsync(Server, "UPDATE clubs SET settings = jsonb_set(settings, '{limits}', @json::jsonb)", new { json });

    private async Task<(TestAgent Agent, TestPlayer Guest)> GuestAsync()
    {
        var agent = await TestAgent.CreateAsync(Server);
        using var login = await agent.PostAsync("/api/v1/auth/guest", new { pcId = agent.PcId, hwid = agent.Hwid });
        var guest = await Players.ReadAsync(login, 200);
        agent.UserToken = guest.GetProperty("accessToken").GetString();
        return (agent, new TestPlayer(guest.GetProperty("user").GetProperty("id").GetGuid(), ""));
    }

    private async Task<List<JsonElement>> DebtsAsync(string token) =>
        (await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", token)).GetProperty("guestDebts").EnumerateArray().ToList();

    private async Task AdvanceOnlineAsync(TestAgent agent, TimeSpan by)
    {
        Server.Clock.Advance(by);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Tick.RunOnceAsync();
    }

    private Task<string> StateAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id });
}
