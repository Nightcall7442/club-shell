using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Voids of bar sales (cash desk part 3, D-56): the whole sale with a reason; a cashier within 15 minutes, the owner later;
/// a method sale only in its own shift (cash leaves the drawer only as far as it holds), a balance sale in any open shift as
/// a positive <c>purchase</c> row (never a refund); <c>defect</c> does not restock; one void per sale; the per-cashier alert.
/// </summary>
public sealed class ShopVoidTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Cash_void_restocks_and_lowers_expected_cash()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 500_000, 5);
        var saleId = await Bar.CashAsync(Server, cashier, product, 500_000, qty: 2);
        var saleAt = await Players.ScalarAsync<DateTimeOffset>(Server, "SELECT created_at FROM shop_sales WHERE id = @saleId", new { saleId });
        Assert.Equal((3, 1_000_000L), (await Bar.StockAsync(Server, product), await ExpectedCashAsync(Server, cashier)));

        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        var voided = await Bar.VoidAsync(Server, cashier, saleId);
        var v = voided.GetProperty("void");
        Assert.Equal((saleId, "cash", 1_000_000L, "mistake", JsonValueKind.Null, "Кассир Азиз"), (v.GetProperty("saleId").GetGuid(), v.GetProperty("method").GetString(),
            v.GetProperty("total").GetInt64(), v.GetProperty("reasonCode").GetString(), v.GetProperty("note").ValueKind, v.GetProperty("staffName").GetString()));
        Assert.Equal(saleAt, v.GetProperty("saleAt").GetDateTimeOffset());
        Assert.Equal((JsonValueKind.Null, 5, 0L), (voided.GetProperty("balance").ValueKind, voided.GetProperty("products")[0].GetProperty("stockQty").GetInt32(),
            voided.GetProperty("expectedCash").GetInt64()));
        Assert.Equal(5, await Bar.StockAsync(Server, product));
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((0L, 0L, 1_000_000L, 1), (x.GetProperty("shop").GetInt64(), Bar.ByMethod(x).Cash, x.GetProperty("shopVoids").GetInt64(), x.GetProperty("shopVoidCount").GetInt32()));

        // The feed: the sale marked voided, the void giving the cash back, both summing to the drawer.
        var items = (await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?kinds=shopSale,shopVoid", cashier)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["shopVoid", "shopSale"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal((true, 1_000_000L, saleId), (items[1].GetProperty("voided").GetBoolean(), items[1].GetProperty("drawer").GetInt64(), items[1].GetProperty("saleId").GetGuid()));
        Assert.Equal((-1_000_000L, "mistake", saleAt, 1_000_000L), (items[0].GetProperty("drawer").GetInt64(), items[0].GetProperty("reasonCode").GetString(),
            items[0].GetProperty("voidOfAt").GetDateTimeOffset(), items[0].GetProperty("paid").GetProperty("amount").GetInt64()));
        Assert.Equal([(2, 500_000L)], items[0].GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("qty").GetInt32(), l.GetProperty("price").GetInt64())));
    }

    [Fact]
    public async Task Cash_void_larger_than_drawer_is_409_cashShort()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 1_000_000, 5);
        var saleId = await Bar.CashAsync(Server, cashier, product, 1_000_000);
        await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = 600_000, reasonCode = "collection" }, Guid.NewGuid());
        var refused = await Bar.VoidAsync(Server, cashier, saleId, status: 409);
        Contract.AssertError(refused, "conflict", "cashShort");
        Assert.Equal(400_000, Amount(refused.GetProperty("error").GetProperty("details").GetProperty("available")));
        Assert.Equal(4, await Bar.StockAsync(Server, product));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM shop_sales WHERE void_of = @saleId", new { saleId }));
    }

    [Fact]
    public async Task Balance_void_posts_a_positive_purchase_and_X_refunds_stay_unchanged()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 1_000_000, null);
        var player = await Players.CreateAsync(Server, balance: 2_000_000);
        var saleId = Guid.NewGuid();
        await Bar.SoldAsync(Server, cashier, Bar.Sale(saleId, 1_000_000, null, player.Id, null, (product, 1)));
        var voided = await Bar.VoidAsync(Server, cashier, saleId, "returned");
        Assert.Equal(2_000_000, Amount(voided.GetProperty("balance")));
        Assert.Equal(2_000_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal("purchase:-1000000 purchase:1000000", await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(type || ':' || amount, ' ' ORDER BY created_at, amount) FROM ledger_entries WHERE user_id = @Id AND type <> 'adjustment'", new { player.Id }));
        Assert.Equal(0, await Players.ScalarAsync<long>(Server, "SELECT lifetime_spent FROM wallets WHERE user_id = @Id", new { player.Id }));
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((0L, 0L, 0L, 1_000_000L), (x.GetProperty("refunds").GetInt64(), x.GetProperty("shop").GetInt64(), Bar.ByMethod(x).Balance, x.GetProperty("shopVoids").GetInt64()));
    }

    [Fact]
    public async Task A_balance_sale_is_voidable_by_the_owner_in_a_later_shift_a_cash_sale_is_not()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 600_000, 10);
        var player = await Players.CreateAsync(Server, balance: 1_000_000);
        var balanceSale = Guid.NewGuid();
        await Bar.SoldAsync(Server, cashier, Bar.Sale(balanceSale, 600_000, null, player.Id, null, (product, 1)));
        var cashSale = await Bar.CashAsync(Server, cashier, product, 600_000);
        var cardSale = await Bar.CashAsync(Server, cashier, product, 600_000, method: "card");

        var nextShift = await FreshShiftAsync(Server, cashier);
        Contract.AssertError(await Bar.VoidAsync(Server, owner, cashSale, status: 409), "conflict", "saleShiftClosed");
        Contract.AssertError(await Bar.VoidAsync(Server, owner, cardSale, status: 409), "conflict", "saleShiftClosed");
        await Bar.VoidAsync(Server, owner, balanceSale, "returned");
        Assert.Equal(1_000_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(nextShift, await Players.ScalarAsync<Guid>(Server, "SELECT shift_id FROM shop_sales WHERE void_of = @balanceSale", new { balanceSale }));

        // The void joins the shift it was made in: its balance part is negative there.
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((-600_000L, -600_000L, 600_000L, 1), (x.GetProperty("shop").GetInt64(), Bar.ByMethod(x).Balance, x.GetProperty("shopVoids").GetInt64(),
            x.GetProperty("shopVoidCount").GetInt32()));
        Assert.Equal(8, await Bar.StockAsync(Server, product));
    }

    [Fact]
    public async Task Cashier_after_15_minutes_is_403_voidWindow_owner_is_allowed()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 200_000, 10);
        var early = await Bar.CashAsync(Server, cashier, product, 200_000, method: "payme");
        var late = await Bar.CashAsync(Server, cashier, product, 200_000, method: "payme");
        Server.Clock.Advance(TimeSpan.FromMinutes(14));
        await Bar.VoidAsync(Server, cashier, early);
        Server.Clock.Advance(TimeSpan.FromMinutes(2));
        var refused = await Bar.VoidAsync(Server, cashier, late, status: 403);
        Contract.AssertError(refused, "forbidden", "voidWindow");
        Assert.Equal(15, refused.GetProperty("error").GetProperty("details").GetProperty("minutes").GetInt32());
        await Bar.VoidAsync(Server, owner, late, "defect");
        Assert.Equal(9, await Bar.StockAsync(Server, product));
    }

    [Fact]
    public async Task Defect_does_not_restock_other_needs_a_note_and_a_sale_is_voided_once()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 100_000, 10);
        var defect = await Bar.CashAsync(Server, cashier, product, 100_000, qty: 2);
        await Bar.VoidAsync(Server, cashier, defect, "defect");
        Assert.Equal(8, await Bar.StockAsync(Server, product));
        Contract.AssertError(await Bar.VoidAsync(Server, cashier, defect, status: 409), "conflict", "alreadyVoided");

        var other = await Bar.CashAsync(Server, cashier, product, 100_000);
        foreach (var (body, field, reason) in new (object, string, string)[]
        {
            (new { }, "reasonCode", "required"), (new { reasonCode = "gift" }, "reasonCode", "enum"), (new { reasonCode = "other" }, "note", "required"),
            (new { reasonCode = "other", note = " ab " }, "note", "min"), (new { reasonCode = "mistake", note = new string('n', 201) }, "note", "max"),
        })
        {
            Assert.Equal((field, reason), Bar.Details(await RawExpectAsync(Server, 400, HttpMethod.Post, $"/shop/sales/{other}/void", cashier, body, Guid.NewGuid())));
        }

        Assert.Equal(("Idempotency-Key", "required"), Bar.Details(await RawExpectAsync(Server, 400, HttpMethod.Post, $"/shop/sales/{other}/void", cashier, new { reasonCode = "mistake" })));
        Assert.Equal("Покупатель передумал", (await Bar.VoidAsync(Server, cashier, other, "other", "  Покупатель передумал ")).GetProperty("void").GetProperty("note").GetString());
        Assert.Equal("other Покупатель передумал", await Players.ScalarAsync<string>(Server,
            "SELECT (meta ->> 'reasonCode') || ' ' || (meta ->> 'note') FROM audit_entries WHERE action = 'shopVoid' AND meta ->> 'saleId' = @id", new { id = other.ToString() }));

        // Unknown, a void's own id and a sale of another kind of row: 404 sale.
        Assert.Equal("sale", (await Bar.VoidAsync(Server, cashier, Guid.NewGuid(), status: 404)).GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        var voidId = await Players.ScalarAsync<Guid>(Server, "SELECT id FROM shop_sales WHERE void_of = @other", new { other });
        Assert.Equal("sale", (await Bar.VoidAsync(Server, owner, voidId, status: 404)).GetProperty("error").GetProperty("details").GetProperty("what").GetString());

        // Two voids at once: one wins.
        for (var i = 0; i < 3; i++)
        {
            var sale = await Bar.CashAsync(Server, cashier, product, 100_000);
            var results = await Task.WhenAll(
                RawAsync(Server, HttpMethod.Post, $"/shop/sales/{sale}/void", cashier, new { reasonCode = "mistake" }, Guid.NewGuid()),
                RawAsync(Server, HttpMethod.Post, $"/shop/sales/{sale}/void", owner, new { reasonCode = "returned" }, Guid.NewGuid()));
            Assert.Equal([200, 409], results.Select(r => r.Status).Order());
            Contract.AssertError(results.Single(r => r.Status == 409).Body, "conflict", "alreadyVoided");
        }

        Assert.Equal(8, await Bar.StockAsync(Server, product));
    }

    [Fact]
    public async Task Third_void_of_a_cashier_in_a_shift_raises_suspicious_once_and_a_big_one_at_once()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 100_000, null);
        var big = await Bar.ProductAsync(Server, owner, 25_000_000, null);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            webhooks = new[] { new { id = "bar-watch", url = "https://hooks.example.com/watch", events = new[] { "suspicious" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null } },
        });
        try
        {
            var count = () => Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM webhook_outbox WHERE event = 'suspicious' AND payload ->> 'text' LIKE '%аннулирования продаж бара%'");
            for (var i = 1; i <= 4; i++)
            {
                await Bar.VoidAsync(Server, cashier, await Bar.CashAsync(Server, cashier, product, 100_000, method: "card"));
                Assert.Equal(i >= 3 ? 1 : 0, await count());
            }

            // The owner's own voids are counted for the owner.
            await Bar.VoidAsync(Server, owner, await Bar.CashAsync(Server, cashier, product, 100_000, method: "card"));
            Assert.Equal(1, await count());

            await Bar.VoidAsync(Server, cashier, await Bar.CashAsync(Server, cashier, big, 25_000_000, method: "click"));
            Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM webhook_outbox WHERE event = 'suspicious' AND payload ->> 'text' LIKE '%аннулирована продажа бара%'"));
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { webhooks = Array.Empty<object>() });
        }
    }
}

