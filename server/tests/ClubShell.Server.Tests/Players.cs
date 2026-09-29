using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Wallet;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>A player created for one test: every test has its own, so open-session rules never cross tests.</summary>
public sealed record TestPlayer(Guid Id, string Username);

/// <summary>Players, money and the seeded tariffs of slice S2 tests (the fixture runs with <c>Seed:Dev</c>).</summary>
public static class Players
{
    public const string Password = "correct horse";

    /// <summary>Seed tariffs (<see cref="DevSeed"/>): Standard 12 000 sum/h 30..720 min, VIP only in zone VIP, the 5 h night package.</summary>
    public static readonly Guid Standard = DevSeed.Sid("tariff:standard");
    public static readonly Guid Vip = DevSeed.Sid("tariff:vip");
    public static readonly Guid NightPack = DevSeed.Sid("tariff:night");

    private static readonly Lazy<string> Hash = new(() => Passwords.Hash(Password));

    /// <summary>A member with <paramref name="balance"/> tiyin posted through the ledger (the invariant holds from the start).</summary>
    public static async Task<TestPlayer> CreateAsync(ServerFixture server, long balance = 10_000_000, string role = "member", string? card = null)
    {
        var player = new TestPlayer(Guid.NewGuid(), "p-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
        await using var c = await OpenAsync(server);
        await using var tx = await c.BeginTransactionAsync();
        var network = await c.QuerySingleAsync<Guid>("SELECT network_id FROM clubs LIMIT 1", transaction: tx);
        var now = server.Clock.GetUtcNow();
        await c.ExecuteAsync(
            """
            INSERT INTO users (id, network_id, username, display_name, role, password_hash, card_id, created_at) VALUES (@Id, @network, @Username, @Username, @role, @hash, @card, @now);
            INSERT INTO wallets (user_id, network_id, updated_at) VALUES (@Id, @network, @now);
            """,
            new { player.Id, player.Username, network, role, hash = Hash.Value, card, now },
            tx);
        if (balance != 0)
        {
            await Ledger.PostAsync(c, tx, player.Id, allowOverdraft: true, now, new LedgerLine("adjustment", balance, "test"));
        }

        await tx.CommitAsync();
        return player;
    }

    /// <summary>The player's profile in the club (group, blacklist, birth year).</summary>
    public static async Task ProfileAsync(ServerFixture server, TestPlayer player, string? groupId = null, bool blacklisted = false, int? birthYear = null)
    {
        await using var c = await OpenAsync(server);
        await c.ExecuteAsync(
            """
            INSERT INTO client_profiles (club_id, user_id, group_id, blacklisted, birth_year) SELECT id, @Id, @groupId, @blacklisted, @birthYear FROM clubs
            ON CONFLICT (club_id, user_id) DO UPDATE SET group_id = excluded.group_id, blacklisted = excluded.blacklisted, birth_year = excluded.birth_year
            """,
            new { player.Id, groupId, blacklisted, birthYear });
    }

    public static async Task<long> BalanceAsync(ServerFixture server, Guid userId)
    {
        await using var c = await OpenAsync(server);
        return await c.ExecuteScalarAsync<long>("SELECT main_balance FROM wallets WHERE user_id = @userId", new { userId });
    }

    public static async Task ExecuteAsync(ServerFixture server, string sql, object? args = null)
    {
        await using var c = await OpenAsync(server);
        await c.ExecuteAsync(sql, args);
    }

    public static async Task<T> ScalarAsync<T>(ServerFixture server, string sql, object? args = null)
    {
        await using var c = await OpenAsync(server);
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }

    /// <summary>A PC of the fixture with <paramref name="player"/> signed in.</summary>
    public static async Task<(TestAgent Agent, TestPlayer Player)> SignedInAsync(ServerFixture server, long balance = 10_000_000)
    {
        var agent = await TestAgent.CreateAsync(server);
        var player = await CreateAsync(server, balance);
        await agent.LoginAsync(player);
        return (agent, player);
    }

    /// <summary>
    /// <c>POST /sessions</c> with a fresh key; the body as the server answered. Postpaid goes without <c>minutes</c>, as the
    /// kiosk sends it (<c>LockScreen</c>, <c>Tariffs</c>).
    /// </summary>
    public static async Task<(int Status, JsonElement Body)> StartAsync(
        TestAgent agent, TestPlayer player, Guid? tariff = null, int? minutes = 60, bool prepaid = true, Guid? key = null)
    {
        using var response = await agent.PostAsync("/api/v1/sessions",
            new { pcId = agent.PcId, userId = player.Id, tariffId = tariff ?? Standard, minutes = prepaid ? minutes : null, prepaid }, key ?? Guid.NewGuid());
        return ((int)response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == (int)response.StatusCode, $"expected {status}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonElement.Parse(text);
    }

    private static Task<NpgsqlConnection> OpenAsync(ServerFixture server) =>
        server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync().AsTask();
}

/// <summary>
/// The ledger invariant (DESIGN §4.3, §10.a): every wallet's cached balance equals the sum of its ledger rows. S2 test classes derive from <see cref="LedgerCheckedTest"/>, so it
/// is asserted after every test.
/// </summary>
public static class LedgerInvariant
{
    public static async Task AssertAsync(ServerFixture server)
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var broken = (await c.QueryAsync<string>(
            """
            SELECT w.user_id::text || ': ' || w.main_balance || ' vs ' || coalesce(l.total, 0)
            FROM wallets w LEFT JOIN (SELECT user_id, sum(amount) AS total FROM ledger_entries GROUP BY user_id) l ON l.user_id = w.user_id
            WHERE w.main_balance <> coalesce(l.total, 0)
            """)).ToList();
        Assert.True(broken.Count == 0, "SUM(ledger) <> wallets: " + string.Join("; ", broken));
    }
}

/// <summary>Base of S2 test classes: the ledger invariant is checked after each test (xunit disposes the class per test).</summary>
public abstract class LedgerCheckedTest(ServerFixture server) : IAsyncLifetime
{
    protected ServerFixture Server => server;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => LedgerInvariant.AssertAsync(server);
}

/// <summary>An S2 fixture whose agent tokens outlive the clock jumps of tick and offline tests.</summary>
public sealed class LongClockServerFixture : ServerFixture
{
    public LongClockServerFixture() => Settings["Auth:AgentTokenMinutes"] = "10080";
}
