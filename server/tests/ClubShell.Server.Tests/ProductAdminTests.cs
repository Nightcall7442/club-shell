using System.Text.Json;
using ClubShell.Server.Admin;
using ClubShell.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Products at the desk (cash desk part 3, D-54, D-58): the owner creates and archives them (a cashier cannot), the seed
/// file neither deletes the desk's products nor revives what the desk archived (only what it deleted itself), and a quantity
/// edit names the quantity it saw, so a bar sale since then is not undone.
/// </summary>
public sealed class ProductAdminTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Owner_creates_and_archives_cashier_403()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        var created = (await RawExpectAsync(server, 201, HttpMethod.Post, "/products", owner,
            new { title = "  Чай с лимоном ", category = "drink", price = 600_000, stockQty = 12 }, Guid.NewGuid())).GetProperty("product");
        var id = created.GetProperty("id").GetGuid();
        Contract.AssertMatches("Product", created);
        Assert.Equal(("Чай с лимоном", "drink", 600_000L, true, 12), (created.GetProperty("title").GetString(), created.GetProperty("category").GetString(),
            Amount(created.GetProperty("price")), created.GetProperty("inStock").GetBoolean(), created.GetProperty("stockQty").GetInt32()));
        Assert.Equal("desk", await Players.ScalarAsync<string>(server, "SELECT source FROM products WHERE id = @id", new { id }));
        Assert.Equal("600000 Чай с лимоном", await Players.ScalarAsync<string>(server,
            "SELECT amount || ' ' || detail FROM audit_entries WHERE action = 'stockCreate' AND meta ->> 'productId' = @p", new { p = id.ToString() }));
        Assert.Contains((await ExpectAsync(server, 200, HttpMethod.Get, "/products", cashier)).GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);
        var untracked = (await RawExpectAsync(server, 201, HttpMethod.Post, "/products", owner, new { title = "Наушники", category = "service", price = 0, inStock = false }, Guid.NewGuid()))
            .GetProperty("product");
        Assert.Equal((JsonValueKind.Null, false), (untracked.GetProperty("stockQty").ValueKind, untracked.GetProperty("inStock").GetBoolean()));

        Contract.AssertError(await RawExpectAsync(server, 403, HttpMethod.Post, "/products", cashier, new { title = "X", category = "food", price = 1 }), "forbidden", "ownerOnly");
        Contract.AssertError(await RawExpectAsync(server, 403, HttpMethod.Delete, $"/products/{id}", cashier), "forbidden", "ownerOnly");
        foreach (var (body, field, reason) in new (object, string, string)[]
        {
            (new { category = "food", price = 1 }, "title", "required"), (new { title = "  ", category = "food", price = 1 }, "title", "required"),
            (new { title = new string('t', 81), category = "food", price = 1 }, "title", "max"), (new { title = "X", category = "toy", price = 1 }, "category", "enum"),
            (new { title = "X", category = "food" }, "price", "required"), (new { title = "X", category = "food", price = -1 }, "price", "min"),
            (new { title = "X", category = "food", price = 1_000_000_001L }, "price", "max"), (new { title = "X", category = "food", price = 1, stockQty = -1 }, "stockQty", "min"),
            (new { title = "X", category = "food", price = 1, inStock = (bool?)null }, "inStock", "format"),
        })
        {
            Assert.Equal((field, reason), Bar.Details(await RawExpectAsync(server, 400, HttpMethod.Post, "/products", owner, body)));
        }

        // Sold, then archived: gone from the list and the bar, the sale's line keeps its title.
        await OpenShiftAsync(server, cashier);
        var saleId = await Bar.CashAsync(server, cashier, id, 600_000);
        Assert.Equal("{\"ok\":true}", (await RawExpectAsync(server, 200, HttpMethod.Delete, $"/products/{id}", owner)).GetRawText());
        Assert.Equal("desk", await Players.ScalarAsync<string>(server, "SELECT deleted_by FROM products WHERE id = @id", new { id }));
        Assert.DoesNotContain((await ExpectAsync(server, 200, HttpMethod.Get, "/products", cashier)).GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);
        Assert.Equal("product", (await RawExpectAsync(server, 404, HttpMethod.Post, "/shop/sales", cashier, Bar.Sale(Guid.NewGuid(), 600_000, "cash", null, null, (id, 1)), Guid.NewGuid()))
            .GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        Assert.Equal("Чай с лимоном", await Players.ScalarAsync<string>(server, "SELECT title FROM shop_sale_lines WHERE sale_id = @saleId", new { saleId }));
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM audit_entries WHERE action = 'stockArchive' AND meta ->> 'productId' = @p", new { p = id.ToString() }));
        await RawExpectAsync(server, 404, HttpMethod.Delete, $"/products/{id}", owner);
        await RawExpectAsync(server, 404, HttpMethod.Delete, "/products/tea", owner);
    }

    [Fact]
    public async Task ProductSeed_keeps_desk_products_and_does_not_restore_a_desk_archived_seed_product_but_restores_its_own()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var db = server.Services.GetRequiredService<NpgsqlDataSource>();
        var (kept, archived, dropped) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var path = Path.Combine(Path.GetTempPath(), $"clubshell-products-{Guid.NewGuid():N}.json");
        string Seed(params Guid[] ids) => JsonSerializer.Serialize(ids.Select(id => new
        {
            id, title = "Seed " + id.ToString("N")[..4], category = "snack", price = new { amount = 300_000, currency = "UZS" }, imageUrl = "", inStock = true, stockQty = 10,
            tags = Array.Empty<string>(),
        }));
        async Task<HashSet<Guid>> LiveAsync() =>
            [.. (await ExpectAsync(server, 200, HttpMethod.Get, "/products", owner)).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid())];
        try
        {
            await File.WriteAllTextAsync(path, Seed(kept, archived, dropped));
            await ProductSeed.ApplyAsync(db, path, server.Clock);
            var desk = await Bar.ProductAsync(server, owner, 500_000, 3);
            await RawExpectAsync(server, 200, HttpMethod.Delete, $"/products/{archived}", owner);

            // The file again without one of its own: the seed hides that one, never the desk's, and does not revive the archived.
            await File.WriteAllTextAsync(path, Seed(kept, archived));
            await ProductSeed.ApplyAsync(db, path, server.Clock);
            var live = await LiveAsync();
            Assert.True(live.Contains(kept) && live.Contains(desk), "the seed's kept product and the desk's product stay");
            Assert.False(live.Contains(archived) || live.Contains(dropped), "archived at the desk and dropped from the file stay hidden");
            Assert.Equal(("desk", "seed"), (await Players.ScalarAsync<string>(server, "SELECT deleted_by FROM products WHERE id = @archived", new { archived }),
                await Players.ScalarAsync<string>(server, "SELECT deleted_by FROM products WHERE id = @dropped", new { dropped })));

            // Back in the file: the one the seed deleted itself returns, the desk's archive does not.
            await File.WriteAllTextAsync(path, Seed(kept, archived, dropped));
            await ProductSeed.ApplyAsync(db, path, server.Clock);
            live = await LiveAsync();
            Assert.True(live.Contains(dropped) && !live.Contains(archived));
            Assert.Null(await Players.ScalarAsync<string?>(server, "SELECT deleted_by FROM products WHERE id = @dropped", new { dropped }));

            // A product the seed hid before M0009 (deleted_by NULL) comes back too.
            await Players.ExecuteAsync(server, "UPDATE products SET deleted_at = now(), deleted_by = NULL WHERE id = @kept", new { kept });
            await ProductSeed.ApplyAsync(db, path, server.Clock);
            Assert.Contains(kept, await LiveAsync());
        }
        finally
        {
            File.Delete(path);
            await Players.ExecuteAsync(server, "UPDATE products SET deleted_at = NULL, deleted_by = NULL WHERE source = 'seed' AND deleted_by IS DISTINCT FROM 'desk'");
        }
    }

    [Fact]
    public async Task Patch_with_a_stale_expectedStockQty_is_409_stockChanged_and_a_price_patch_keeps_the_stock()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        await OpenShiftAsync(server, cashier);
        var product = await Bar.ProductAsync(server, owner, 100_000, 10);
        await Bar.CashAsync(server, cashier, product, 100_000, qty: 3);

        var refused = await RawExpectAsync(server, 409, HttpMethod.Patch, $"/products/{product}", owner, new { stockQty = 12, expectedStockQty = 10 });
        Contract.AssertError(refused, "conflict", "stockChanged");
        Assert.Equal(7, refused.GetProperty("error").GetProperty("details").GetProperty("stockQty").GetInt32());
        Assert.Equal(7, await Bar.StockAsync(server, product));
        Assert.Equal(12, (await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{product}", owner, new { stockQty = 12, expectedStockQty = 7 }))
            .GetProperty("product").GetProperty("stockQty").GetInt32());

        // Only the price changes: a sale in between is kept; expectedStockQty without stockQty checks nothing.
        await Bar.CashAsync(server, cashier, product, 100_000, qty: 2);
        var priced = (await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{product}", owner, new { price = 150_000, expectedStockQty = 12 })).GetProperty("product");
        Assert.Equal((150_000L, 10), (Amount(priced.GetProperty("price")), priced.GetProperty("stockQty").GetInt32()));

        // Untracked expected (null) and a negative one.
        var untracked = await Bar.ProductAsync(server, owner, 100_000, null);
        Assert.Equal(4, (await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{untracked}", owner, new { stockQty = 4, expectedStockQty = (int?)null }))
            .GetProperty("product").GetProperty("stockQty").GetInt32());
        Assert.Equal(("expectedStockQty", "min"), Bar.Details(await ExpectAsync(server, 400, HttpMethod.Patch, $"/products/{untracked}", owner, new { stockQty = 4, expectedStockQty = -1 })));
    }
}
