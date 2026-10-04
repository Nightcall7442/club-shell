using System.Text.Json;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions.Billing;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>The bar's requests (cash desk part 3, beyond the contract): products added at the desk, sales and voids.</summary>
public static class Bar
{
    /// <summary>A product the owner adds at the desk (<c>POST /admin/products</c>); its id.</summary>
    public static async Task<Guid> ProductAsync(ServerFixture server, string owner, long price, int? stockQty, string category = "drink", string? title = null) =>
        (await RawExpectAsync(server, 201, HttpMethod.Post, "/products", owner,
            new { title = title ?? "Товар " + Guid.NewGuid().ToString("N")[..6], category, price, stockQty }))
        .GetProperty("product").GetProperty("id").GetGuid();

    /// <summary>A sale body: <paramref name="method"/> null — from <paramref name="userId"/>'s balance.</summary>
    public static object Sale(Guid saleId, long total, string? method, Guid? userId, Guid? pcId, params (Guid ProductId, int Qty)[] items) => new
    {
        saleId, items = items.Select(i => new { productId = i.ProductId, qty = i.Qty }).ToArray(), total, userId, pcId,
        payment = method is null ? null : new { method, amount = total },
    };

    public static Task<(int Status, JsonElement Body, bool Replayed)> SellAsync(ServerFixture server, string token, object sale, Guid? key = null) =>
        RawAsync(server, HttpMethod.Post, "/shop/sales", token, sale, key ?? Guid.NewGuid());

    /// <summary>A sale that must be booked (201); the answer.</summary>
    public static Task<JsonElement> SoldAsync(ServerFixture server, string token, object sale, Guid? key = null) =>
        RawExpectAsync(server, 201, HttpMethod.Post, "/shop/sales", token, sale, key ?? Guid.NewGuid());

    /// <summary>A cash sale of one product priced <paramref name="price"/>, <paramref name="qty"/> of it; the sale's id.</summary>
    public static async Task<Guid> CashAsync(ServerFixture server, string token, Guid productId, long price, int qty = 1, string method = "cash")
    {
        var saleId = Guid.NewGuid();
        await SoldAsync(server, token, Sale(saleId, price * qty, method, null, null, (productId, qty)));
        return saleId;
    }

    public static Task<JsonElement> VoidAsync(ServerFixture server, string token, Guid saleId, string reasonCode = "mistake", string? note = null, int status = 200, Guid? key = null) =>
        RawExpectAsync(server, status, HttpMethod.Post, $"/shop/sales/{saleId}/void", token, new { reasonCode, note }, key ?? Guid.NewGuid());

    public static Task<int?> StockAsync(ServerFixture server, Guid productId) =>
        Players.ScalarAsync<int?>(server, "SELECT stock_qty FROM products WHERE id = @productId", new { productId });

    /// <summary>The open shift's X report.</summary>
    public static async Task<JsonElement> XAsync(ServerFixture server, string token) =>
        (await ExpectAsync(server, 200, HttpMethod.Get, "/shift", token)).GetProperty("x");

    public static (long Cash, long Card, long Payme, long Click, long Uzum, long Balance) ByMethod(JsonElement x)
    {
        var m = x.GetProperty("shopByMethod");
        return (m.GetProperty("cash").GetInt64(), m.GetProperty("card").GetInt64(), m.GetProperty("payme").GetInt64(), m.GetProperty("click").GetInt64(),
            m.GetProperty("uzum").GetInt64(), m.GetProperty("balance").GetInt64());
    }

    public static (string Field, string Reason) Details(JsonElement error) =>
        (error.GetProperty("error").GetProperty("details").GetProperty("field").GetString()!, error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString()!);
}

