using System.Text.Json;
using ClubShell.Server.Wallet;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The bonus account (D-106): every bonus credit goes to <c>wallets.bonus_balance</c>, not the main balance; it pays only for
/// play time and before the money (a seat, an extension, a postpaid bill, the player's own purchase on the PC), its share of a
/// refund of unused time goes back to it; the bar, payouts and a debt never touch it, and it never goes below zero. A top-up
/// taken back takes its bonus back (refused once that bonus is spent, as the money is); a deleted client's bonus is written
/// off with it. The cashier's X/Z, the expected drawer and «Принято за смену» count no bonus; X/Z and the reports show the
/// bonus spent apart. The seeded tiers: 5 % from 5 000 000, 10 % from 10 000 000; Standard is 1 200 000 an hour.
/// </summary>
public sealed class BonusAccountTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    /// <summary>A bonus credit through the only writer of money (as a promo code or a rule would post it).</summary>
    private async Task GiveBonusAsync(Guid userId, long amount)
    {
        await using var c = await Server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await Ledger.PostAsync(c, tx, userId, allowOverdraft: false, Server.Clock.GetUtcNow(), new LedgerLine("bonus", amount, "Бонус: тест"));
        await tx.CommitAsync();
    }

    private async Task<(long Balance, long Bonus)> WalletAsync(Guid userId) =>
        (await Players.BalanceAsync(Server, userId), await Players.BonusAsync(Server, userId));

    [Fact]
    public async Task Every_bonus_credit_goes_to_the_bonus_account_and_the_cash_figures_count_none()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        await FreshShiftAsync(Server, cashier, openingCash: 100_000);
        var (agent, player) = await Players.SignedInAsync(Server, balance: 0);

        // The tier bonus of a desk top-up.
        var topUp = await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 10_000_000, method = "cash" });
        Assert.Equal((1_000_000L, 10_000_000L), (Amount(topUp.GetProperty("bonus")), Amount(topUp.GetProperty("balance"))));
        Assert.Equal((10_000_000L, 1_000_000L), await WalletAsync(player.Id));
        Assert.Equal("bonus|1000000|1000000|", await Players.ScalarAsync<string>(Server,
            "SELECT account || '|' || amount || '|' || balance_after || '|' || coalesce(method, '') FROM ledger_entries WHERE user_id = @Id AND type = 'bonus'", new { player.Id }));

        // The player's own promo code on the PC.
        var code = "B" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await Players.ExecuteAsync(Server,
            "INSERT INTO promo_codes (id, club_id, code, kind, value, uses_left) SELECT gen_random_uuid(), id, @code, 'bonus', 300000, 5 FROM clubs",
            new { code });
        var redeemed = await agent.RawPostAsync($"/api/v1/wallet/{player.Id}/promo", new { code });
        Assert.Equal((200, 10_000_000L), (redeemed.Status, Amount(redeemed.Body.GetProperty("balance"))));
        Assert.Equal((10_000_000L, 1_300_000L), await WalletAsync(player.Id));

        // The PC and the desk read both balances apart.
        var balance = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/wallet/{player.Id}/balance"), 200);
        Assert.Equal((10_000_000L, 1_300_000L), (Amount(balance.GetProperty("amount")), Amount(balance.GetProperty("bonus"))));
        var me = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 200);
        Assert.Equal((10_000_000L, 1_300_000L), (Amount(me.GetProperty("balance")), Amount(me.GetProperty("bonus"))));
        var hit = (await RawExpectAsync(Server, 200, HttpMethod.Get, $"/clients/lookup?q={player.Username}", cashier))
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == player.Id);
        Assert.Equal((10_000_000L, 1_300_000L), (Amount(hit.GetProperty("balance")), Amount(hit.GetProperty("bonus"))));
        var client = (await ExpectAsync(Server, 200, HttpMethod.Get, $"/clients?q={player.Username}", cashier))
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == player.Id);
        Assert.Equal(1_300_000L, Amount(client.GetProperty("bonus")));

        // X: the cash is the money paid; the bonus is a line apart, never in the drawer or «Принято за смену».
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((10_000_000L, 1_300_000L, 0L), (x.GetProperty("topUpCash").GetInt64(), x.GetProperty("bonuses").GetInt64(), x.GetProperty("bonusSpent").GetInt64()));
        Assert.Equal(10_100_000, await ExpectedCashAsync(Server, cashier));
        var feed = await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?kinds=topUp", cashier);
        Assert.Equal(10_000_000L, feed.GetProperty("today").GetProperty("taken").GetInt64());
    }

    [Fact]
    public async Task Play_time_takes_the_bonus_first_and_a_refund_gives_it_back_first()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        await FreshShiftAsync(Server, cashier, openingCash: 0);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 1_000_000);
        await GiveBonusAsync(player.Id, 500_000);

        // A desk seat of 30 minutes (600 000): 500 000 from the bonus, 100 000 from the money.
        var seat = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 30 });
        Assert.Equal((600_000L, 900_000L), (Amount(seat.GetProperty("charged")), Amount(seat.GetProperty("balance"))));
        Assert.Equal((900_000L, 0L), await WalletAsync(player.Id));
        var sessionId = seat.GetProperty("session").GetProperty("id").GetGuid();
        Assert.Equal("bonus:-500000:0 main:-100000:900000", await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(account || ':' || amount || ':' || balance_after, ' ' ORDER BY account) FROM ledger_entries WHERE session_id = @sessionId AND type = 'charge'",
            new { sessionId }));

        // The bonus is used up: the extension is money only.
        var extended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/extend", cashier, new { pcId = agent.PcId, minutes = 30 });
        Assert.Equal((600_000L, 300_000L), (Amount(extended.GetProperty("charged")), Amount(extended.GetProperty("balance"))));
        Assert.Equal((300_000L, 0L), await WalletAsync(player.Id));

        // X: the sessions are the time sold, of which the bonus paid 500 000; no cash came in for any of it.
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((1_200_000L, 500_000L, 0L), (x.GetProperty("sessions").GetInt64(), x.GetProperty("bonusSpent").GetInt64(), x.GetProperty("topUpCash").GetInt64()));
        Assert.Equal(0, await ExpectedCashAsync(Server, cashier));

        // Ended at once: the unused hour (1 200 000) goes back, the 500 000 the bonus paid to the bonus account first, so the
        // bonus never turns into money.
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        Assert.Equal(1_200_000L, Amount(ended.GetProperty("refunded")));
        Assert.Equal((1_000_000L, 500_000L), await WalletAsync(player.Id));
        Assert.Equal(0L, (await Bar.XAsync(Server, cashier)).GetProperty("bonusSpent").GetInt64());

        // The player's own time on the PC takes the bonus first too.
        await agent.LoginAsync(player);
        var (status, _) = await Players.StartAsync(agent, player, minutes: 60);
        Assert.Equal(201, status);
        Assert.Equal((300_000L, 0L), await WalletAsync(player.Id));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
    }

    [Fact]
    public async Task A_postpaid_bill_takes_the_bonus_first_and_the_debt_stays_on_the_balance()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 0);
        await GiveBonusAsync(player.Id, 500_000);

        await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier,
            new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60, prepaid = false }, Guid.NewGuid());
        Server.Clock.Advance(TimeSpan.FromMinutes(30));
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        Assert.Equal((600_000L, -100_000L), (Amount(ended.GetProperty("charged")), Amount(ended.GetProperty("balance"))));
        Assert.Equal((-100_000L, 0L), await WalletAsync(player.Id));
        Assert.False(await Players.ScalarAsync<bool>(Server, "SELECT bool_or(overdraft) FROM ledger_entries WHERE user_id = @Id AND account = 'bonus'", new { player.Id }));
    }

    [Fact]
    public async Task The_bar_and_a_payout_never_touch_the_bonus()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 100_000, null, title: "Бонусная вода");
        var player = await Players.CreateAsync(Server, balance: 100_000);
        await GiveBonusAsync(player.Id, 5_000_000);

        // From the balance: only the money counts, the bonus buys no goods.
        var (status, body, _) = await Bar.SellAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 200_000, null, player.Id, null, (product, 2)));
        Assert.Equal(402, status);
        Assert.Equal(100_000L, Amount(body.GetProperty("error").GetProperty("details").GetProperty("available")));
        await Bar.SoldAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 100_000, null, player.Id, null, (product, 1)));
        Assert.Equal((0L, 5_000_000L), await WalletAsync(player.Id));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id AND account = 'bonus' AND type <> 'bonus'", new { player.Id }));

        // An open postpaid bill is time: the bonus covers it first, so the bar may still spend the money.
        var agent = await TestAgent.CreateAsync(Server);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 100_000, method = "cash" });
        await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier,
            new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60, prepaid = false }, Guid.NewGuid());
        Server.Clock.Advance(TimeSpan.FromMinutes(30));
        await Bar.SoldAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 100_000, null, player.Id, null, (product, 1)));
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        Assert.Equal(600_000L, Amount(ended.GetProperty("charged")));
        Assert.Equal((0L, 4_400_000L), await WalletAsync(player.Id));
    }

    [Fact]
    public async Task A_cancelled_top_up_takes_its_bonus_back_and_not_once_that_bonus_is_spent()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier, openingCash: 0);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 0);
        var topUp = (await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 10_000_000, method = "card" }))
            .GetProperty("transaction").GetProperty("id").GetGuid();
        Assert.Equal((10_000_000L, 1_000_000L), await WalletAsync(player.Id));

        // 600 000 of its bonus went on time: nothing is taken back, not even in part (D-91).
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 30 });
        Assert.Equal((10_000_000L, 400_000L), await WalletAsync(player.Id));
        var spent = await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/topup/cancel", cashier, new { transactionId = topUp, reasonCode = "wrongAmount" }, Guid.NewGuid());
        Contract.AssertError(spent, "conflict", "topUpSpent");
        var details = spent.GetProperty("error").GetProperty("details");
        Assert.Equal(("bonus", 400_000L, 1_000_000L), (details.GetProperty("account").GetString(), Amount(details.GetProperty("spendable")), Amount(details.GetProperty("required"))));

        // The desk's end gives the unused time back to the bonus: now the top-up goes back with its whole bonus.
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        Assert.Equal((10_000_000L, 1_000_000L), await WalletAsync(player.Id));
        var cancelled = await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup/cancel", cashier, new { transactionId = topUp, reasonCode = "wrongAmount" }, Guid.NewGuid());
        Assert.Equal(1_000_000L, cancelled.GetProperty("cancelled").GetProperty("bonus").GetInt64());
        Assert.Equal((0L, 0L), await WalletAsync(player.Id));
        Assert.Equal("bonus:-1000000", await Players.ScalarAsync<string>(Server,
            "SELECT account || ':' || amount FROM ledger_entries WHERE user_id = @Id AND type = 'bonus' AND amount < 0", new { player.Id }));
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((0L, 0L, 0L), (x.GetProperty("topUpOther").GetInt64(), x.GetProperty("bonuses").GetInt64(), x.GetProperty("bonusSpent").GetInt64()));
    }

    [Fact]
    public async Task A_deleted_client_takes_the_bonus_away_and_the_bonus_never_holds_a_delete_back()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var onlyBonus = await Players.CreateAsync(Server, balance: 0);
        await GiveBonusAsync(onlyBonus.Id, 300_000);
        await RawExpectAsync(Server, 200, HttpMethod.Delete, $"/clients/{onlyBonus.Id}", owner);
        Assert.Equal((0L, 0L), await WalletAsync(onlyBonus.Id));
        Assert.Equal("bonus|-300000|clientDelete|true", await Players.ScalarAsync<string>(Server,
            "SELECT account || '|' || amount || '|' || (meta ->> 'kind') || '|' || (method IS NULL AND shift_id IS NULL) FROM ledger_entries WHERE user_id = @Id AND type = 'adjustment' AND amount < 0",
            new { onlyBonus.Id }));
        Assert.Equal("0|300000", await Players.ScalarAsync<string>(Server,
            "SELECT amount || '|' || (meta ->> 'bonusWrittenOff') FROM audit_entries WHERE action = 'clientDelete' AND user_id = @Id", new { onlyBonus.Id }));

        // With money too: the refusal names both, the write-off of the money shown takes the bonus with it.
        var rich = await Players.CreateAsync(Server, balance: 200_000);
        await GiveBonusAsync(rich.Id, 50_000);
        var refused = await RawExpectAsync(Server, 409, HttpMethod.Delete, $"/clients/{rich.Id}", owner);
        var details = refused.GetProperty("error").GetProperty("details");
        Assert.Equal((200_000L, 50_000L), (Amount(details.GetProperty("balance")), Amount(details.GetProperty("bonus"))));
        await RawExpectAsync(Server, 200, HttpMethod.Delete, $"/clients/{rich.Id}?writeOff=200000", owner);
        Assert.Equal((0L, 0L), await WalletAsync(rich.Id));
    }

    [Fact]
    public async Task The_z_report_and_the_reports_show_the_bonus_apart_from_the_money()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        await FreshShiftAsync(Server, cashier, openingCash: 50_000);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 0);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 5_000_000, method = "cash" });
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 30 });
        Server.Clock.Advance(TimeSpan.FromMinutes(30));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });

        var state = await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier);
        Assert.Equal(5_050_000, state.GetProperty("expectedCash").GetInt64());
        var closed = await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 5_050_000 });
        var z = closed.GetProperty("shift").GetProperty("totals");
        Assert.Equal((5_000_000L, 600_000L, 250_000L, 250_000L), (z.GetProperty("topUpCash").GetInt64(), z.GetProperty("sessions").GetInt64(),
            z.GetProperty("bonuses").GetInt64(), z.GetProperty("bonusSpent").GetInt64()));
        Assert.Equal(5_050_000, closed.GetProperty("expectedCash").GetInt64());

        var totals = (await RawExpectAsync(Server, 200, HttpMethod.Get, "/reports?days=1", owner)).GetProperty("totals");
        Assert.True(totals.GetProperty("bonusSpent").GetInt64() >= 250_000);
        Assert.True(totals.GetProperty("bonuses").GetInt64() >= 250_000);
        Assert.Equal(JsonValueKind.Number, totals.GetProperty("sessions").ValueKind);
    }
}
