using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// <c>adminRedeemPromo</c> (DESIGN §4.4, D-15): one atomic decrement, so parallel redeems never take more than
/// <c>usesLeft</c>; one redeem per client and code; <c>expired</c>, <c>exhausted</c>, <c>discountAtCheckout</c>; the bonus
/// through the ledger, once per <c>Idempotency-Key</c>.
/// </summary>
public sealed class PromoTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Parallel_redeems_never_take_more_than_the_uses_left()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var code = await PromoAsync(value: 500_000, usesLeft: 3);
        var players = new List<TestPlayer>();
        for (var i = 0; i < 10; i++)
        {
            players.Add(await Players.CreateAsync(Server, balance: 0));
        }

        var answers = await Task.WhenAll(players.Select(p => SendAsync(Server, HttpMethod.Post, "/promo/redeem", cashier, new { userId = p.Id, code })));
        Assert.Equal(3, answers.Count(a => a.Status == 200));
        Assert.All(answers.Where(a => a.Status != 200), a => Assert.Equal((400, "exhausted"), (a.Status, Reason(a.Body))));
        Assert.Equal("3 0", await Players.ScalarAsync<string>(Server, "SELECT used || ' ' || uses_left FROM promo_codes WHERE code = @code", new { code }));
        Assert.Equal(1_500_000, await Players.ScalarAsync<long>(Server, "SELECT sum(main_balance)::bigint FROM wallets WHERE user_id = ANY(@ids)",
            new { ids = players.Select(p => p.Id).ToArray() }));
        Assert.Equal(3, await Players.ScalarAsync<int>(Server,
            "SELECT count(*)::int FROM promo_redemptions r JOIN ledger_entries l ON l.op_id = r.op_id WHERE l.type = 'bonus' AND l.user_id = ANY(@ids)",
            new { ids = players.Select(p => p.Id).ToArray() }));
    }

    [Fact]
    public async Task A_client_redeems_a_code_once_even_in_parallel()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var code = await PromoAsync(value: 200_000, usesLeft: null);
        var player = await Players.CreateAsync(Server, balance: 0);

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            SendAsync(Server, HttpMethod.Post, "/promo/redeem", cashier, new { userId = player.Id, code = code.ToLowerInvariant() })));
        Assert.Equal([200, 400, 400, 400, 400, 400], answers.Select(a => a.Status).Order());
        Assert.All(answers.Where(a => a.Status == 400), a => Assert.Equal("exhausted", Reason(a.Body)));
        Assert.Equal(200_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal("1 ", await Players.ScalarAsync<string>(Server, "SELECT used || ' ' || coalesce(uses_left::text, '') FROM promo_codes WHERE code = @code", new { code }));
    }

    [Fact]
    public async Task Refusals_come_in_order_and_a_retry_by_key_credits_once()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var player = await Players.CreateAsync(Server, balance: 100);
        var now = Server.Clock.GetUtcNow();
        var expired = await PromoAsync(1_000, 5, expiresAt: now.AddHours(-1));
        var empty = await PromoAsync(1_000, 0);
        var discount = await PromoAsync(15, 5, kind: "discountPct");
        var emptyDiscount = await PromoAsync(15, 0, kind: "discountPct");

        foreach (var (code, reason) in new[] { (expired, "expired"), (empty, "exhausted"), (discount, "discountAtCheckout"), (emptyDiscount, "exhausted") })
        {
            var error = await ExpectAsync(Server, 400, HttpMethod.Post, "/promo/redeem", cashier, new { userId = player.Id, code });
            Assert.Equal(("code", reason), StaffAdminTests.Details(error));
        }

        foreach (var (body, what) in new (object, string)[] { (new { userId = Guid.NewGuid(), code = "WELCOME" }, "user"), (new { userId = player.Id, code = "NO-SUCH" }, "promo") })
        {
            Assert.Equal(what, (await ExpectAsync(Server, 404, HttpMethod.Post, "/promo/redeem", cashier, body)).GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        }

        Assert.Equal(("userId", "required"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, "/promo/redeem", cashier, new { code = "WELCOME" })));

        // A code expiring later still works now; the seeded WELCOME is 10 000 sum, credited once for a repeated key.
        var later = await PromoAsync(300, 5, expiresAt: now.AddMinutes(1));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/promo/redeem", cashier, new { userId = player.Id, code = later });
        Server.Clock.Advance(TimeSpan.FromMilliseconds(5)); // the newest ledger row below is WELCOME's, not tied with the one above
        var redeemed = await ReplayedAsync(Server, 200, HttpMethod.Post, "/promo/redeem", cashier, new { userId = player.Id, code = "welcome" });
        Assert.Equal(1_000_400, redeemed.GetProperty("balance").GetProperty("amount").GetInt64());
        Assert.Equal(1_000_400, await Players.BalanceAsync(Server, player.Id));
        var audit = await Players.ScalarAsync<string>(Server,
            "SELECT amount || ' ' || (meta ->> 'code') FROM audit_entries WHERE action = 'promoRedeem' AND user_id = @Id ORDER BY at DESC, id DESC LIMIT 1", new { player.Id });
        Assert.Equal("1000000 WELCOME", audit);
        var row = await Players.ScalarAsync<string>(Server,
            "SELECT type || ' ' || description FROM ledger_entries WHERE user_id = @Id ORDER BY created_at DESC, id DESC LIMIT 1", new { player.Id });
        Assert.Equal("bonus Промокод WELCOME", row);
    }

    private async Task<string> PromoAsync(long value, int? usesLeft, DateTimeOffset? expiresAt = null, string kind = "bonus")
    {
        var code = "P" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await Players.ExecuteAsync(Server,
            """
            INSERT INTO promo_codes (id, club_id, code, kind, value, uses_left, expires_at)
            SELECT @id, id, @code, @kind, @value, @usesLeft, @expiresAt FROM clubs
            """,
            new { id = Guid.NewGuid(), code, kind, value, usesLeft, expiresAt });
        return code;
    }

    private static string? Reason(JsonElement error) => error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString();
}
