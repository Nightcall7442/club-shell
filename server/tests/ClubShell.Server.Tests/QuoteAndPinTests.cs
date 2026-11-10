using System.Text.Json;
using static ClubShell.Server.Tests.PurchaseRulesTests;

namespace ClubShell.Server.Tests;

/// <summary>
/// Two player routes beyond the contract (SERVER_API.md §4.4, §4.5): <c>POST /sessions/quote</c> — the price
/// <c>POST /sessions</c> would charge, with the club's day rate and the player's discount, nothing created — and
/// <c>POST /users/{userId}/pin/verify</c> — the unlock PIN checked on the server under the password's lockout, and
/// <c>pinSet</c> in <c>User.flags</c>. Standard costs 1 200 000 tiyin an hour, 30..720 minutes; the seed group
/// <c>staff</c> has 50 % off.
/// </summary>
public sealed class QuoteAndPinTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task The_quote_is_what_create_charges_with_the_day_rate_and_a_discount()
    {
        await Players.ExecuteAsync(Server,
            """UPDATE clubs SET settings = settings || '{"pricing":{"weekdayPct":[150,150,150,150,150,150,150],"holidays":[],"holidayPct":100}}'""");
        try
        {
            var (agent, player) = await Players.SignedInAsync(Server);
            await Players.ProfileAsync(Server, player, groupId: "staff");
            var sessions = await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM sessions WHERE user_id = @Id", new { player.Id });

            var (status, quote) = await agent.RawPostAsync("/api/v1/sessions/quote", new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 90 });
            Assert.True(status == 200, $"expected 200, got {status}: {quote}");
            Assert.Equal((90, 1_800_000L, 1_350_000L, 150, 50, "group", false), (quote.GetProperty("minutes").GetInt32(), Amount(quote.GetProperty("base")),
                Amount(quote.GetProperty("total")), quote.GetProperty("dayPct").GetInt32(), quote.GetProperty("discountPct").GetInt32(),
                quote.GetProperty("discountReason").GetString(), quote.GetProperty("package").GetBoolean()));
            Assert.Equal("UZS", quote.GetProperty("total").GetProperty("currency").GetString());

            // Nothing created or charged.
            Assert.Equal(sessions, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM sessions WHERE user_id = @Id", new { player.Id }));
            Assert.Equal(10_000_000, await Players.BalanceAsync(Server, player.Id));

            // The start charges exactly that.
            var (created, session) = await Players.StartAsync(agent, player, minutes: 90);
            Assert.Equal(201, created);
            Assert.Equal(1_350_000, Amount(session.GetProperty("cost")));
            Assert.Equal(10_000_000 - 1_350_000, await Players.BalanceAsync(Server, player.Id));

            // Without minutes: one hour.
            var (_, hour) = await agent.RawPostAsync("/api/v1/sessions/quote", new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard });
            Assert.Equal((60, 900_000L), (hour.GetProperty("minutes").GetInt32(), Amount(hour.GetProperty("total"))));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'pricing'");
        }
    }

    [Fact]
    public async Task The_quote_has_the_refusals_of_create_and_is_the_signed_in_players_alone()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        object Body(Guid? userId = null, Guid? pcId = null, Guid? tariffId = null, int? minutes = 60) =>
            new { pcId = pcId ?? agent.PcId, userId = userId ?? player.Id, tariffId = tariffId ?? Players.Standard, minutes };

        // VIP sells only in the VIP zone: as create, 403 policyDenied tariffZone.
        var (zone, zoneBody) = await agent.RawPostAsync("/api/v1/sessions/quote", Body(tariffId: Players.Vip));
        Assert.Equal((403, "tariffZone"), (zone, Details(zoneBody).GetProperty("rule").GetString()));
        var (start, startBody) = await Players.StartAsync(agent, player, tariff: Players.Vip);
        Assert.Equal((403, "tariffZone"), (start, Details(startBody).GetProperty("rule").GetString()));

        var (few, fewBody) = await agent.RawPostAsync("/api/v1/sessions/quote", Body(minutes: 10));
        Assert.Equal((400, "minutes", "min"), (few, Details(fewBody).GetProperty("field").GetString(), Details(fewBody).GetProperty("reason").GetString()));
        Assert.Equal(404, (await agent.RawPostAsync("/api/v1/sessions/quote", Body(tariffId: Guid.NewGuid()))).Status);

        await Players.ProfileAsync(Server, player, blacklisted: true);
        var (banned, bannedBody) = await agent.RawPostAsync("/api/v1/sessions/quote", Body());
        Assert.Equal((403, "blacklisted"), (banned, Details(bannedBody).GetProperty("rule").GetString()));
        await Players.ProfileAsync(Server, player, blacklisted: false);

        var other = await Players.CreateAsync(Server);
        var (notOwner, notOwnerBody) = await agent.RawPostAsync("/api/v1/sessions/quote", Body(userId: other.Id));
        Assert.Equal((403, "notOwner"), (notOwner, Details(notOwnerBody).GetProperty("reason").GetString()));
        var (pc, pcBody) = await agent.RawPostAsync("/api/v1/sessions/quote", Body(pcId: Guid.NewGuid()));
        Assert.Equal((403, "pcMismatch"), (pc, Details(pcBody).GetProperty("reason").GetString()));
        var (missing, missingBody) = await agent.RawPostAsync("/api/v1/sessions/quote", new { userId = player.Id, tariffId = Players.Standard });
        Assert.Equal((400, "pcId", "required"), (missing, Details(missingBody).GetProperty("field").GetString(), Details(missingBody).GetProperty("reason").GetString()));

        agent.UserToken = null;
        var (anonymous, anonymousBody) = await agent.RawPostAsync("/api/v1/sessions/quote", Body());
        Assert.Equal((401, "userToken"), (anonymous, Details(anonymousBody).GetProperty("reason").GetString()));
        Assert.Equal(10_000_000, await Players.BalanceAsync(Server, player.Id));
    }

    [Fact]
    public async Task The_pin_is_verified_on_the_server_204_401_404_and_shown_as_pinSet()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var path = $"/api/v1/users/{player.Id}/pin/verify";

        var (none, noneBody) = await agent.RawPostAsync(path, new { pin = "1234" });
        Assert.Equal((404, "notFound", "noPin"), (none, noneBody.GetProperty("error").GetProperty("code").GetString(), Details(noneBody).GetProperty("reason").GetString()));
        Assert.DoesNotContain("pinSet", await FlagsAsync(agent, player));

        // Set on this PC (or any): pinSet in User.flags, and verified here.
        using (var set = await agent.SendAsync(HttpMethod.Patch, $"/api/v1/users/{player.Id}", """{"pin":"1234"}"""))
        {
            Assert.Contains("pinSet", (await Players.ReadAsync(set, 200)).GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        }

        Assert.Contains("pinSet", await FlagsAsync(agent, player));
        Assert.Equal(204, (await agent.RawPostAsync(path, new { pin = "1234" })).Status);

        var (wrong, wrongBody) = await agent.RawPostAsync(path, new { pin = "9999" });
        Assert.Equal((401, "unauthorized", "badSecret", 4), (wrong, wrongBody.GetProperty("error").GetProperty("code").GetString(),
            Details(wrongBody).GetProperty("reason").GetString(), Details(wrongBody).GetProperty("attemptsLeft").GetInt32()));

        // A right PIN clears the count, as a right password does; a malformed one is 400 and not counted.
        Assert.Equal(204, (await agent.RawPostAsync(path, new { pin = "1234" })).Status);
        var (malformed, malformedBody) = await agent.RawPostAsync(path, new { pin = "12a4" });
        Assert.Equal((400, "pin", "format"), (malformed, Details(malformedBody).GetProperty("field").GetString(), Details(malformedBody).GetProperty("reason").GetString()));
        Assert.Equal(4, Details((await agent.RawPostAsync(path, new { pin = "0000" })).Body).GetProperty("attemptsLeft").GetInt32());

        // Another player's PIN is not this token's to try.
        var other = await Players.CreateAsync(Server);
        var (notOwner, notOwnerBody) = await agent.RawPostAsync($"/api/v1/users/{other.Id}/pin/verify", new { pin = "1234" });
        Assert.Equal((403, "notOwner"), (notOwner, Details(notOwnerBody).GetProperty("reason").GetString()));
    }

    /// <summary>Wrong PINs and wrong passwords share the lockout of the player's name: 5 within 15 min close both, with sign-in's refusal.</summary>
    [Fact]
    public async Task Wrong_pins_count_toward_the_sign_in_lockout()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var path = $"/api/v1/users/{player.Id}/pin/verify";
        await Players.ReadAsync(await agent.SendAsync(HttpMethod.Patch, $"/api/v1/users/{player.Id}", """{"pin":"4321"}"""), 200);

        // Two wrong passwords on another PC, then three wrong PINs: 5 failures of the name.
        var other = await TestAgent.CreateAsync(Server);
        for (var i = 0; i < 2; i++)
        {
            await Contract.ReadErrorAsync(await other.PostAsync("/api/v1/auth/login",
                new { kind = "password", username = player.Username, password = "wrong", pcId = other.PcId, hwid = other.Hwid }), 401, "unauthorized", "badCredentials");
        }

        int[] left = [2, 1, 0];
        foreach (var expected in left)
        {
            var (status, body) = await agent.RawPostAsync(path, new { pin = "1111" });
            Assert.Equal((401, "badSecret", expected), (status, Details(body).GetProperty("reason").GetString(), Details(body).GetProperty("attemptsLeft").GetInt32()));
        }

        // Locked: the right PIN and the right password get sign-in's refusal.
        var (locked, lockedBody) = await agent.RawPostAsync(path, new { pin = "4321" });
        Assert.Equal((401, "badCredentials", 0), (locked, Details(lockedBody).GetProperty("reason").GetString(), Details(lockedBody).GetProperty("attemptsLeft").GetInt32()));
        var signIn = await Contract.ReadErrorAsync(await other.PostAsync("/api/v1/auth/login",
            new { kind = "password", username = player.Username, password = Players.Password, pcId = other.PcId, hwid = other.Hwid }), 401, "unauthorized", "badCredentials");
        Assert.Equal(0, Details(signIn).GetProperty("attemptsLeft").GetInt32());

        // After the window: the right PIN again.
        Server.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(204, (await agent.RawPostAsync(path, new { pin = "4321" })).Status);
    }

    private static async Task<IEnumerable<string?>> FlagsAsync(TestAgent agent, TestPlayer player)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}");
        return (await Players.ReadAsync(response, 200)).GetProperty("flags").EnumerateArray().Select(f => f.GetString()).ToList();
    }

    private static long Amount(JsonElement money) => money.GetProperty("amount").GetInt64();
}
