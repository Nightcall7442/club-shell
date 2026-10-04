using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Shop;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The shop's stock at the counter (slice S5): <c>adminProducts</c> — every product of the club, out of stock too, and the
/// <c>lowAt</c> threshold (<c>settings.stock.lowAt</c>, 5 when unset, as the mock's default); the owner edits a product
/// (<c>adminUpdateProduct</c>, partial; a changed quantity is journaled <c>stockEdit</c>); anyone receives goods
/// (<c>adminReceiveProduct</c>: <c>stock_qty = coalesce(stock_qty, 0) + qty</c> in one statement, in stock again, journaled
/// <c>stockReceive</c>, <c>Idempotency-Key</c> optional). Products come from the seed (D-14, <see cref="ProductSeed"/>) and,
/// beyond the contract (D-58, cash desk part 3), from the owner at the desk: <c>POST /admin/products</c> (<c>source =
/// 'desk'</c>, journaled <c>stockCreate</c>) and <c>DELETE /admin/products/{id}</c> (a soft delete with <c>deleted_by =
/// 'desk'</c>, journaled <c>stockArchive</c>; past sales keep their lines), which the seed then neither deletes nor revives.
/// A PATCH that sets <c>stockQty</c> may name the quantity it saw (<c>expectedStockQty</c>): a bar sale since then is
/// <c>409 conflict stockChanged {stockQty}</c> instead of being undone. <c>GET /shop/products</c> is notImplemented in v1, so
/// no agent ETag moves yet. Every quantity change passes <see cref="StockChangedAsync"/> inside its transaction (a bar sale's
/// decrement too; a void's restock only goes up).
/// </summary>
public static class StockEndpoints
{
    public static readonly string[] Operations = ["adminProducts", "adminUpdateProduct", "adminReceiveProduct"];

    /// <summary><c>settings.stock.lowAt</c> when the owner has not set one (the mock's club default).</summary>
    public const int DefaultLowAt = 5;

    /// <summary><c>products.category</c> values (the contract's <c>ProductCategory</c>).</summary>
    private static readonly string[] Categories = ["food", "drink", "snack", "service", "merch", "time"];

    public static void MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/products");
        api.MapGet("", ListAsync).WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapPost("", CreateAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapPatch("/{id}", UpdateAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapDelete("/{id}", ArchiveAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapPost("/{id}/receive", ReceiveAsync).WithMetadata(new AuthRequirement(AuthMode.Staff));
    }

    /// <summary><c>settings.stock.lowAt</c> of the club, <see cref="DefaultLowAt"/> when unset.</summary>
    public static async Task<int> LowAtAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid clubId) =>
        await c.ExecuteScalarAsync<int?>("SELECT (settings -> 'stock' ->> 'lowAt')::int FROM clubs WHERE id = @clubId", new { clubId }, tx) ?? DefaultLowAt;

    /// <summary>
    /// What follows a change of a product's tracked quantity: called once, from <c>QuantityChangedAsync</c> or a bar sale's
    /// decrement (<see cref="ShopSaleEndpoints"/>), inside the change's transaction (after its UPDATE, before the commit), with the product as it is now and the
    /// quantity before. A quantity that crosses <see cref="LowAtAsync"/> downwards (from above it, or from untracked, to at or
    /// below it) raises <c>lowStock</c> for the webhooks — once per crossing, not on every sale below it (the mock repeats it).
    /// </summary>
    public static async Task StockChangedAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, Product product, int? before, DateTimeOffset now)
    {
        if (product.StockQty is not { } qty)
        {
            return;
        }

        var lowAt = await LowAtAsync(c, tx, clubId);
        if (qty <= lowAt && (before is null || before > lowAt))
        {
            await Webhooks.EnqueueAsync(c, tx, clubId, "lowStock", now, $"{product.Title}: осталось {qty}", new { productId = product.Id, title = product.Title, qty });
        }
    }

    private static async Task<IResult> ListAsync(HttpContext context, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        await using var c = await db.OpenConnectionAsync();
        var items = await c.QueryAsync<ProductRow>(
            $"SELECT {ProductRow.Columns} FROM products WHERE club_id = @ClubId AND deleted_at IS NULL ORDER BY created_at, id", new { staff.ClubId });
        return AdminJson.Ok(new AdminProductList(items.Select(p => p.ToWire()).ToList(), await LowAtAsync(c, null, staff.ClubId)));
    }

