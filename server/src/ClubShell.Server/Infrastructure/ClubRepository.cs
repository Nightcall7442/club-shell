using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Infrastructure;

/// <summary><c>Club:*</c>: bootstrap of the single club of a v1 deployment (DESIGN §2.5, §4.1).</summary>
public sealed class ClubOptions
{
    public string Name { get; set; } = "ClubShell";

    /// <summary>IANA zone for all calendar logic; set on first start only.</summary>
    public string TimeZone { get; set; } = "Asia/Tashkent";

    /// <summary>Agent <c>X-Club-Key</c>. Empty closes registration.</summary>
    public string EnrollmentKey { get; set; } = "";

    /// <summary>The key before the last rotation, still accepted: agents re-register every morning.</summary>
    public string PreviousEnrollmentKey { get; set; } = "";
}

public sealed class ClubRepository(NpgsqlDataSource db, TimeProvider clock)
{
    /// <summary>
    /// First start creates the network and the club; every start writes the enrollment key hashes from config, so a
    /// rotation is a config change plus restart. Serialized by a transaction-scoped advisory lock.
    /// </summary>
    public async Task EnsureAsync(ClubOptions options, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenConnectionAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        await c.ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", new { key = AdvisoryLocks.Bootstrap }, tx);
        var now = clock.GetUtcNow();
        var clubId = await c.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM clubs ORDER BY created_at LIMIT 1", transaction: tx);
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

        await c.ExecuteAsync(
            "UPDATE clubs SET enrollment_key_hash = @current, prev_enrollment_key_hash = @previous WHERE id = @clubId",
            new { clubId, current = Hash(options.EnrollmentKey), previous = Hash(options.PreviousEnrollmentKey) },
            tx);
        await tx.CommitAsync(cancellationToken);
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

    private static bool Matches(byte[] presented, byte[]? stored) =>
        stored is not null && CryptographicOperations.FixedTimeEquals(presented, stored);

    private static byte[]? Hash(string? key) => string.IsNullOrEmpty(key) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
