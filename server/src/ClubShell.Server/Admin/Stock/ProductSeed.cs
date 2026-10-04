using System.Text.Json;
using ClubShell.Contracts.Shop;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The shop's products from seed JSON (DESIGN §12 D-14: the contract has no product create or delete).
/// <c>Catalog:ProductsSeedPath</c> is a JSON array of contract <c>Product</c> objects. Every start inserts the products the
/// club does not have yet (<c>source = 'seed'</c>), soft-deletes the seed's own products missing from the file
/// (<c>deleted_by = 'seed'</c>) and brings back those that return — only those it deleted itself (D-58): a product the owner
/// added (<c>source = 'desk'</c>) or archived at the desk (<c>deleted_by = 'desk'</c>) is left alone. A product the club
/// already has is left as the console edited it (title, price, stock). No file: the products stay as they are.
/// </summary>
public static class ProductSeed
{
    public static async Task<int> ApplyAsync(NpgsqlDataSource db, string path, TimeProvider clock)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var products = JsonSerializer.Deserialize<List<Product>>(File.ReadAllText(path), ServerJson.Options)
            ?? throw new InvalidOperationException($"Products seed {path} is empty");
        if (products.FirstOrDefault(p => p.Category == ProductCategory.Unknown) is { } unknown)
        {
            throw new InvalidOperationException($"Products seed {path}: product {unknown.Id} has an unknown category");
        }

        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var rows = 0;
        foreach (var clubId in await c.QueryAsync<Guid>("SELECT id FROM clubs", transaction: tx))
        {
            foreach (var p in products)
            {
                rows += await InsertAsync(c, tx, clubId, p, now, restore: true);
            }

            rows += await c.ExecuteAsync(
                """
                UPDATE products SET deleted_at = @now, deleted_by = 'seed', updated_at = @now
                WHERE club_id = @clubId AND deleted_at IS NULL AND source = 'seed' AND NOT (id = ANY(@ids))
                """,
                new { clubId, now, ids = products.Select(p => p.Id).ToArray() },
                tx);
        }

        await tx.CommitAsync();
        return rows;
    }

    /// <summary>
    /// Inserts <paramref name="p"/> into <paramref name="clubId"/> unless it exists (<c>source = 'seed'</c>); with
    /// <paramref name="restore"/> one the seed soft-deleted itself comes back (<c>deleted_by</c> 'seed', or NULL from before
    /// M0009), never one archived at the desk. Rows written.
    /// </summary>
    public static Task<int> InsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, Product p, DateTimeOffset now, bool restore = false) =>
        c.ExecuteAsync(
            """
            INSERT INTO products (id, club_id, title, category, price, image_url, in_stock, stock_qty, tags, source, created_at, updated_at)
            VALUES (@Id, @clubId, @Title, @category, @price, @ImageUrl, @InStock, @StockQty, @tags, 'seed', @now, @now)
            ON CONFLICT (id) DO UPDATE SET deleted_at = NULL, deleted_by = NULL, updated_at = excluded.updated_at
            WHERE @restore AND products.club_id = excluded.club_id AND products.deleted_at IS NOT NULL
              AND coalesce(products.deleted_by, 'seed') = 'seed'
            """,
            new
            {
                p.Id, clubId, p.Title, category = p.Category.ToString().ToLowerInvariant(), price = p.Price.Amount, p.ImageUrl, p.InStock,
                p.StockQty, tags = p.Tags.ToArray(), now, restore,
            },
            tx);
}