/// <summary>
/// Bar sales (cash desk part 3, D-52..D-55, D-70): staff only, in an open shift, under a required key with the strict body
/// check; the desk's <c>saleId</c> books a cart once; stock goes down by conditional updates and never writes
/// <c>in_stock</c>; any refusal books nothing; a method sale leaves the wallet alone, a balance sale posts a <c>purchase</c>
/// row and never goes past what an open postpaid session still owes; X/Z and the expected drawer count the bar.
/// </summary>
public sealed class ShopSaleTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Cash_sale_decrements_stock_books_shift_shop_and_expected_cash()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier, openingCash: 100_000);
        var cola = await Bar.ProductAsync(Server, owner, 800_000, 3, title: "Кола бар");
        var water = await Bar.ProductAsync(Server, owner, 400_000, null, title: "Вода бар");
        var pc = await TestAgent.CreateAsync(Server);
        var saleId = Guid.NewGuid();

        var sold = await Bar.SoldAsync(Server, cashier, Bar.Sale(saleId, 2_800_000, "cash", null, pc.PcId, (cola, 3), (water, 1)));
        var sale = sold.GetProperty("sale");
        Assert.Equal((saleId, "cash", 2_800_000L, "Кассир Азиз", JsonValueKind.Null, pc.PcId),
            (sale.GetProperty("id").GetGuid(), sale.GetProperty("method").GetString(), sale.GetProperty("total").GetInt64(), sale.GetProperty("staffName").GetString(),
             sale.GetProperty("user").ValueKind, sale.GetProperty("pc").GetProperty("id").GetGuid()));
        Assert.Equal([(cola, "Кола бар", 3, 800_000L, 2_400_000L), (water, "Вода бар", 1, 400_000L, 400_000L)], sale.GetProperty("lines").EnumerateArray().Select(l => (
            l.GetProperty("productId").GetGuid(), l.GetProperty("title").GetString(), l.GetProperty("qty").GetInt32(), l.GetProperty("price").GetInt64(), l.GetProperty("amount").GetInt64())));
        Assert.Equal(JsonValueKind.Null, sold.GetProperty("balance").ValueKind);
        var products = sold.GetProperty("products").EnumerateArray().ToList();
        Assert.Equal((0, true, JsonValueKind.Null), (products[0].GetProperty("stockQty").GetInt32(), products[0].GetProperty("inStock").GetBoolean(), products[1].GetProperty("stockQty").ValueKind));
        Assert.Equal(2_900_000, sold.GetProperty("expectedCash").GetInt64());

        // X: the bar by method; the expected drawer has the cash; no top-up was made.
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((2_800_000L, 0L, 0L, 0), (x.GetProperty("shop").GetInt64(), x.GetProperty("topUpCash").GetInt64(), x.GetProperty("shopVoids").GetInt64(), x.GetProperty("shopVoidCount").GetInt32()));
        Assert.Equal((2_800_000L, 0L, 0L, 0L, 0L, 0L), Bar.ByMethod(x));
        Assert.Equal(2_900_000, await ExpectedCashAsync(Server, cashier));

        // A sale never writes in_stock: sold out at 0, still «В продаже»; the lines and the journal entry are stored.
        Assert.True(await Players.ScalarAsync<bool>(Server, "SELECT in_stock FROM products WHERE id = @cola", new { cola }));
        Assert.Equal(2, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM shop_sale_lines WHERE sale_id = @saleId", new { saleId }));
        Assert.Equal("2800000 cash Кола бар ×3, Вода бар", await Players.ScalarAsync<string>(Server,
            "SELECT amount || ' ' || (meta ->> 'method') || ' ' || detail FROM audit_entries WHERE action = 'shopSale' AND meta ->> 'saleId' = @id", new { id = saleId.ToString() }));
        var (status, refused, _) = await Bar.SellAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 800_000, "cash", null, null, (cola, 1)));
        Assert.Equal(409, status);
        Contract.AssertError(refused, "conflict", "outOfStock");
        Assert.Equal((cola, 0), (refused.GetProperty("error").GetProperty("details").GetProperty("productId").GetGuid(),
            refused.GetProperty("error").GetProperty("details").GetProperty("available").GetInt32()));
    }

    [Fact]
    public async Task Sale_needs_key_open_shift_and_staff()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        var product = await Bar.ProductAsync(Server, owner, 500_000, 10);
        object Body() => Bar.Sale(Guid.NewGuid(), 500_000, "cash", null, null, (product, 1));

        await FreshShiftAsync(Server, cashier);
        Assert.Equal(("Idempotency-Key", "required"), Bar.Details(await RawExpectAsync(Server, 400, HttpMethod.Post, "/shop/sales", cashier, Body())));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 0 });
        Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/shop/sales", cashier, Body(), Guid.NewGuid()), "conflict", "shiftClosed");
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/open", cashier, new { openingCash = 0 });
        Contract.AssertError(await RawExpectAsync(Server, 403, HttpMethod.Post, "/shop/sales", await ApiKeyAsync(Server), Body(), Guid.NewGuid()), "forbidden", "staffOnly");
        await ClearApiKeyAsync(Server);
        Assert.Equal(10, await Bar.StockAsync(Server, product));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM shop_sale_lines WHERE product_id = @product", new { product }));

        // Every field is checked before anything is written.
        var player = await Players.CreateAsync(Server);
        foreach (var (body, field, reason) in new (object, string, string)[]
        {
            (new { items = new[] { new { productId = product, qty = 1 } }, total = 500_000, payment = new { method = "cash", amount = 500_000 } }, "saleId", "required"),
            (new { saleId = "x", items = new[] { new { productId = product, qty = 1 } }, total = 500_000 }, "saleId", "format"),
            (new { saleId = Guid.NewGuid(), items = Array.Empty<object>(), total = 500_000, userId = player.Id }, "items", "required"),
            (new { saleId = Guid.NewGuid(), items = Enumerable.Range(0, 21).Select(_ => new { productId = Guid.NewGuid(), qty = 1 }), total = 500_000, userId = player.Id }, "items", "max"),
            (new { saleId = Guid.NewGuid(), items = new[] { new { productId = product, qty = 1 }, new { productId = product, qty = 2 } }, total = 500_000, userId = player.Id }, "items", "duplicate"),
            (new { saleId = Guid.NewGuid(), items = new[] { new { productId = product, qty = 0 } }, total = 500_000, userId = player.Id }, "items[0].qty", "min"),
            (new { saleId = Guid.NewGuid(), items = new[] { new { productId = product, qty = 100 } }, total = 500_000, userId = player.Id }, "items[0].qty", "max"),
            (new { saleId = Guid.NewGuid(), items = new[] { new { productId = product, qty = 1 } }, total = 0, userId = player.Id }, "total", "min"),
            (new { saleId = Guid.NewGuid(), items = new[] { new { productId = product, qty = 1 } }, total = 100_000_001, userId = player.Id }, "total", "max"),
            (new { saleId = Guid.NewGuid(), items = new[] { new { productId = product, qty = 1 } }, total = 500_000 }, "userId", "required"),
            (new { saleId = Guid.NewGuid(), items = new[] { new { productId = product, qty = 1 } }, total = 500_000, payment = new { method = "iou", amount = 500_000 } }, "payment.method", "enum"),
            (new { saleId = Guid.NewGuid(), items = new[] { new { productId = product, qty = 1 } }, total = 500_000, payment = new { method = "cash", amount = 400_000 } }, "payment.amount", "total"),
            (new { saleId = Guid.NewGuid(), items = new[] { new { productId = product, qty = 1 } }, total = 500_000, pcId = "pc-1", payment = new { method = "cash", amount = 500_000 } }, "pcId", "format"),
        })
        {
            Assert.Equal((field, reason), Bar.Details(await RawExpectAsync(Server, 400, HttpMethod.Post, "/shop/sales", cashier, body, Guid.NewGuid())));
        }

        foreach (var (body, what) in new (object, string)[]
        {
            (Bar.Sale(Guid.NewGuid(), 500_000, "cash", null, null, (Guid.NewGuid(), 1)), "product"),
            (Bar.Sale(Guid.NewGuid(), 500_000, null, Guid.NewGuid(), null, (product, 1)), "user"),
            (Bar.Sale(Guid.NewGuid(), 500_000, "cash", null, Guid.NewGuid(), (product, 1)), "pc"),
        })
        {
            Assert.Equal(what, (await RawExpectAsync(Server, 404, HttpMethod.Post, "/shop/sales", cashier, body, Guid.NewGuid())).GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        }

        Assert.Equal(10, await Bar.StockAsync(Server, product));
    }

    [Fact]
    public async Task Same_key_replays_once_and_same_key_other_body_is_409_idempotencyKeyReused()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 300_000, 5);
        var key = Guid.NewGuid();
        var body = Bar.Sale(Guid.NewGuid(), 600_000, "card", null, null, (product, 2));

        var first = await Bar.SoldAsync(Server, cashier, body, key);
        var (status, again, replayed) = await Bar.SellAsync(Server, cashier, body, key);
        Assert.Equal((201, true), (status, replayed));
        Assert.True(JsonElement.DeepEquals(first, again));
        Assert.Equal(3, await Bar.StockAsync(Server, product));

        var other = Bar.Sale(Guid.NewGuid(), 300_000, "card", null, null, (product, 1));
        Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/shop/sales", cashier, other, key), "conflict", "idempotencyKeyReused");
        Assert.Equal(3, await Bar.StockAsync(Server, product));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM shop_sale_lines WHERE product_id = @product", new { product }));
    }

    [Fact]
    public async Task Same_saleId_under_a_new_key_is_409_saleExists_with_the_sale()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 250_000, 9);
        var saleId = Guid.NewGuid();
        var first = await Bar.SoldAsync(Server, cashier, Bar.Sale(saleId, 750_000, "cash", null, null, (product, 3)));

        foreach (var body in new[] { Bar.Sale(saleId, 750_000, "cash", null, null, (product, 3)), Bar.Sale(saleId, 250_000, "click", null, null, (product, 1)) })
        {
            var refused = await RawExpectAsync(Server, 409, HttpMethod.Post, "/shop/sales", cashier, body, Guid.NewGuid());
            Contract.AssertError(refused, "conflict", "saleExists");
            Assert.True(JsonElement.DeepEquals(first.GetProperty("sale"), refused.GetProperty("error").GetProperty("details").GetProperty("sale")),
                refused.GetRawText());
        }

        // Two new keys for one cart at the same instant: booked once.
        var racing = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Bar.SellAsync(Server, cashier, Bar.Sale(racing, 250_000, "cash", null, null, (product, 1)))));
        Assert.Equal([201, 409, 409], results.Select(r => r.Status).Order());
        Assert.All(results.Where(r => r.Status == 409), r => Contract.AssertError(r.Body, "conflict", "saleExists"));
        Assert.Equal(5, await Bar.StockAsync(Server, product));
    }

    [Fact]
    public async Task Out_of_stock_rolls_back_the_whole_cart()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var plenty = await Bar.ProductAsync(Server, owner, 100_000, 5);
        var scarce = await Bar.ProductAsync(Server, owner, 200_000, 2);
        var refused = await RawExpectAsync(Server, 409, HttpMethod.Post, "/shop/sales", cashier,
            Bar.Sale(Guid.NewGuid(), 700_000, "cash", null, null, (plenty, 1), (scarce, 3)), Guid.NewGuid());
        Contract.AssertError(refused, "conflict", "outOfStock");
        Assert.Equal((scarce, 2), (refused.GetProperty("error").GetProperty("details").GetProperty("productId").GetGuid(),
            refused.GetProperty("error").GetProperty("details").GetProperty("available").GetInt32()));
        Assert.Equal((5, 2), (await Bar.StockAsync(Server, plenty), await Bar.StockAsync(Server, scarce)));

        // Taken off sale by the owner: nothing is available, whatever the quantity.
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/products/{scarce}", owner, new { inStock = false });
        var off = await RawExpectAsync(Server, 409, HttpMethod.Post, "/shop/sales", cashier, Bar.Sale(Guid.NewGuid(), 200_000, "cash", null, null, (scarce, 1)), Guid.NewGuid());
        Assert.Equal(0, off.GetProperty("error").GetProperty("details").GetProperty("available").GetInt32());
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM shop_sale_lines WHERE product_id IN (@plenty, @scarce)", new { plenty, scarce }));
    }

    [Fact]
    public async Task Two_parallel_sales_of_the_last_unit_and_opposite_line_orders_do_not_deadlock()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        for (var i = 0; i < 4; i++)
        {
            var last = await Bar.ProductAsync(Server, owner, 100_000, 1);
            var results = await Task.WhenAll(
                Bar.SellAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 100_000, "cash", null, null, (last, 1))),
                Bar.SellAsync(Server, owner, Bar.Sale(Guid.NewGuid(), 100_000, "card", null, null, (last, 1))));
            Assert.Equal([201, 409], results.Select(r => r.Status).Order());
            Contract.AssertError(results.Single(r => r.Status == 409).Body, "conflict", "outOfStock");
            Assert.Equal(0, await Bar.StockAsync(Server, last));
        }

        var x = await Bar.ProductAsync(Server, owner, 100_000, 20);
        var y = await Bar.ProductAsync(Server, owner, 200_000, 20);
        for (var i = 0; i < 4; i++)
        {
            var results = await Task.WhenAll(
                Bar.SellAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 300_000, "cash", null, null, (x, 1), (y, 1))),
                Bar.SellAsync(Server, owner, Bar.Sale(Guid.NewGuid(), 300_000, "cash", null, null, (y, 1), (x, 1))));
            Assert.All(results, r => Assert.Equal(201, r.Status));
        }

        Assert.Equal((12, 12), (await Bar.StockAsync(Server, x), await Bar.StockAsync(Server, y)));
    }

    [Fact]
    public async Task Price_changed_is_409_untracked_sells_and_time_is_not_sellable()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 500_000, 4);
        var untracked = await Bar.ProductAsync(Server, owner, 100_000, null);
        var refused = await RawExpectAsync(Server, 409, HttpMethod.Post, "/shop/sales", cashier,
            Bar.Sale(Guid.NewGuid(), 800_000, "cash", null, null, (product, 1), (untracked, 2)), Guid.NewGuid());
        Contract.AssertError(refused, "conflict", "priceChanged");
        var details = refused.GetProperty("error").GetProperty("details");
        Assert.Equal(700_000, Amount(details.GetProperty("total")));
        Assert.Equal([(product, 500_000L), (untracked, 100_000L)], details.GetProperty("prices").EnumerateArray().Select(p => (p.GetProperty("productId").GetGuid(), Amount(p.GetProperty("price")))));
        Assert.Equal(4, await Bar.StockAsync(Server, product));

        // Untracked goods sell any quantity and stay untracked.
        await Bar.SoldAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 9_900_000, "uzum", null, null, (untracked, 99)));
        Assert.Null(await Bar.StockAsync(Server, untracked));

        // Time may be created for a later kiosk, but the bar never sells it.
        var time = await Bar.ProductAsync(Server, owner, 1_000_000, null, category: "time");
        var notSellable = await RawExpectAsync(Server, 409, HttpMethod.Post, "/shop/sales", cashier, Bar.Sale(Guid.NewGuid(), 1_000_000, "cash", null, null, (time, 1)), Guid.NewGuid());
        Contract.AssertError(notSellable, "conflict", "notSellable");
        Assert.Equal(time, notSellable.GetProperty("error").GetProperty("details").GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task Sold_out_product_sells_again_after_a_quantity_patch_without_inStock()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 100_000, 1);
        await Bar.CashAsync(Server, cashier, product, 100_000);
        Assert.Equal(409, (await Bar.SellAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 100_000, "cash", null, null, (product, 1)))).Status);
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/products/{product}", owner, new { stockQty = 5 });
        await Bar.CashAsync(Server, cashier, product, 100_000, qty: 2);
        Assert.Equal(3, await Bar.StockAsync(Server, product));
    }

    [Fact]
    public async Task Balance_sale_posts_a_purchase_row_and_counts_lifetime_spent()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        var shiftId = await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 500_000, 10);
        var player = await Players.CreateAsync(Server, balance: 3_000_000);
        var spent = await Players.ScalarAsync<long>(Server, "SELECT lifetime_spent FROM wallets WHERE user_id = @Id", new { player.Id });
        var saleId = Guid.NewGuid();

        var sold = await Bar.SoldAsync(Server, cashier, Bar.Sale(saleId, 1_000_000, null, player.Id, null, (product, 2)));
        Assert.Equal((2_000_000L, "balance", player.Id, player.Username), (Amount(sold.GetProperty("balance")), sold.GetProperty("sale").GetProperty("method").GetString(),
            sold.GetProperty("sale").GetProperty("user").GetProperty("id").GetGuid(), sold.GetProperty("sale").GetProperty("user").GetProperty("displayName").GetString()));
        Assert.Equal(2_000_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(spent + 1_000_000, await Players.ScalarAsync<long>(Server, "SELECT lifetime_spent FROM wallets WHERE user_id = @Id", new { player.Id }));
        Assert.Equal($"purchase -1000000 {saleId} {shiftId} true", await Players.ScalarAsync<string>(Server,
            "SELECT type || ' ' || amount || ' ' || ref || ' ' || shift_id || ' ' || (method IS NULL) FROM ledger_entries WHERE user_id = @Id AND type = 'purchase'", new { player.Id }));
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((1_000_000L, 1_000_000L), (x.GetProperty("shop").GetInt64(), Bar.ByMethod(x).Balance));
        Assert.Equal(0, await ExpectedCashAsync(Server, cashier));

        // The player's history names the goods.
        Assert.StartsWith("Покупка в баре: ", await Players.ScalarAsync<string>(Server, "SELECT description FROM ledger_entries WHERE ref = @id", new { id = saleId.ToString() }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Method_sale_with_userId_names_the_buyer_and_leaves_the_wallet_and_lifetime_spent()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 450_000, 10);
        var player = await Players.CreateAsync(Server, balance: 100_000);
        var rows = await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id", new { player.Id });

        var sold = await Bar.SoldAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), 900_000, "card", player.Id, null, (product, 2)));
        Assert.Equal((player.Id, JsonValueKind.Null), (sold.GetProperty("sale").GetProperty("user").GetProperty("id").GetGuid(), sold.GetProperty("balance").ValueKind));
        Assert.Equal(100_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(rows, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id", new { player.Id }));
        Assert.Equal(0, await Players.ScalarAsync<long>(Server, "SELECT lifetime_spent FROM wallets WHERE user_id = @Id", new { player.Id }));
        var x = await Bar.XAsync(Server, cashier);
        Assert.Equal((900_000L, 0L, 900_000L), (Bar.ByMethod(x).Card, x.GetProperty("topUpOther").GetInt64(), x.GetProperty("shop").GetInt64()));

        // The feed names the client; the card money is not in the drawer.
        var row = (await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?kinds=shopSale", cashier)).GetProperty("items")[0];
        Assert.Equal((player.Id, "card", 900_000L, 0L, false), (row.GetProperty("client").GetProperty("id").GetGuid(), row.GetProperty("method").GetString(),
            row.GetProperty("paid").GetProperty("amount").GetInt64(), row.GetProperty("drawer").GetInt64(), row.GetProperty("voided").GetBoolean()));
    }

    [Fact]
    public async Task Balance_short_is_402_and_an_open_postpaid_session_reserves_played_time_plus_a_minute()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var cheap = await Bar.ProductAsync(Server, owner, 100_000, null);
        var player = await Players.CreateAsync(Server, balance: 500_000);
        var refused = await RawExpectAsync(Server, 402, HttpMethod.Post, "/shop/sales", cashier, Bar.Sale(Guid.NewGuid(), 800_000, null, player.Id, null, (cheap, 8)), Guid.NewGuid());
        Contract.AssertError(refused, "insufficientFunds");
        Assert.Equal((800_000L, 500_000L), (Amount(refused.GetProperty("error").GetProperty("details").GetProperty("required")),
            Amount(refused.GetProperty("error").GetProperty("details").GetProperty("available"))));
        Assert.Equal(500_000, await Players.BalanceAsync(Server, player.Id));

        // Postpaid for 10 minutes: what the session would charge a minute from now stays reserved.
        var gamer = await Players.CreateAsync(Server, balance: 2_000_000);
        var agent = await TestAgent.CreateAsync(Server);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = gamer.Id, tariffId = Players.Standard, minutes = 60, prepaid = false });
        Server.Clock.Advance(TimeSpan.FromMinutes(10));
        const string Open = "FROM sessions WHERE user_id = @Id AND state <> 'ended'";
        var price = await Players.ScalarAsync<long>(Server, $"SELECT price_per_hour_snapshot {Open}", new { gamer.Id });
        var dayPct = await Players.ScalarAsync<int>(Server, $"SELECT day_pct {Open}", new { gamer.Id });
        var discountPct = await Players.ScalarAsync<int>(Server, $"SELECT discount_pct {Open}", new { gamer.Id });
        var available = 2_000_000 - Pricing.Frozen(price, 660, dayPct, discountPct);
        Assert.True(available is > 0 and < 2_000_000);
        var exact = await Bar.ProductAsync(Server, owner, available, null);
        var more = await Bar.ProductAsync(Server, owner, available + 100, null);
        var reserved = await RawExpectAsync(Server, 402, HttpMethod.Post, "/shop/sales", cashier, Bar.Sale(Guid.NewGuid(), available + 100, null, gamer.Id, null, (more, 1)), Guid.NewGuid());
        Assert.Equal(available, Amount(reserved.GetProperty("error").GetProperty("details").GetProperty("available")));
        await Bar.SoldAsync(Server, cashier, Bar.Sale(Guid.NewGuid(), available, null, gamer.Id, null, (exact, 1)));
        Assert.Equal(2_000_000 - available, await Players.BalanceAsync(Server, gamer.Id));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
    }

    [Fact]
    public async Task Guest_balance_sale_lowers_payable_and_its_void_restores_it()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var pc = await TestAgent.CreateAsync(Server);
        var guest = (await DeskGuests.SeatAsync(Server, cashier, pc.PcId)).GetProperty("user").GetProperty("id").GetGuid();
        var payable = Amount((await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = pc.PcId })).GetProperty("payable"));
        Assert.True(payable > 300_000);
        var snack = await Bar.ProductAsync(Server, owner, 300_000, 5);

        var saleId = Guid.NewGuid();
        await Bar.SoldAsync(Server, cashier, Bar.Sale(saleId, 300_000, null, guest, pc.PcId, (snack, 1)));
        Assert.Equal(payable - 300_000, await PayableAsync(cashier, guest));
        await Bar.VoidAsync(Server, cashier, saleId, "returned");
        Assert.Equal(payable, await PayableAsync(cashier, guest));
        Assert.Equal(5, await Bar.StockAsync(Server, snack));
    }

    [Fact]
    public async Task LowStock_webhook_once_per_crossing()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var product = await Bar.ProductAsync(Server, owner, 100_000, 8, title: "Low bar");
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            webhooks = new[] { new { id = "bar-stock", url = "https://hooks.example.com/stock", events = new[] { "lowStock" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null } },
        });
        try
        {
            var events = () => Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM webhook_outbox WHERE event = 'lowStock' AND payload ->> 'text' LIKE 'Low bar%'");
            await Bar.CashAsync(Server, cashier, product, 100_000, qty: 2); // 6
            Assert.Equal(0, await events());
            await Bar.CashAsync(Server, cashier, product, 100_000, qty: 2); // 4: crossed
            Assert.Equal(1, await events());
            await Bar.CashAsync(Server, cashier, product, 100_000); // 3: still below
            Assert.Equal(1, await events());
            await ExpectAsync(Server, 200, HttpMethod.Patch, $"/products/{product}", owner, new { stockQty = 9 });
            await Bar.CashAsync(Server, cashier, product, 100_000, qty: 5); // 4: crossed again
            Assert.Equal(2, await events());
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { webhooks = Array.Empty<object>() });
        }
    }

    private async Task<long> PayableAsync(string token, Guid guest) =>
        Amount((await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", token)).GetProperty("guestRefunds").EnumerateArray()
            .Single(r => r.GetProperty("userId").GetGuid() == guest).GetProperty("payable"));
}

