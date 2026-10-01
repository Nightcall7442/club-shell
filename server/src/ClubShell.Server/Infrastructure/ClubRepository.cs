using System.Security.Cryptography;
using System.Text;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Infrastructure;

/// <summary><c>Club:*</c>: bootstrap of the single club of a v1 deployment (DESIGN §2.5, §4.1).</summary>
public sealed class ClubOptions
{
    public string Name { get; set; } = "ClubShell";

    /// <summary>IANA zone for all calendar logic; set on first start only.</summary>
    public string TimeZone { get; set; } = "Asia/Tashkent";

    /// <summary>
    /// Agent <c>X-Club-Key</c> of the first (bootstrap) club. Empty leaves the stored key as it is: a fresh server then has
    /// none (registration closed), and a key issued by the platform administration is not wiped by a restart.
    /// </summary>
    public string EnrollmentKey { get; set; } = "";

    /// <summary>The key before the last rotation, still accepted: agents re-register every morning.</summary>
    public string PreviousEnrollmentKey { get; set; } = "";

    /// <summary>New PCs are approved at registration (dev and tests only, D-7); otherwise they wait for the owner.</summary>
    public bool AutoApprovePcs { get; set; }

    /// <summary>A password login returns <c>offlineHash</c>, so the PC can sign the player in while the server is away.</summary>
    public bool OfflineLogin { get; set; } = true;

    /// <summary><c>POST /auth/guest</c> is allowed; otherwise <c>403 guestDisabled</c>.</summary>
    public bool GuestLogin { get; set; } = true;
}

/// <summary>What the agent surface reads of a club: <c>shell.club</c> and the three versions (DESIGN §5.9).</summary>
public sealed class ClubAgentView
{
    public string Name { get; init; } = "";
    public int ConfigVersion { get; init; }
    public int CatalogVersion { get; init; }
    public int PolicyVersion { get; init; }
    public string TimeZone { get; init; } = "Asia/Tashkent";
    public string? Settings { get; init; }

    /// <summary>Branding, the banners live on the club's local date and the rules (<see cref="Agents.AgentConfig.ShellClubOf"/>).</summary>
    public ShellClub Club { get; set; } = new();
}

public sealed class ClubRepository(NpgsqlDataSource db, TimeProvider clock)
{
    /// <summary>
    /// First start creates the network and the club; every start with a configured key writes the enrollment key hashes,
    /// so a rotation is a config change plus restart (more clubs and their keys: <see cref="CreateAsync"/>). Every later start also bumps <c>config_version</c>: the agent config
    /// carries <c>Agents:*</c>/<c>Sessions:*</c> from appsettings, which change only with a restart, and running agents
    /// refetch it only when the heartbeat's <c>configVersion</c> changes (the ETag <c>"c&lt;v&gt;"</c> follows too).
    /// ponytail: bumped even when those values did not change, one <c>GET /config</c> per PC per restart; store a
    /// fingerprint of them if that ever costs. Serialized by a transaction-scoped advisory lock.
    /// </summary>
    public async Task EnsureAsync(ClubOptions options, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenConnectionAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        await c.ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", new { key = AdvisoryLocks.Bootstrap }, tx);
        var now = clock.GetUtcNow();
        var clubId = await c.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM clubs ORDER BY created_at, id LIMIT 1", transaction: tx);
        if (clubId is null)
        {
            var networkId = Guid.CreateVersion7(now);
            clubId = Guid.CreateVersion7(now);
            await c.ExecuteAsync(
                """
                INSERT INTO networks (id, name, created_at) VALUES (@networkId, @name, @now);
                INSERT INTO clubs (id, network_id, name, time_zone, created_at, updated_at)
                VALUES (@clubId, @networkId, @name, @timeZone, @now, @now);
                """,
                new { networkId, clubId, name = options.Name, timeZone = options.TimeZone, now },
                tx);
        }
        else
        {
            await c.ExecuteAsync("UPDATE clubs SET config_version = config_version + 1, updated_at = @now WHERE id = @clubId", new { clubId, now }, tx);
        }

        // Every club has a code (M0006): the console sends it with the PIN once the server holds more than one club.
        await c.ExecuteAsync("UPDATE clubs SET code = @code WHERE id = @clubId AND code IS NULL", new { clubId, code = NewCode() }, tx);

        if (!string.IsNullOrEmpty(options.EnrollmentKey))
        {
            await c.ExecuteAsync(
                "UPDATE clubs SET enrollment_key_hash = @current, prev_enrollment_key_hash = @previous WHERE id = @clubId",
                new { clubId, current = Hash(options.EnrollmentKey), previous = Hash(options.PreviousEnrollmentKey) },
                tx);
        }

        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>The club the configured key belongs to (<see cref="EnsureAsync"/>): its key is managed by config, not by the platform.</summary>
    public async Task<Guid?> BootstrapClubIdAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM clubs ORDER BY created_at, id LIMIT 1");
    }