    /// <summary>
    /// A product the owner adds at the desk (D-58): <c>201 {product}</c>, <c>source = 'desk'</c>, journaled <c>stockCreate</c>;
    /// <c>Idempotency-Key</c> optional. A <c>time</c> product may be created for a later kiosk, but the bar never sells it.
    /// </summary>
    private static async Task<IResult> CreateAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminProductCreateRequest>(body, "title", "category", "price");
        var title = AdminInput.Text(r.Title?.Trim(), "title", 80);
        var category = Categories.Contains(r.Category, StringComparer.Ordinal) ? r.Category! : throw ApiException.Validation("category", "enum");
        var price = AdminInput.Range(r.Price, "price", 0, 1_000_000_000)!.Value;
        var qty = AdminInput.Range(r.StockQty, "stockQty", 0, 1_000_000);
        if (AdminInput.Has(body, "inStock") && r.InStock is null)
        {
            throw ApiException.Validation("inStock", "format");
        }

        return await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var productId = Guid.CreateVersion7(now);
            var product = await c.QuerySingleAsync<ProductRow>(
                $"""
                INSERT INTO products (id, club_id, title, category, price, in_stock, stock_qty, source, created_at, updated_at)
                VALUES (@productId, @ClubId, @title, @category, @price, @inStock, @qty, 'desk', @now, @now)
                RETURNING {ProductRow.Columns}
                """,
                new { productId, staff.ClubId, title, category, price, inStock = r.InStock ?? true, qty, now }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "stockCreate", amount: price, detail: title,
                meta: new { productId, title, category, price, stockQty = qty });
            return new IdempotentResult(StatusCodes.Status201Created, AdminJson.ToElement(new AdminProductResponse(product.ToWire())));
        });
    }

    /// <summary>
    /// The owner archives a product (D-58): a soft delete with <c>deleted_by = 'desk'</c> (the seed does not bring it back),
    /// journaled <c>stockArchive</c>; <c>{ok: true}</c>. Lines of past sales keep their title and price.
    /// </summary>
    private static async Task<IResult> ArchiveAsync(HttpContext context, string id, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var productId = ProductId(id);
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var now = clock.GetUtcNow();
        var product = await c.QuerySingleOrDefaultAsync<ProductRow>(
            $"""
            UPDATE products SET deleted_at = @now, deleted_by = 'desk', updated_at = @now
            WHERE id = @productId AND club_id = @ClubId AND deleted_at IS NULL
            RETURNING {ProductRow.Columns}
            """,
            new { productId, staff.ClubId, now }, tx)
            ?? throw ApiException.NotFound("product");
        await Audit.WriteAsync(c, tx, staff, now, "stockArchive", detail: product.Title, meta: new { productId, title = product.Title, stockQty = product.StockQty });
        await tx.CommitAsync();
        return AdminJson.Ok(AdminJson.OkBody);
    }

    /// <summary>
    /// Present keys are applied (<c>stockQty: null</c> stops tracking), absent ones left. With <c>stockQty</c>, a sent
    /// <c>expectedStockQty</c> (null: untracked) must still be the quantity, else <c>409 conflict stockChanged {stockQty}</c>
    /// (D-54: a bar sale since the panel loaded would be undone).
    /// </summary>
    private static async Task<IResult> UpdateAsync(HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminProductUpdateRequest>(body);
        var title = r.Title is null ? null : AdminInput.Text(r.Title, "title", 80);
        var price = AdminInput.Range(r.Price, "price", 0, 1_000_000_000);
        var qty = AdminInput.Range(r.StockQty, "stockQty", 0, 1_000_000);
        var hasQty = AdminInput.Has(body, "stockQty");
        var expected = AdminInput.Range(r.ExpectedStockQty, "expectedStockQty", 0, int.MaxValue);
        var checkExpected = hasQty && AdminInput.Has(body, "expectedStockQty");
        if (AdminInput.Has(body, "inStock") && r.InStock is null)
        {
            throw ApiException.Validation("inStock", "format");
        }

        var productId = ProductId(id);
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var now = clock.GetUtcNow();
        var before = await LockAsync(c, tx, staff, productId);
        if (checkExpected && before.StockQty != expected)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: stockChanged", new { reason = "stockChanged", stockQty = before.StockQty });
        }

        var after = await c.QuerySingleAsync<ProductRow>(
            $"""
            UPDATE products SET title = coalesce(@title, title), price = coalesce(@price, price), in_stock = coalesce(@InStock, in_stock),
                                stock_qty = CASE WHEN @hasQty THEN @qty ELSE stock_qty END, updated_at = @now
            WHERE id = @productId
            RETURNING {ProductRow.Columns}
            """,
            new { productId, title, price, r.InStock, hasQty, qty, now }, tx);
        if (after.StockQty != before.StockQty)
        {
            await QuantityChangedAsync(c, tx, staff, now, before, after, "stockEdit",
                $"{after.Title}: {before.StockQty?.ToString() ?? "—"} → {after.StockQty?.ToString() ?? "—"}",
                new { productId, before = before.StockQty, after = after.StockQty });
        }

        await tx.CommitAsync();
        return AdminJson.Ok(new AdminProductResponse(after.ToWire()));
    }

    /// <summary>A replay answers with the product right after this delivery, not the live row (as the mock).</summary>
    private static async Task<IResult> ReceiveAsync(HttpContext context, string id, [FromBody] JsonElement body, IdempotencyStore store, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var qty = AdminInput.Range(Api.Read<AdminProductReceiveRequest>(body, "qty").Qty, "qty", 1, 100_000)!.Value;
        var productId = ProductId(id);
        return await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var before = await LockAsync(c, tx, staff, productId);
            if ((long)(before.StockQty ?? 0) + qty > int.MaxValue)
            {
                throw ApiException.Validation("qty", "max");
            }

            var after = await c.QuerySingleAsync<ProductRow>(
                $"""
                UPDATE products SET stock_qty = coalesce(stock_qty, 0) + @qty, in_stock = true, updated_at = @now
                WHERE id = @productId
                RETURNING {ProductRow.Columns}
                """,
                new { productId, qty, now }, tx);
            await QuantityChangedAsync(c, tx, staff, now, before, after, "stockReceive", $"{after.Title} +{qty}", new { productId, qty });
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminProductResponse(after.ToWire())));
        });
    }

    /// <summary>
    /// After every change of a tracked quantity, in its transaction: the journal entry, then <see cref="StockChangedAsync"/>
    /// — its only call site.
    /// </summary>
    private static async Task QuantityChangedAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, DateTimeOffset now, ProductRow before, ProductRow after, string action, string detail,
        object meta)
    {
        await Audit.WriteAsync(c, tx, staff, now, action, detail: detail, meta: meta);
        await StockChangedAsync(c, tx, staff.ClubId, after.ToWire(), before.StockQty, now);
    }

    /// <summary>A live product of the club, locked (DESIGN §4.4: products after wallets), else <c>404 what=product</c>.</summary>
    internal static async Task<ProductRow> LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, Guid productId) =>
        await c.QuerySingleOrDefaultAsync<ProductRow>(
            $"SELECT {ProductRow.Columns} FROM products WHERE id = @productId AND club_id = @ClubId AND deleted_at IS NULL FOR UPDATE",
            new { productId, staff.ClubId }, tx)
        ?? throw ApiException.NotFound("product");

    private static Guid ProductId(string id) => Guid.TryParse(id, out var productId) ? productId : throw ApiException.NotFound("product");

    /// <summary>A <c>products</c> row as the contract's <c>Product</c>.</summary>
    internal sealed class ProductRow
    {
        public const string Columns = "id, title, category, price, image_url, in_stock, stock_qty, tags";

        public Guid Id { get; init; }
        public string Title { get; init; } = "";
        public string Category { get; init; } = "";
        public long Price { get; init; }
        public string ImageUrl { get; init; } = "";
        public bool InStock { get; init; }
        public int? StockQty { get; init; }
        public string[] Tags { get; init; } = [];

        public Product ToWire() =>
            new(Id, Title, Enum.Parse<ProductCategory>(Category, ignoreCase: true), Money.Uzs(Price), ImageUrl, InStock, StockQty, Tags);
    }
}
