using System.Text.Json;
using ClubShell.Server.Admin;
using ClubShell.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Stock at the counter (slice S5): the products and <c>lowAt</c>, the owner's partial edit (<c>stockEdit</c> only when the
/// quantity changes), receiving goods atomically and once per <c>Idempotency-Key</c> (<c>stockReceive</c>), and the product
/// seed (D-14) that never overwrites the console's edits; the <c>lowStock</c> event when a quantity crosses <c>lowAt</c>
/// (<see cref="StockEndpoints.StockChangedAsync"/>).
/// </summary>
public sealed class StockTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static readonly Guid Cola = DevSeed.Sid("product:cola");
    private static readonly Guid Americano = DevSeed.Sid("product:americano");
    private static readonly Guid Snickers = DevSeed.Sid("product:snickers");
    private static readonly Guid Popcorn = DevSeed.Sid("product:popcorn");

    [Fact]
    public async Task Products_are_listed_edited_by_the_owner_and_received_by_anyone()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        var list = await ExpectAsync(server, 200, HttpMethod.Get, "/products", cashier);
        Assert.Equal(5, list.GetProperty("lowAt").GetInt32());
        Assert.True(list.GetProperty("items").GetArrayLength() >= 12);
        var snickers = Product(list, Snickers);
        Assert.Equal((false, 0, 700_000L), (snickers.GetProperty("inStock").GetBoolean(), snickers.GetProperty("stockQty").GetInt32(), snickers.GetProperty("price").GetProperty("amount").GetInt64()));
        Assert.Equal(JsonValueKind.Null, Product(list, Americano).GetProperty("stockQty").ValueKind);

        Contract.AssertError(await ExpectAsync(server, 403, HttpMethod.Patch, $"/products/{Cola}", cashier, new { stockQty = "x" }), "forbidden", "ownerOnly");
        var cola = (await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{Cola}", owner, new { stockQty = 3, title = "Cola" })).GetProperty("product");
        Assert.Equal((3, "Cola"), (cola.GetProperty("stockQty").GetInt32(), cola.GetProperty("title").GetString()));
        Assert.Equal("48 3", await Players.ScalarAsync<string>(server,
            "SELECT (meta ->> 'before') || ' ' || (meta ->> 'after') FROM audit_entries WHERE action = 'stockEdit' AND meta ->> 'productId' = @id", new { id = Cola.ToString() }));
        cola = (await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{Cola}", owner, new { price = 900_000, inStock = false })).GetProperty("product");
        Assert.Equal((900_000L, false, 3), (cola.GetProperty("price").GetProperty("amount").GetInt64(), cola.GetProperty("inStock").GetBoolean(), cola.GetProperty("stockQty").GetInt32()));
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM audit_entries WHERE action = 'stockEdit' AND meta ->> 'productId' = @id", new { id = Cola.ToString() }));
        cola = (await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{Cola}", owner, new { stockQty = (int?)null })).GetProperty("product");
        Assert.Equal(JsonValueKind.Null, cola.GetProperty("stockQty").ValueKind);

        foreach (var (body, field, reason) in new (object, string, string)[]
                 {
                     (new { stockQty = -1 }, "stockQty", "min"), (new { stockQty = 1_000_001 }, "stockQty", "max"), (new { title = "" }, "title", "required"),
                     (new { price = 1_000_000_001L }, "price", "max"), (new { inStock = (bool?)null }, "inStock", "format"),
                 })
        {
            Assert.Equal((field, reason), StaffAdminTests.Details(await ExpectAsync(server, 400, HttpMethod.Patch, $"/products/{Cola}", owner, body)));
        }

        foreach (var unknown in new[] { Guid.NewGuid().ToString(), "cola" })
        {
            Assert.Equal("product", (await ExpectAsync(server, 404, HttpMethod.Patch, $"/products/{unknown}", owner, new { title = "X" }))
                .GetProperty("error").GetProperty("details").GetProperty("what").GetString());
            Assert.Equal("product", (await ExpectAsync(server, 404, HttpMethod.Post, $"/products/{unknown}/receive", cashier, new { qty = 1 }))
                .GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        }

        // Receiving: an untracked quantity counts from 0, the product is in stock again; a retried delivery is applied once.
        var americano = (await ExpectAsync(server, 200, HttpMethod.Post, $"/products/{Americano}/receive", cashier, new { qty = 5 })).GetProperty("product");
        Assert.Equal((5, true), (americano.GetProperty("stockQty").GetInt32(), americano.GetProperty("inStock").GetBoolean()));
        var received = (await ReplayedAsync(server, 200, HttpMethod.Post, $"/products/{Snickers}/receive", cashier, new { qty = 2 })).GetProperty("product");
        Assert.Equal((2, true), (received.GetProperty("stockQty").GetInt32(), received.GetProperty("inStock").GetBoolean()));
        Assert.Equal(2, await Players.ScalarAsync<int>(server, "SELECT stock_qty FROM products WHERE id = @Snickers", new { Snickers }));
        Assert.Equal("Snickers +2", await Players.ScalarAsync<string>(server,
            "SELECT detail FROM audit_entries WHERE action = 'stockReceive' AND meta ->> 'productId' = @id", new { id = Snickers.ToString() }));
        foreach (var (body, reason) in new (object, string)[] { (new { qty = 0 }, "min"), (new { qty = 100_001 }, "max"), (new { }, "required") })
        {
            Assert.Equal(("qty", reason), StaffAdminTests.Details(await ExpectAsync(server, 400, HttpMethod.Post, $"/products/{Snickers}/receive", cashier, body)));
        }

        await Players.ExecuteAsync(server, "UPDATE clubs SET settings = settings || '{\"stock\":{\"lowAt\":7}}'::jsonb");
        try
        {
            Assert.Equal(7, (await ExpectAsync(server, 200, HttpMethod.Get, "/products", owner)).GetProperty("lowAt").GetInt32());
        }
        finally
        {
            await Players.ExecuteAsync(server, "UPDATE clubs SET settings = settings - 'stock'");
        }
    }

    [Fact]
    public async Task Parallel_deliveries_all_add_up()
    {
        var cashier = await LoginAsync(server, CashierPin);
        var before = await Players.ScalarAsync<int>(server, "SELECT stock_qty FROM products WHERE id = @Popcorn", new { Popcorn });
        var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            SendAsync(server, HttpMethod.Post, $"/products/{Popcorn}/receive", cashier, new { qty = 3 }, Guid.NewGuid())));
        Assert.All(answers, a => Assert.Equal(200, a.Status));
        Assert.Equal(before + 36, await Players.ScalarAsync<int>(server, "SELECT stock_qty FROM products WHERE id = @Popcorn", new { Popcorn }));
        Assert.Equal(Enumerable.Range(1, 12).Select(i => before + (3 * i)), answers.Select(a => a.Body.GetProperty("product").GetProperty("stockQty").GetInt32()).Order());
    }

    [Fact]
    public async Task The_product_seed_adds_hides_and_restores_but_never_overwrites_an_edit()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var db = server.Services.GetRequiredService<NpgsqlDataSource>();
        var id = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), $"clubshell-products-{Guid.NewGuid():N}.json");
        string Seed(string title) => JsonSerializer.Serialize(new[]
        {
            new { id, title, category = "drink", price = new { amount = 500_000, currency = "UZS" }, imageUrl = "", inStock = true, stockQty = 10, tags = new[] { "new" } },
        });
        try
        {
            await File.WriteAllTextAsync(path, Seed("Компот"));
            await ProductSeed.ApplyAsync(db, path, server.Clock);
            var list = await ExpectAsync(server, 200, HttpMethod.Get, "/products", owner);
            Assert.Equal(("Компот", 10), (Product(list, id).GetProperty("title").GetString(), Product(list, id).GetProperty("stockQty").GetInt32()));
            Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == Cola);

            await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{id}", owner, new { title = "Компот домашний", stockQty = 4 });
            await File.WriteAllTextAsync(path, Seed("Компот из файла"));
            await ProductSeed.ApplyAsync(db, path, server.Clock);
            var kept = Product(await ExpectAsync(server, 200, HttpMethod.Get, "/products", owner), id);
            Assert.Equal(("Компот домашний", 4), (kept.GetProperty("title").GetString(), kept.GetProperty("stockQty").GetInt32()));

            await File.WriteAllTextAsync(path, "[]");
            await ProductSeed.ApplyAsync(db, path, server.Clock);
            Assert.Equal(0, (await ExpectAsync(server, 200, HttpMethod.Get, "/products", owner)).GetProperty("items").GetArrayLength());
            await ExpectAsync(server, 404, HttpMethod.Post, $"/products/{id}/receive", owner, new { qty = 1 });
            await File.WriteAllTextAsync(path, Seed("Компот"));
            await ProductSeed.ApplyAsync(db, path, server.Clock);
            Assert.Equal("Компот домашний", Product(await ExpectAsync(server, 200, HttpMethod.Get, "/products", owner), id).GetProperty("title").GetString());
        }
        finally
        {
            File.Delete(path);

            // The other tests of this class want the demo products back.
            await Players.ExecuteAsync(server, "UPDATE products SET deleted_at = NULL");
        }
    }

    [Fact]
    public async Task Crossing_lowAt_downwards_raises_lowStock_once_per_crossing()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var burger = DevSeed.Sid("product:burger");
        await ExpectAsync(server, 200, HttpMethod.Patch, "/club", owner, new
        {
            webhooks = new[] { new { id = "stock", url = "https://hooks.example.com/stock", events = new[] { "lowStock" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null } },
        });
        var events = () => Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM webhook_outbox WHERE event = 'lowStock'");

        await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{burger}", owner, new { stockQty = 6 });
        Assert.Equal(0, await events());
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{burger}", owner, new { stockQty = 4 });
        Assert.Equal(1, await events());
        var payload = JsonElement.Parse(await Players.ScalarAsync<string>(server, "SELECT payload::text FROM webhook_outbox WHERE event = 'lowStock'"));
        Contract.AssertMatches("AdminWebhookPayload", payload);
        Assert.Equal(("Club Burger: осталось 4", 4), (payload.GetProperty("text").GetString(), payload.GetProperty("data").GetProperty("qty").GetInt32()));

        await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{burger}", owner, new { stockQty = 3 }); // still below: no repeat
        await ExpectAsync(server, 200, HttpMethod.Post, $"/products/{burger}/receive", owner, new { qty = 10 });
        Assert.Equal(1, await events());
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/products/{burger}", owner, new { stockQty = 5 }); // at lowAt again
        Assert.Equal(2, await events());
    }

    private static JsonElement Product(JsonElement list, Guid id) =>
        list.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);
}