    /// <summary>
    /// A new club in a network of its own (platform administration): wallets and players are per network, so clubs of
    /// different owners never share them. Returns the club id; the caller adds the owner in the same transaction.
    /// </summary>
    public static async Task<(Guid ClubId, Guid NetworkId)> CreateAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, string name, string code, string timeZone, string enrollmentKey, DateTimeOffset now)
    {
        var networkId = Guid.CreateVersion7(now);
        var clubId = Guid.CreateVersion7(now);
        await c.ExecuteAsync(
            """
            INSERT INTO networks (id, name, created_at) VALUES (@networkId, @name, @now);
            INSERT INTO clubs (id, network_id, name, code, time_zone, enrollment_key_hash, created_at, updated_at)
            VALUES (@clubId, @networkId, @name, @code, @timeZone, @key, @now, @now);
            """,
            new { networkId, clubId, name, code, timeZone, key = Hash(enrollmentKey), now },
            tx);
        return (clubId, networkId);
    }

    /// <summary>Replaces the club's key; the old one stays accepted as the previous key until the next rotation.</summary>
    public static Task<int> RotateEnrollmentKeyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, string enrollmentKey, DateTimeOffset now) =>
        c.ExecuteAsync(
            """
            UPDATE clubs SET prev_enrollment_key_hash = enrollment_key_hash, enrollment_key_hash = @key, updated_at = @now
            WHERE id = @clubId
            """,
            new { clubId, key = Hash(enrollmentKey), now },
            tx);

    public async Task<ClubAgentView> GetAgentViewAsync(Guid clubId)
    {
        await using var c = await db.OpenConnectionAsync();
        var view = await c.QuerySingleAsync<ClubAgentView>(
            "SELECT name, config_version, catalog_version, policy_version, time_zone, settings::text AS settings FROM clubs WHERE id = @clubId", new { clubId });
        view.Club = Agents.AgentConfig.ShellClubOf(view.Name, view.Settings, DateOnly.FromDateTime(ClubTime.Local(clock.GetUtcNow(), view.TimeZone)));
        return view;
    }

    /// <summary>The stored policy document (<c>version</c> = <c>policy_version</c>), or null before the first seed.</summary>
    public async Task<(int Version, string? Json)> GetPolicyAsync(Guid clubId)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleAsync<(int, string?)>("SELECT policy_version, policy::text FROM clubs WHERE id = @clubId", new { clubId });
    }

    /// <summary>
    /// Seed of the policy (DESIGN §12 D-14): every club whose stored policy differs from <paramref name="seed"/> (ignoring
    /// <c>version</c>/<c>updatedAt</c>) gets it with <c>policy_version + 1</c>. The document is stored as the contract
    /// <see cref="Policy"/> serializes it, so what the agent reads back always parses. Returns the clubs that changed.
    /// ponytail: agents learn a reseed from the heartbeat; the <c>reloadPolicy</c> push to connected PCs comes with a
    /// runtime reload (none in v1: the seed is read at start, when no PC is connected).
    /// </summary>
    public async Task<int> SeedPolicyAsync(Policy seed, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenConnectionAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var changed = 0;
        foreach (var (id, version, json) in await c.QueryAsync<(Guid, int, string?)>("SELECT id, policy_version, policy::text FROM clubs FOR UPDATE", transaction: tx))
        {
            if (json is not null && Normalized(JsonDefaults.Deserialize<Policy>(json)!) == Normalized(seed))
            {
                continue;
            }

            var next = version + 1;
            await c.ExecuteAsync(
                "UPDATE clubs SET policy = @policy::jsonb, policy_version = @next, updated_at = @now WHERE id = @id",
                new { id, next, now, policy = JsonDefaults.Serialize(seed with { Version = next, UpdatedAt = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond)) }) },
                tx);
            changed++;
        }

        await tx.CommitAsync(cancellationToken);
        return changed;

        static string Normalized(Policy policy) => JsonDefaults.Serialize(policy with { Version = 0, UpdatedAt = default });
    }

    /// <summary>The club whose current or previous enrollment key is <paramref name="key"/> (constant-time compare).</summary>
    public async Task<(Guid Id, bool Disabled)?> FindByEnrollmentKeyAsync(string? key)
    {
        var presented = Hash(key);
        if (presented is null)
        {
            return null;
        }

        await using var c = await db.OpenConnectionAsync();
        var clubs = await c.QueryAsync<(Guid Id, bool Disabled, byte[]? Current, byte[]? Previous)>(
            "SELECT id, disabled, enrollment_key_hash, prev_enrollment_key_hash FROM clubs");
        (Guid, bool)? found = null;
        foreach (var club in clubs)
        {
            // Both keys are always compared: no early exit that would tell which one matched.
            var match = Matches(presented, club.Current) | Matches(presented, club.Previous);
            found ??= match ? (club.Id, club.Disabled) : null;
        }

        return found;
    }

    /// <summary>A club code: six characters without the look-alikes 0/O and 1/I, typed once on the console's login screen.</summary>
    public static string NewCode() => RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6);

    /// <summary>A club's enrollment key for the agents' installer: 24 letters and digits, safe on any command line.</summary>
    public static string NewEnrollmentKey() =>
        RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 24);

    private static bool Matches(byte[] presented, byte[]? stored) =>
        stored is not null && CryptographicOperations.FixedTimeEquals(presented, stored);

    private static byte[]? Hash(string? key) => string.IsNullOrEmpty(key) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