/// <summary>A balance sale tells the player's PC at once (<c>walletUpdated</c>); a method sale does not touch the wallet.</summary>
public sealed class BarPushTests(KestrelServerFixture server) : LedgerCheckedTest(server), IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task Balance_sale_and_its_void_push_the_wallet_to_the_players_pc()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var (agent, player) = await Players.SignedInAsync(Server, balance: 2_000_000);
        using var socket = await WsTestSocket.ConnectAsync((KestrelServerFixture)Server, agent.AccessToken);
        await Wait.UntilAsync(() => Server.Services.GetRequiredService<AgentSocketHub>().IsConnected(agent.PcId));
        var product = await Bar.ProductAsync(Server, owner, 700_000, null);

        var saleId = Guid.NewGuid();
        await Bar.SoldAsync(Server, cashier, Bar.Sale(saleId, 700_000, null, player.Id, agent.PcId, (product, 1)));
        var frame = await socket.ReceiveAsync();
        Contract.AssertMessage("pushWalletUpdated", frame);
        Assert.Equal((player.Id, 1_300_000L), (frame.GetProperty("payload").GetProperty("userId").GetGuid(), Amount(frame.GetProperty("payload").GetProperty("amount"))));

        await Bar.VoidAsync(Server, cashier, saleId);
        Assert.Equal(2_000_000, Amount((await socket.ReceiveAsync()).GetProperty("payload").GetProperty("amount")));
    }
}
