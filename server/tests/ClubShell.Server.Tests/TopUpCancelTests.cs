using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// A desk top-up taken back (D-91, <c>POST /admin/wallet/topup/cancel</c>): the money back by the same method with the
/// tier bonus it gave, or corrected to the right amount in one step; every sum by method nets it out (X, the drawer,
/// «Принято за смену»); the feed marks the top-up and shows the cancel; refused once spent, outside its own open shift,
/// for a seat's payment, twice; a cashier only for their own top-up within 15 minutes. The seeded tiers: 5 % from 5 000 000,
/// 10 % from 10 000 000.
/// </summary>
public sealed class TopUpCancelTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    private static async Task<(Guid Id, long Bonus)> TopUpAsync(ServerFixture server, string token, Guid userId, long amount, string method = "cash")
    {
        var r = await ExpectAsync(server, 200, HttpMethod.Post, "/wallet/topup", token, new { userId, amount, method });
        return (r.GetProperty("transaction").GetProperty("id").GetGuid(), Amount(r.GetProperty("bonus")));
    }

    private static Task<JsonElement> CancelAsync(ServerFixture server, string token, Guid transactionId, int status = 200, long? amount = null, string reasonCode = "wrongAmount") =>
        RawExpectAsync(server, status, HttpMethod.Post, "/wallet/topup/cancel", token,
            amount is { } a ? new { transactionId, reasonCode, amount = a } : new { transactionId, reasonCode }, Guid.NewGuid());

    [Fact]
    public async Task A_cash_top_up_goes_back_with_its_bonus_and_every_total_nets_it_out()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        // «Принято за смену» counts from the shift's opening: nothing of an earlier test at that very instant.
        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        await FreshShiftAsync(Server, cashier, openingCash: 100_000);
        var player = await Players.CreateAsync(Server, balance: 200_000);
        var (topUp, bonus) = await TopUpAsync(Server, cashier, player.Id, 6_000_000);
        Assert.Equal((300_000L, 6_500_000L, 6_100_000L), (bonus, await Players.BalanceAsync(Server, player.Id), await ExpectedCashAsync(Server, cashier)));

        Server.Clock.Advance(TimeSpan.FromMinutes(1));
        var r = await CancelAsync(Server, cashier, topUp);
        var c = r.GetProperty("cancelled");
        Assert.Equal((topUp, 6_000_000L, 300_000L, "cash", "wrongAmount", "Кассир Азиз"), (c.GetProperty("transactionId").GetGuid(), c.GetProperty("amount").GetInt64(),
            c.GetProperty("bonus").GetInt64(), c.GetProperty("method").GetString(), c.GetProperty("reasonCode").GetString(), c.GetProperty("staffName").GetString()));
        Assert.Equal((200_000L, JsonValueKind.Null, 100_000L), (Amount(r.GetProperty("balance")), r.GetProperty("topUp").ValueKind, r.GetProperty("expectedCash").GetInt64()));
        Assert.Equal(200_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal("topUp:6000000 bonus:300000 topUp:-6000000 bonus:-300000", await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(type || ':' || amount, ' ' ORDER BY created_at, abs(amount) DESC) FROM ledger_entries WHERE user_id = @Id AND type IN ('topUp', 'bonus')", new { player.Id }));
        Assert.Equal(0, await Players.ScalarAsync<long>(Server, "SELECT lifetime_spent FROM wallets WHERE user_id = @Id", new { player.Id }));

        // X: the top-up is netted out by method, the cancel counted apart; the drawer back to the float.
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((0L, 0L, 0L, 6_000_000L, 1), (x.GetProperty("topUpCash").GetInt64(), x.GetProperty("topUpByMethod").GetProperty("cash").GetInt64(),
            x.GetProperty("bonuses").GetInt64(), x.GetProperty("topUpCancels").GetInt64(), x.GetProperty("topUpCancelCount").GetInt32()));
        Assert.Equal(100_000, await ExpectedCashAsync(Server, cashier));

        // The feed: the top-up marked, the cancel giving the cash back; «Принято за смену» nets it out.
        var feed = await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?kinds=topUp,topUpCancel", cashier);
        var items = feed.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["topUpCancel", "topUp"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal((-6_000_000L, 6_000_000L, "cash", "wrongAmount"), (items[0].GetProperty("drawer").GetInt64(), items[0].GetProperty("paid").GetProperty("amount").GetInt64(),
            items[0].GetProperty("paid").GetProperty("method").GetString(), items[0].GetProperty("reasonCode").GetString()));
        Assert.Equal((true, 6_000_000L), (items[1].GetProperty("voided").GetBoolean(), items[1].GetProperty("drawer").GetInt64()));
        Assert.Equal((0L, 0L), (feed.GetProperty("today").GetProperty("byMethod").GetProperty("cash").GetInt64(), feed.GetProperty("today").GetProperty("taken").GetInt64()));

        Contract.AssertError(await CancelAsync(Server, cashier, topUp, 409), "conflict", "alreadyCancelled");

        // The owner's control page lists it with the cashier's count.
        var owner = await LoginAsync(Server, OwnerPin);
        var control = await ExpectAsync(Server, 200, HttpMethod.Get, "/control?days=1", owner);
        var listed = control.GetProperty("corrections").EnumerateArray().First(e => e.GetProperty("userId").GetGuid() == player.Id);
        Assert.Equal(("topUpCancel", 6_000_000L, "cash"), (listed.GetProperty("action").GetString(), listed.GetProperty("amount").GetInt64(),
            listed.GetProperty("meta").GetProperty("method").GetString()));
        var azizSummary = control.GetProperty("staff").EnumerateArray().First(s => s.GetProperty("staffName").GetString() == "Кассир Азиз");
        Assert.True(azizSummary.GetProperty("topUpCancels").GetInt32() >= 1);
    }

    [Fact]
    public async Task A_wrong_amount_is_corrected_in_one_step_with_the_bonus_of_the_right_one()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var player = await Players.CreateAsync(Server, balance: 0);
        // 10 000 000 by card (10 % bonus) instead of 1 000 000.
        var (topUp, bonus) = await TopUpAsync(Server, cashier, player.Id, 10_000_000, "card");
        Assert.Equal(1_000_000, bonus);

        var r = await CancelAsync(Server, cashier, topUp, amount: 1_000_000);
        var corrected = r.GetProperty("topUp");
        Assert.Equal((1_000_000L, 0L, 1_000_000L), (Amount(corrected.GetProperty("transaction").GetProperty("amount")), Amount(corrected.GetProperty("bonus")),
            Amount(r.GetProperty("balance"))));
        Assert.Equal(1_000_000, await Players.BalanceAsync(Server, player.Id));
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((1_000_000L, 1_000_000L, 0L, 0L), (x.GetProperty("topUpByMethod").GetProperty("card").GetInt64(), x.GetProperty("topUpOther").GetInt64(),
            x.GetProperty("bonuses").GetInt64(), x.GetProperty("topUpCash").GetInt64()));

        // The right top-up is in the journal as a top-up that corrects the wrong one; the same amount again is refused.
        Assert.Equal(topUp, await Players.ScalarAsync<Guid>(Server,
            "SELECT (meta ->> 'correctionOf')::uuid FROM audit_entries WHERE action = 'topUp' AND user_id = @Id AND meta ? 'correctionOf'", new { player.Id }));
        var (again, _) = await TopUpAsync(Server, cashier, player.Id, 500_000, "card");
        var same = await CancelAsync(Server, cashier, again, 400, amount: 500_000);
        Assert.Equal(("amount", "same"), (same.GetProperty("error").GetProperty("details").GetProperty("field").GetString(),
            same.GetProperty("error").GetProperty("details").GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task Money_already_spent_a_closed_shift_and_a_seats_payment_are_refused()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 0);
        var (topUp, _) = await TopUpAsync(Server, cashier, player.Id, 1_000_000);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 30 });

        // 600 000 went on time: only 400 000 is left of the 1 000 000.
        var spent = await CancelAsync(Server, cashier, topUp, 409);
        Contract.AssertError(spent, "conflict", "topUpSpent");
        var details = spent.GetProperty("error").GetProperty("details");
        Assert.Equal((400_000L, 1_000_000L), (Amount(details.GetProperty("spendable")), Amount(details.GetProperty("required"))));
        Assert.Equal(400_000, await Players.BalanceAsync(Server, player.Id));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });

        // A seat's payment went into the session at once: not a top-up to take back.
        var guest = await Players.CreateAsync(Server, balance: 0);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier,
            new { pcId = agent.PcId, userId = guest.Id, tariffId = Players.Standard, minutes = 30, payment = new { amount = 600_000, method = "cash" } });
        var seatPayment = await Players.ScalarAsync<Guid>(Server, "SELECT id FROM ledger_entries WHERE user_id = @Id AND type = 'topUp'", new { guest.Id });
        Contract.AssertError(await CancelAsync(Server, owner, seatPayment, 409), "conflict", "notCorrectable");
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        Assert.Equal(404, (await RawAsync(Server, HttpMethod.Post, "/wallet/topup/cancel", owner, new { transactionId = Guid.NewGuid(), reasonCode = "wrongAmount" }, Guid.NewGuid())).Status);

        // Only in the shift that took it, even for the owner.
        var other = await Players.CreateAsync(Server, balance: 0);
        var (earlier, _) = await TopUpAsync(Server, cashier, other.Id, 300_000, "payme");
        await FreshShiftAsync(Server, cashier);
        Contract.AssertError(await CancelAsync(Server, owner, earlier, 409), "conflict", "topUpShiftClosed");
        Assert.Equal(300_000, await Players.BalanceAsync(Server, other.Id));
    }

    [Fact]
    public async Task A_cashier_takes_back_only_their_own_top_up_within_15_minutes_and_cash_only_if_the_owner_allows_cash_out()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var player = await Players.CreateAsync(Server, balance: 0);

        var (owners, _) = await TopUpAsync(Server, owner, player.Id, 100_000);
        Contract.AssertError(await CancelAsync(Server, cashier, owners, 403), "forbidden", "notOwnTopUp");

        var (late, _) = await TopUpAsync(Server, cashier, player.Id, 100_000, "click");
        Server.Clock.Advance(TimeSpan.FromMinutes(16));
        var window = await CancelAsync(Server, cashier, late, 403);
        Contract.AssertError(window, "forbidden", "cancelWindow");
        Assert.Equal(15, window.GetProperty("error").GetProperty("details").GetProperty("minutes").GetInt32());
        await CancelAsync(Server, owner, late, reasonCode: "wrongClient");

        // limits.cashOutOwnerOnly: no cash back from a cashier, a card top-up still; the owner may.
        await Players.ExecuteAsync(Server, """UPDATE clubs SET settings = settings || jsonb_build_object('limits', coalesce(settings -> 'limits', '{}') || '{"cashOutOwnerOnly":true}')""");
        try
        {
            var (cash, _) = await TopUpAsync(Server, cashier, player.Id, 200_000);
            var (card, _) = await TopUpAsync(Server, cashier, player.Id, 200_000, "card");
            Contract.AssertError(await CancelAsync(Server, cashier, cash, 403), "forbidden", "ownerOnly");
            // A correction upwards puts cash in: allowed.
            await CancelAsync(Server, cashier, cash, amount: 250_000);
            await CancelAsync(Server, cashier, card);
            Assert.Equal(100_000 + 250_000, await Players.BalanceAsync(Server, player.Id));
            Assert.Equal(350_000, await ExpectedCashAsync(Server, cashier));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings #- '{limits,cashOutOwnerOnly}'");
        }

        // `other` needs a note; the club API key has no drawer.
        var (noted, _) = await TopUpAsync(Server, cashier, player.Id, 100_000, "card");
        var note = await RawExpectAsync(Server, 400, HttpMethod.Post, "/wallet/topup/cancel", cashier, new { transactionId = noted, reasonCode = "other" }, Guid.NewGuid());
        Assert.Equal("note", note.GetProperty("error").GetProperty("details").GetProperty("field").GetString());
        await RawExpectAsync(Server, 403, HttpMethod.Post, "/wallet/topup/cancel", await ApiKeyAsync(Server),
            new { transactionId = noted, reasonCode = "wrongAmount" }, Guid.NewGuid());
        await ClearApiKeyAsync(Server);
    }
}
