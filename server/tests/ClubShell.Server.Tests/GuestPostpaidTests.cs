using System.Text.Json;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Postpaid for guests (<c>settings.limits.guestPostpaid</c>, beyond the contract): the kiosk never starts a guest's postpaid
/// session (<c>403 policyDenied postpaidNotAllowed</c>, whatever account the guest button made and whatever the club set);
/// the desk seats a guest postpaid unless the owner turned it off, with no cap by default — a guest with an empty wallet
/// plays and the bill becomes a guest debt on the counter (<c>adminOverview.guestDebts</c>) that a cash top-up clears;
/// <c>limits.guestDebtLimit</c> caps a desk seat at the minute boundary like D-10 does. Standard costs 20 000 tiyin a minute.
/// </summary>
public sealed class GuestPostpaidTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private SessionTickWorker Tick => Server.Services.GetRequiredService<SessionTickWorker>();

    [Fact]
    public async Task A_guest_on_the_kiosk_may_not_start_postpaid_whatever_the_club_allows_the_desk()
    {
        await LimitsAsync("""{"minorAge":18,"minorCurfew":"22:00","guestPostpaid":true}""");
        try
        {
            // A new guest account per press changes nothing: each one is refused.
            for (var i = 0; i < 2; i++)
            {
                var (agent, guest) = await GuestAsync();
                var (status, body) = await Players.StartAsync(agent, guest, prepaid: false);
                Assert.True(status == 403, $"expected 403, got {status}: {body}");
                Contract.AssertError(body, "policyDenied", "postpaidNotAllowed");
                Assert.Equal("postpaidNotAllowed", body.GetProperty("error").GetProperty("details").GetProperty("rule").GetString());

                // Prepaid from the guest's balance is the kiosk's way (here: none, so 402).
                Assert.Equal(402, (await Players.StartAsync(agent, guest)).Status);
            }
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task A_desk_guest_plays_postpaid_by_default_and_the_bill_is_taken_at_the_counter()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var agent = await Seats.OnlineAsync(Server);
        var opened = await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions/guest", owner, Seat(agent.PcId), Guid.NewGuid());
        var id = opened.GetProperty("session").GetProperty("id").GetGuid();
        var guestId = opened.GetProperty("user").GetProperty("id").GetGuid();

        // No limit set: the tick does not stop a guest whose bill already exceeds the (empty) wallet.
        await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(10));
        Assert.Equal("active", await StateAsync(id));

        var settled = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
        Assert.Equal(200_000, settled.GetProperty("charged").GetProperty("amount").GetInt64());
        Assert.Equal(-200_000, await Players.BalanceAsync(Server, guestId));

        var debt = Assert.Single(await DebtsAsync(owner), d => d.GetProperty("userId").GetGuid() == guestId);
        Assert.Equal(200_000, debt.GetProperty("debt").GetProperty("amount").GetInt64());
        Assert.False(string.IsNullOrEmpty(debt.GetProperty("pc").GetString()));

        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = guestId, amount = 200_000, method = "cash" });
        Assert.Equal(0, await Players.BalanceAsync(Server, guestId));
        Assert.DoesNotContain(await DebtsAsync(owner), d => d.GetProperty("userId").GetGuid() == guestId);
    }

    /// <summary>A 50 000 limit pays 2 minutes (40 000); the 3rd (60 000) is not started, as D-10 for a member's balance.</summary>
    [Fact]
    public async Task The_guest_debt_limit_stops_a_desk_seat_at_the_last_minute_it_covers()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        await LimitsAsync("""{"minorAge":18,"minorCurfew":"22:00","guestDebtLimit":50000}""");
        try
        {
            var agent = await Seats.OnlineAsync(Server);
            var opened = await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions/guest", owner, Seat(agent.PcId), Guid.NewGuid());
            var id = opened.GetProperty("session").GetProperty("id").GetGuid();
            Assert.Equal(50_000, await Players.ScalarAsync<long>(Server, "SELECT debt_limit FROM sessions WHERE id = @id", new { id }));

            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(119));
            Assert.Equal("active", await StateAsync(id));

            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
            Assert.Equal("ended", await StateAsync(id));
            Assert.Equal(-40_000, await Players.BalanceAsync(Server, opened.GetProperty("user").GetProperty("id").GetGuid()));

            // A limit below the first minute refuses the seat (402), and leaves no guest account behind.
            await LimitsAsync("""{"minorAge":18,"minorCurfew":"22:00","guestDebtLimit":10000}""");
            var second = await Seats.OnlineAsync(Server);
            Contract.AssertError(await RawExpectAsync(Server, 402, HttpMethod.Post, "/sessions/guest", owner, Seat(second.PcId), Guid.NewGuid()), "insufficientFunds");
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task The_owner_turns_it_off_in_the_club_settings()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        try
        {
            // Unset, the console reads it as on: what the server applies.
            Assert.True((await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("limits").GetProperty("guestPostpaid").GetBoolean());

            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", guestPostpaid = false, guestDebtLimit = 5_000_000 } });
            var limits = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("limits");
            Assert.False(limits.GetProperty("guestPostpaid").GetBoolean());
            Assert.Equal(5_000_000, limits.GetProperty("guestDebtLimit").GetInt64());

            var agent = await Seats.OnlineAsync(Server);
            var refused = await RawExpectAsync(Server, 403, HttpMethod.Post, "/sessions/guest", owner, Seat(agent.PcId), Guid.NewGuid());
            Contract.AssertError(refused, "policyDenied", "postpaidNotAllowed");

            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", guestPostpaid = true, guestDebtLimit = 5_000_000 } });
            var opened = await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions/guest", owner, Seat(agent.PcId), Guid.NewGuid());
            Assert.Equal(5_000_000, await Players.ScalarAsync<long>(Server, "SELECT debt_limit FROM sessions WHERE id = @id",
                new { id = opened.GetProperty("session").GetProperty("id").GetGuid() }));
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId });
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    private static object Seat(Guid pcId) => new { pcId, tariffId = Players.Standard, minutes = 60, prepaid = false };

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
