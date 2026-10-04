using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Walk-in guests at the desk (cash desk part 2, D-24..D-26, D-36, D-37, D-48): <c>POST /admin/sessions/guest</c> creates the
/// transient account and its seat in one transaction with one exact payment and no bonus; a refusal leaves no account,
/// money or journal entry. «Гость» on that PC signs in to the desk's session (even with kiosk guests off), but never takes
/// over a kiosk guest's or a member's session (<c>403 pcOccupied</c>). An early desk end gives back only the unused time,
/// and only what the guest paid in cash leaves the drawer (<c>POST /admin/wallet/payout</c>). Standard costs 1 200 000
/// tiyin an hour; the seeded bonus tiers start at 5 000 000.
/// </summary>
public sealed class DeskGuestTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task A_walk_in_guest_is_seated_prepaid_with_one_exact_payment_and_the_guest_button_signs_in_to_that_session()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        var users = await CountAsync("users");

        // 300 minutes cost 6 000 000: a member would get the 5 % tier bonus, a guest gets none.
        var price = Amount((await ExpectAsync(Server, 200, HttpMethod.Post, "/quote", cashier, new { tariffId = Players.Standard, pcId = agent.PcId, minutes = 300 })).GetProperty("total"));
        Assert.Equal(6_000_000, price);
        var opened = await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions/guest", cashier,
            new { pcId = agent.PcId, tariffId = Players.Standard, minutes = 300, payment = new { amount = price, method = "cash" } }, Guid.NewGuid());
        var guest = opened.GetProperty("user");
        var guestId = guest.GetProperty("id").GetGuid();
        var sessionId = opened.GetProperty("session").GetProperty("id").GetGuid();
        var number = await Players.ScalarAsync<int>(Server, "SELECT number FROM pcs WHERE id = @PcId", new { agent.PcId });
        Assert.Equal(("guest", $"Гость {number}"), (guest.GetProperty("role").GetString(), guest.GetProperty("displayName").GetString()));
        Assert.Equal((price, 0L, 0L, 18_000), (Amount(opened.GetProperty("charged")), Amount(opened.GetProperty("balance")),
            Amount(opened.GetProperty("payment").GetProperty("bonus")), opened.GetProperty("session").GetProperty("secondsLeft").GetInt32()));
        Assert.Equal((price, "topUp"), (Amount(opened.GetProperty("payment").GetProperty("transaction").GetProperty("amount")),
            opened.GetProperty("payment").GetProperty("transaction").GetProperty("type").GetString()));
        Assert.Equal(users + 1, await CountAsync("users"));
        Assert.Equal("topUp:6000000 charge:-6000000", await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(type || ':' || amount, ' ' ORDER BY created_at, type DESC) FROM ledger_entries WHERE user_id = @guestId", new { guestId }));
        Assert.Equal("cashier:true", await Players.ScalarAsync<string>(Server,
            "SELECT s.origin || ':' || u.transient FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.id = @sessionId", new { sessionId }));

        // One journal row for the seat with the payment and the price; its top-up is marked for the session.
        var meta = JsonElement.Parse(await Players.ScalarAsync<string>(Server,
            "SELECT meta::text FROM audit_entries WHERE action = 'sessionOpen' AND user_id = @guestId", new { guestId }));
        Assert.Equal((price, "cash", true, 6_000_000L, 300), (meta.GetProperty("paidAmount").GetInt64(), meta.GetProperty("paidMethod").GetString(),
            meta.GetProperty("guest").GetBoolean(), meta.GetProperty("quote").GetProperty("base").GetInt64(), meta.GetProperty("minutes").GetInt32()));
        Assert.True(await Players.ScalarAsync<bool>(Server,
            "SELECT (meta ->> 'forSession')::boolean FROM audit_entries WHERE action = 'topUp' AND user_id = @guestId", new { guestId }));
        Assert.False(await SignedInAsync(cashier, agent.PcId));

        // «Гость» on that PC: the desk's guest and session, no second account.
        var auth = await DeskGuests.PressAsync(agent);
        Assert.Equal((guestId, sessionId), (auth.GetProperty("user").GetProperty("id").GetGuid(), auth.GetProperty("session").GetProperty("id").GetGuid()));
        Assert.Equal(users + 1, await CountAsync("users"));
        Assert.True(await SignedInAsync(cashier, agent.PcId));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
    }

    [Fact]
    public async Task The_guest_route_refuses_without_a_key_with_a_wrong_price_and_leaves_nothing()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        var busy = await TestAgent.CreateAsync(Server);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier,
            new { pcId = busy.PcId, userId = (await Players.CreateAsync(Server)).Id, tariffId = Players.Standard, minutes = 60 });
        var apiKey = await ApiKeyAsync(Server);
        var counts = await CountsAsync();
        object Seat(Guid pcId, long amount = 1_200_000, bool prepaid = true, object? payment = null, string? displayName = null) =>
            new { pcId, tariffId = Players.Standard, minutes = 60, prepaid, displayName, payment = payment ?? (prepaid ? new { amount, method = "cash" } : null) };

        Assert.Equal(("Idempotency-Key", "required"), Field(await RawExpectAsync(Server, 400, HttpMethod.Post, "/sessions/guest", cashier, Seat(agent.PcId))));
        var changed = await RawExpectAsync(Server, 409, HttpMethod.Post, "/sessions/guest", cashier, Seat(agent.PcId, amount: 1_000_000), Guid.NewGuid());
        Contract.AssertError(changed, "conflict", "priceChanged");
        Assert.Equal(1_200_000, Amount(changed.GetProperty("error").GetProperty("details").GetProperty("total")));
        Assert.Equal("conflict", (await RawExpectAsync(Server, 409, HttpMethod.Post, "/sessions/guest", cashier, Seat(agent.PcId, amount: 1_300_000), Guid.NewGuid()))
            .GetProperty("error").GetProperty("code").GetString());
        Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/sessions/guest", cashier, Seat(busy.PcId), Guid.NewGuid()), "sessionAlreadyActive");
        Contract.AssertError(await RawExpectAsync(Server, 403, HttpMethod.Post, "/sessions/guest", apiKey, Seat(agent.PcId), Guid.NewGuid()), "forbidden", "staffOnly");
        await ClearApiKeyAsync(Server);
        Assert.Equal(("payment", "required"), Field(await RawExpectAsync(Server, 400, HttpMethod.Post, "/sessions/guest", cashier,
            new { pcId = agent.PcId, tariffId = Players.Standard, minutes = 60 }, Guid.NewGuid())));
        Assert.Equal(("payment", "postpaid"), Field(await RawExpectAsync(Server, 400, HttpMethod.Post, "/sessions/guest", cashier,
            Seat(agent.PcId, prepaid: false, payment: new { amount = 1_200_000, method = "cash" }), Guid.NewGuid())));
        Assert.Equal(("displayName", "max"), Field(await RawExpectAsync(Server, 400, HttpMethod.Post, "/sessions/guest", cashier,
            Seat(agent.PcId, displayName: new string('x', 33)), Guid.NewGuid())));

        await Players.ExecuteAsync(Server, "UPDATE pcs SET maintenance = true WHERE id = @PcId", new { agent.PcId });
        var refused = await RawExpectAsync(Server, 403, HttpMethod.Post, "/sessions/guest", cashier, Seat(agent.PcId), Guid.NewGuid());
        Assert.Equal("pcMaintenance", refused.GetProperty("error").GetProperty("details").GetProperty("rule").GetString());
        await Players.ExecuteAsync(Server, "UPDATE pcs SET maintenance = false WHERE id = @PcId", new { agent.PcId });

        // A guest postpaid seat while the club does not allow it.
        Assert.Equal("postpaidNotAllowed", (await RawExpectAsync(Server, 403, HttpMethod.Post, "/sessions/guest", cashier, Seat(agent.PcId, prepaid: false), Guid.NewGuid()))
            .GetProperty("error").GetProperty("details").GetProperty("rule").GetString());

        // Nothing of the refused seats: no guest account, no ledger row, no journal entry.
        Assert.Equal(counts, await CountsAsync());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = busy.PcId });
    }

    [Fact]
    public async Task The_guest_button_on_a_pc_with_a_kiosk_guest_or_a_member_session_is_refused()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);

        // A kiosk guest topped up at the desk and playing on its own: another «Гость» press there does not take it over.
        var kiosk = await TestAgent.CreateAsync(Server);
        var kioskGuest = await DeskGuests.PressAsync(kiosk);
        var kioskGuestId = kioskGuest.GetProperty("user").GetProperty("id").GetGuid();
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = kioskGuestId, amount = 1_200_000, method = "cash" });
        var (status, _) = await Players.StartAsync(kiosk, new TestPlayer(kioskGuestId, ""));
        Assert.Equal(201, status);
        var member = await Players.CreateAsync(Server);
        var users = await CountAsync("users");
        Contract.AssertError(await DeskGuests.PressAsync(kiosk, 403), "forbidden", "pcOccupied");

        // A member seated at the desk.
        var seat = await TestAgent.CreateAsync(Server);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = seat.PcId, userId = member.Id, tariffId = Players.Standard, minutes = 60 });
        Contract.AssertError(await DeskGuests.PressAsync(seat, 403), "forbidden", "pcOccupied");
        Assert.Equal(users, await CountAsync("users"));
    }

    [Fact]
    public async Task An_early_desk_end_pays_out_only_the_refund_and_only_cash()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);

        // Paid 1 200 000 cash for an hour and ended at once: all of it is refunded and payable.
        var guestId = (await DeskGuests.SeatAsync(Server, cashier, agent.PcId)).GetProperty("user").GetProperty("id").GetGuid();
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        Assert.Equal((1_200_000L, 1_200_000L, 1_200_000L, "guest"), (Amount(ended.GetProperty("refunded")), Amount(ended.GetProperty("payable")),
            Amount(ended.GetProperty("balance")), ended.GetProperty("user").GetProperty("role").GetString()));
        var listed = Assert.Single(await RefundsAsync(cashier), r => r.GetProperty("userId").GetGuid() == guestId);
        Assert.Equal((1_200_000L, 1_200_000L), (Amount(listed.GetProperty("payable")), Amount(listed.GetProperty("balance"))));
        object Payout(Guid userId, long amount) => new { userId, amount, method = "cash" };

        Assert.Equal(("Idempotency-Key", "required"), Field(await RawExpectAsync(Server, 400, HttpMethod.Post, "/wallet/payout", cashier, Payout(guestId, 1_200_000))));
        var changed = await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/payout", cashier, Payout(guestId, 1_000_000), Guid.NewGuid());
        Contract.AssertError(changed, "conflict", "payableChanged");
        Assert.Equal(1_200_000, Amount(changed.GetProperty("error").GetProperty("details").GetProperty("payable")));
        var member = await Players.CreateAsync(Server);
        Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/payout", cashier, Payout(member.Id, 100_000), Guid.NewGuid()), "conflict", "notGuest");
        var playing = await TestAgent.CreateAsync(Server);
        var playingId = (await DeskGuests.SeatAsync(Server, cashier, playing.PcId)).GetProperty("user").GetProperty("id").GetGuid();
        Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/payout", cashier, Payout(playingId, 100_000), Guid.NewGuid()), "conflict", "guestPlaying");
        Contract.AssertError(await RawExpectAsync(Server, 403, HttpMethod.Post, "/wallet/payout", await ApiKeyAsync(Server), Payout(guestId, 1_200_000), Guid.NewGuid()),
            "forbidden", "staffOnly");
        await ClearApiKeyAsync(Server);

        // The drawer emptied by a cash-out: the payout waits for cash.
        var drawer = await ExpectedCashAsync(Server, cashier);
        await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = drawer, reasonCode = "collection" }, Guid.NewGuid());
        var shortBody = await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/payout", cashier, Payout(guestId, 1_200_000), Guid.NewGuid());
        Contract.AssertError(shortBody, "conflict", "cashShort");
        Assert.Equal(0, Amount(shortBody.GetProperty("error").GetProperty("details").GetProperty("available")));
        await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "in", amount = 1_500_000, reasonCode = "change" }, Guid.NewGuid());

        // The exact payout, then its replay: one ledger row, balance 0, the X report and the drawer follow.
        var key = Guid.NewGuid();
        var paid = await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/payout", cashier, Payout(guestId, 1_200_000), key);
        Assert.Equal((0L, 0L, -1_200_000L, "adjustment"), (Amount(paid.GetProperty("balance")), Amount(paid.GetProperty("payable")),
            Amount(paid.GetProperty("transaction").GetProperty("amount")), paid.GetProperty("transaction").GetProperty("type").GetString()));
        var (replayStatus, replayed, wasReplayed) = await RawAsync(Server, HttpMethod.Post, "/wallet/payout", cashier, Payout(guestId, 1_200_000), key);
        Assert.Equal((200, true), (replayStatus, wasReplayed));
        Assert.True(JsonElement.DeepEquals(paid, replayed));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @guestId AND type = 'adjustment'", new { guestId }));
        var state = await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier);
        Assert.Equal((1_200_000L, 300_000L), (state.GetProperty("x").GetProperty("payouts").GetInt64(), state.GetProperty("expectedCash").GetInt64()));
        Assert.DoesNotContain(await RefundsAsync(cashier), r => r.GetProperty("userId").GetGuid() == guestId);
        Assert.Equal("1200000 cash", await Players.ScalarAsync<string>(Server,
            "SELECT amount || ' ' || (meta ->> 'method') FROM audit_entries WHERE user_id = @guestId AND action = 'payout'", new { guestId }));

        // Paid by card: the refund stays on the account, no cash goes back.
        var card = await TestAgent.CreateAsync(Server);
        var cardGuest = (await DeskGuests.SeatAsync(Server, cashier, card.PcId, method: "card")).GetProperty("user").GetProperty("id").GetGuid();
        Assert.Equal(0, Amount((await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = card.PcId })).GetProperty("payable")));
        Assert.Equal(0, Amount(Assert.Single(await RefundsAsync(cashier), r => r.GetProperty("userId").GetGuid() == cardGuest).GetProperty("payable")));
        Assert.Equal(0, Amount((await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/payout", cashier, Payout(cardGuest, 1_200_000), Guid.NewGuid()))
            .GetProperty("error").GetProperty("details").GetProperty("payable")));

        // A bonus credited to a guest's balance (here straight in the ledger) never leaves as cash.
        var bonus = await TestAgent.CreateAsync(Server);
        var bonusGuest = (await DeskGuests.SeatAsync(Server, cashier, bonus.PcId)).GetProperty("user").GetProperty("id").GetGuid();
        await Players.ExecuteAsync(Server,
            """
            INSERT INTO ledger_entries (id, op_id, user_id, network_id, club_id, type, amount, balance_after, description, created_at)
            SELECT gen_random_uuid(), gen_random_uuid(), w.user_id, w.network_id, (SELECT id FROM clubs LIMIT 1), 'bonus', 500000, w.main_balance + 500000, 'test', now()
            FROM wallets w WHERE w.user_id = @bonusGuest;
            UPDATE wallets SET main_balance = main_balance + 500000 WHERE user_id = @bonusGuest;
            """,
            new { bonusGuest });
        var bonusEnd = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = bonus.PcId });
        Assert.Equal((1_700_000L, 1_200_000L), (Amount(bonusEnd.GetProperty("balance")), Amount(bonusEnd.GetProperty("payable"))));

        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = playing.PcId });
    }

    [Fact]
    public async Task Guests_get_no_bonus()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            automation = new object[]
            {
                new { id = "gift", name = "gift", enabled = true, trigger = new { kind = "sessionStarted" }, action = new { kind = "bonus", amount = 100_000 } },
                new { id = "hello", name = "hello", enabled = true, trigger = new { kind = "sessionStarted" }, action = new { kind = "message", text = "Добро пожаловать" } },
            },
        });
        try
        {
            var agent = await TestAgent.CreateAsync(Server);
            var guestId = (await DeskGuests.SeatAsync(Server, cashier, agent.PcId)).GetProperty("user").GetProperty("id").GetGuid();
            // The bonus rule fires (and counts) without crediting; the message still goes to the PC.
            Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @guestId AND type = 'bonus'", new { guestId }));
            Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM agent_commands WHERE pc_id = @PcId AND name = 'message'", new { agent.PcId }));
            Assert.Equal(2, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM rule_firings WHERE rule_id IN ('gift', 'hello')"));

            // A top-up the tier would reward (5 % from 5 000 000): none for a guest.
            var topUp = await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = guestId, amount = 6_000_000, method = "cash" });
            Assert.Equal((0L, 6_000_000L), (Amount(topUp.GetProperty("bonus")), Amount(topUp.GetProperty("balance"))));

            // A member's does.
            var member = await Players.CreateAsync(Server, balance: 0);
            Assert.Equal(300_000, Amount((await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = member.Id, amount = 6_000_000, method = "cash" }))
                .GetProperty("bonus")));
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { automation = Array.Empty<object>() });
        }
    }

    private Task<int> CountAsync(string table) => Players.ScalarAsync<int>(Server, $"SELECT count(*)::int FROM {table}");

    private async Task<(int Users, int Ledger, int Audit)> CountsAsync() => (await CountAsync("users"), await CountAsync("ledger_entries"), await CountAsync("audit_entries"));

    private async Task<bool> SignedInAsync(string token, Guid pcId) =>
        (await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", token)).GetProperty("seats").EnumerateArray()
        .Single(s => s.GetProperty("pc").GetProperty("id").GetGuid() == pcId).GetProperty("signedIn").GetBoolean();

    private async Task<List<JsonElement>> RefundsAsync(string token) =>
        [.. (await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", token)).GetProperty("guestRefunds").EnumerateArray()];

    private static (string?, string?) Field(JsonElement error) =>
        (error.GetProperty("error").GetProperty("details").GetProperty("field").GetString(), error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
}

/// <summary>The desk's walk-in guest is signed in by «Гость» even when the club turned kiosk guests off (D-25).</summary>
public sealed class DeskGuestLoginOffTests(GuestDisabledTests.Fixture server) : LedgerCheckedTest(server), IClassFixture<GuestDisabledTests.Fixture>
{
    [Fact]
    public async Task The_desk_guest_signs_in_even_when_kiosk_guest_login_is_off()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        Contract.AssertError(await DeskGuests.PressAsync(agent, 403), "forbidden", "guestDisabled");

        var opened = await DeskGuests.SeatAsync(Server, cashier, agent.PcId);
        var auth = await DeskGuests.PressAsync(agent);
        Assert.Equal(opened.GetProperty("session").GetProperty("id").GetGuid(), auth.GetProperty("session").GetProperty("id").GetGuid());
        Assert.Equal("guest", auth.GetProperty("user").GetProperty("role").GetString());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
    }
}

/// <summary>Seating a walk-in guest at the desk and the guest's «Гость» press on the PC.</summary>
public static class DeskGuests
{
    /// <summary>
    /// <c>POST /admin/sessions/guest</c> paid exactly: the price is read from the server's <c>409 priceChanged {total}</c>
    /// to a one-tiyin offer, so the test never assumes it. The 201 body.
    /// </summary>
    public static async Task<JsonElement> SeatAsync(ServerFixture server, string token, Guid pcId, int minutes = 60, string method = "cash", Guid? tariff = null)
    {
        object Body(long amount) => new { pcId, tariffId = tariff ?? Players.Standard, minutes, payment = new { amount, method } };
        var refused = await RawExpectAsync(server, 409, HttpMethod.Post, "/sessions/guest", token, Body(1), Guid.NewGuid());
        var total = Amount(refused.GetProperty("error").GetProperty("details").GetProperty("total"));
        return await RawExpectAsync(server, 201, HttpMethod.Post, "/sessions/guest", token, Body(total), Guid.NewGuid());
    }

    /// <summary>«Гость» on the PC (<c>guestLogin</c>, as the agent sends it); on 200 the PC keeps the guest's token.</summary>
    public static async Task<JsonElement> PressAsync(TestAgent agent, int status = 200)
    {
        using var response = await agent.PostAsync("/api/v1/auth/guest", new { pcId = agent.PcId, hwid = agent.Hwid, locale = "ru" });
        var body = await Players.ReadAsync(response, status);
        if (status == 200)
        {
            agent.UserToken = body.GetProperty("accessToken").GetString();
        }

        return body;
    }
}