/// <summary>
/// The bar in the shift's money (D-57): the Z report keeps it by method with the voids (an old Z reads zeros), the feed's
/// drawer column still sums to the expected drawer, and «Сегодня» counts the bar's method money net of voids.
/// </summary>
public sealed class BarShiftTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Z_keeps_the_bar_by_method_and_the_voids_and_an_old_z_reads_zero()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier, openingCash: 50_000);
        var product = await Bar.ProductAsync(Server, owner, 100_000, null);
        var player = await Players.CreateAsync(Server, balance: 1_000_000);
        await Bar.CashAsync(Server, cashier, product, 100_000, qty: 10);
        var card = await Bar.CashAsync(Server, cashier, product, 100_000, qty: 5, method: "card");
        await Bar.SoldAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 300_000, null, player.Id, null, (product, 3)));
        await Bar.VoidAsync(Server, cashier, card);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            webhooks = new[] { new { id = "bar-z", url = "https://hooks.example.com/z", events = new[] { "shiftClosed" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null } },
        });
        try
        {
            var closed = await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 1_050_000 });
            Assert.Equal(1_050_000, closed.GetProperty("expectedCash").GetInt64());
            var z = closed.GetProperty("shift").GetProperty("totals");
            Assert.Equal((1_000_000L, 0L, 0L, 0L, 0L, 300_000L), Bar.ByMethod(z));
            Assert.Equal((1_300_000L, 500_000L, 1), (z.GetProperty("shop").GetInt64(), z.GetProperty("shopVoids").GetInt64(), z.GetProperty("shopVoidCount").GetInt32()));
            var text = await Players.ScalarAsync<string>(Server, "SELECT payload ->> 'text' FROM webhook_outbox WHERE event = 'shiftClosed' ORDER BY id DESC LIMIT 1");
            Assert.Contains($"бар нал. {ClubShell.Server.Admin.Webhooks.Sum(1_000_000)}", text, StringComparison.Ordinal);
            Assert.Contains($"аннулировано 1 на {ClubShell.Server.Admin.Webhooks.Sum(500_000)}", text, StringComparison.Ordinal);
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { webhooks = Array.Empty<object>() });
        }

        var legacy = Guid.NewGuid();
        await Players.ExecuteAsync(Server,
            """
            INSERT INTO shifts (id, club_id, staff_name, opened_at, closed_at, opening_cash, closing_cash, expected_cash, totals)
            SELECT @legacy, id, 'Кассир Азиз', @at, @at, 0, 0, 0,
                   '{"topUpCash":0,"topUpOther":0,"sessions":0,"shop":0,"refunds":0,"bonuses":0,"count":0,"topUpByMethod":{"cash":0,"card":0,"payme":0,"click":0,"uzum":0,"other":0},"cashIn":0,"cashOut":0,"payouts":0,"apiCash":0}'::jsonb
            FROM clubs
            """,
            new { legacy, at = Server.Clock.GetUtcNow().AddDays(-2) });
        var old = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier)).GetProperty("history").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == legacy).GetProperty("totals");
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), Bar.ByMethod(old));
        Assert.Equal((0L, 0), (old.GetProperty("shopVoids").GetInt64(), old.GetProperty("shopVoidCount").GetInt32()));
    }

    [Fact]
    public async Task The_feed_drawer_sums_to_expected_cash_and_today_counts_the_bar()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier, openingCash: 200_000);
        var before = (await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations", cashier)).GetProperty("today");
        var product = await Bar.ProductAsync(Server, owner, 100_000, null);
        var player = await Players.CreateAsync(Server, balance: 1_000_000);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 500_000, method = "cash" });
        await Bar.CashAsync(Server, cashier, product, 100_000, qty: 4);
        var voided = await Bar.CashAsync(Server, cashier, product, 100_000, qty: 2);
        await Bar.CashAsync(Server, cashier, product, 100_000, qty: 3, method: "click");
        await Bar.SoldAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 100_000, null, player.Id, null, (product, 1)));
        await Bar.VoidAsync(Server, cashier, voided);

        var page = await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations", cashier);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(200_000 + 500_000 + 400_000, await ExpectedCashAsync(Server, cashier));
        Assert.Equal(await ExpectedCashAsync(Server, cashier), items.Sum(i => i.GetProperty("drawer").GetInt64()));
        var balanceRow = items.Single(i => i.GetProperty("kind").GetString() == "shopSale" && i.GetProperty("method").GetString() == "balance");
        Assert.Equal((100_000L, JsonValueKind.Null, 0L), (balanceRow.GetProperty("charged").GetInt64(), balanceRow.GetProperty("paid").ValueKind, balanceRow.GetProperty("drawer").GetInt64()));

        var today = page.GetProperty("today");
        long Get(JsonElement t, string a, string? b = null) => b is null ? t.GetProperty(a).GetInt64() : t.GetProperty(a).GetProperty(b).GetInt64();
        Assert.Equal(400_000, Get(today, "shopByMethod", "cash") - Get(before, "shopByMethod", "cash"));
        Assert.Equal(300_000, Get(today, "shopByMethod", "click") - Get(before, "shopByMethod", "click"));
        Assert.Equal(500_000 + 700_000, Get(today, "taken") - Get(before, "taken"));
        Assert.Equal(500_000, Get(today, "byMethod", "cash") - Get(before, "byMethod", "cash"));
        Assert.Equal(800_000, Get(today, "shop") - Get(before, "shop"));
    }
}
