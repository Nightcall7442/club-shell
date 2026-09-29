using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Wallet;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Infrastructure;

/// <summary>
/// <c>Seed:Dev</c> (DESIGN §2.5; Development and tests only): what the admin e2e (<c>tests/shell-e2e/admin/console.spec.ts</c>)
/// and the MockServer demo expect of a fresh club — tariffs Standard 12 000 sum/h, VIP 20 000 sum/h and the package
/// "Night Pack (5h)", client groups (staff −50 %, student −15 %, …) and the demo players alisher / dilnoza / bekzod
/// (password <c>demo</c>, cards CARD-0001..0003) with their demo balances posted through the ledger. Ids are the mock's
/// (<c>db.ts sid()</c>), so fixtures written against the mock keep working. Staff as in the mock: owner "Владелец" PIN 0000,
/// cashier "Кассир Азиз" PIN 1111; the mock's hall zones and top-up bonus tiers (5/10/15 % from 50 000/100 000/200 000 sum).
/// Rows and settings keys that exist are left alone.
/// </summary>
public static class DevSeed
{
    private static readonly Lazy<string> DemoPassword = new(() => Passwords.Hash("demo"));

    public static async Task SeedAsync(NpgsqlDataSource db, TimeProvider clock, StaffTokens staff)
    {
        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var (clubId, networkId) = await c.QuerySingleAsync<(Guid, Guid)>("SELECT id, network_id FROM clubs ORDER BY created_at LIMIT 1", transaction: tx);

        var night = JsonSerializer.Serialize(new[] { new { days = new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" }, from = "22:00", to = "08:00" } });
        foreach (var (key, name, price, min, max, zones, windows, package) in new[]
        {
            ("standard", "Standard", 1_200_000L, 30, 720, Array.Empty<string>(), "[]", ((int, long)?)null),
            ("vip", "VIP", 2_000_000L, 30, 720, new[] { "VIP" }, "[]", null),
            ("night", "Night Pack (5h)", 800_000L, 300, 300, Array.Empty<string>(), night, (300, 4_000_000L)),
        })
        {
            await c.ExecuteAsync(
                """
                INSERT INTO tariffs (id, club_id, name, price_per_hour, min_minutes, max_minutes, zones, time_windows, is_package,
                                     package_minutes, package_price, created_at, updated_at)
                VALUES (@id, @clubId, @name, @price, @min, @max, @zones, @windows::jsonb, @isPackage, @packageMinutes, @packagePrice, @now, @now)
                ON CONFLICT DO NOTHING
                """,
                new
                {
                    id = Sid("tariff:" + key), clubId, name, price, min, max, zones, windows, isPackage = package is not null,
                    packageMinutes = package?.Item1, packagePrice = package?.Item2, now,
                },
                tx);
        }

        var groups = JsonSerializer.Serialize(new[]
        {
            new { id = "guest", name = "Гость", discountPct = 0, color = "#F97316" },
            new { id = "regular", name = "Постоянный", discountPct = 10, color = "#22C55E" },
            new { id = "student", name = "Школьник", discountPct = 15, color = "#A855F7" },
            new { id = "staff", name = "Сотрудник", discountPct = 50, color = "#3B82F6" },
        });
        var hall = JsonSerializer.Serialize(new[]
        {
            new { name = "Standard", color = "#22C55E" },
            new { name = "VIP", color = "#F2B84B" },
            new { name = "Bootcamp", color = "#9ADFFF" },
        });
        var tiers = JsonSerializer.Serialize(new[]
        {
            new { minAmount = 5_000_000, bonusPct = 5 },
            new { minAmount = 10_000_000, bonusPct = 10 },
            new { minAmount = 20_000_000, bonusPct = 15 },
        });
        foreach (var (key, value) in new[] { ("groups", groups), ("zones", hall), ("bonusTiers", tiers) })
        {
            await c.ExecuteAsync(
                "UPDATE clubs SET settings = settings || jsonb_build_object(@key, @value::jsonb) WHERE id = @clubId AND settings -> @key IS NULL",
                new { clubId, key, value },
                tx);
        }

        foreach (var (id, name, role, pin) in new[] { ("owner", "Владелец", "owner", "0000"), ("cashier-1", "Кассир Азиз", "cashier", "1111") })
        {
            await c.ExecuteAsync(
                """
                INSERT INTO staff (id, network_id, club_id, name, role, pin_hmac, created_at, updated_at)
                VALUES (@id, @networkId, @clubId, @name, @role, @hmac, @now, @now)
                ON CONFLICT DO NOTHING
                """,
                new { id = Sid("staff:" + id), networkId, clubId, name, role, hmac = staff.PinHmac(pin), now },
                tx);
        }

        foreach (var (key, display, role, locale, card, balance) in new[]
        {
            ("alisher", "Alisher K.", "member", "ru", "CARD-0001", 4_500_000L),
            ("dilnoza", "Dilnoza R.", "vip", "uz", "CARD-0002", 12_000_000L),
            ("bekzod", "Bekzod T.", "member", "en", "CARD-0003", 300_000L),
        })
        {
            var id = Sid("user:" + key);
            var inserted = await c.ExecuteAsync(
                """
                INSERT INTO users (id, network_id, username, display_name, role, locale, password_hash, card_id, created_at)
                VALUES (@id, @networkId, @key, @display, @role, @locale, @hash, @card, @now)
                ON CONFLICT DO NOTHING
                """,
                new { id, networkId, key, display, role, locale, hash = DemoPassword.Value, card, now },
                tx);
            if (inserted == 1)
            {
                await c.ExecuteAsync("INSERT INTO wallets (user_id, network_id, updated_at) VALUES (@id, @networkId, @now)", new { id, networkId, now }, tx);
                await Ledger.PostAsync(c, tx, id, allowOverdraft: false, now, new LedgerLine("adjustment", balance, "Демо-баланс", clubId));
            }
        }

        await tx.CommitAsync();
    }

    /// <summary>The mock's deterministic UUID v4-shaped id of a seed name (<c>tools/MockServer/src/db.ts sid</c>).</summary>
    public static Guid Sid(string name)
    {
        var h = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        var variant = "89ab"[Convert.ToInt32(h[16..17], 16) & 0x3];
        return Guid.Parse($"{h[..8]}-{h[8..12]}-4{h[13..16]}-{variant}{h[17..20]}-{h[20..32]}");
    }
}
