using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Auth;

/// <summary>
/// Player tokens (DESIGN §3.4): opaque, 32 random bytes base64url, only the SHA-256 is stored. A token is bound to the
/// pair (user, PC); <c>UNIQUE(pc_id)</c> makes a new sign-in on a PC displace the previous player. Lifetime
/// <see cref="AuthOptions.UserTokenHours"/>; there is no refresh of a player token.
/// </summary>
public sealed class UserTokens(NpgsqlDataSource db, AuthOptions options, TimeProvider clock)
{
    public static byte[] HashOf(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Issues the token of <paramref name="userId"/> on <paramref name="pcId"/> in the caller's transaction.</summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> IssueAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, Guid pcId)
    {
        var token = NewToken();
        var now = clock.GetUtcNow();
        var expires = now.AddHours(options.UserTokenHours);
        await c.ExecuteAsync(
            """
            INSERT INTO user_tokens (token_hash, user_id, pc_id, created_at, expires_at) VALUES (@hash, @userId, @pcId, @now, @expires)
            ON CONFLICT (pc_id) DO UPDATE SET token_hash = excluded.token_hash, user_id = excluded.user_id,
                                              created_at = excluded.created_at, expires_at = excluded.expires_at
            """,
            new { hash = HashOf(token), userId, pcId, now, expires },
            tx);
        return (token, expires.AddTicks(-(expires.Ticks % TimeSpan.TicksPerMillisecond)));
    }

    /// <summary>The player of <paramref name="token"/> on <paramref name="pcId"/>, or the <c>401 userToken</c> problem.</summary>
    public async Task<(UserContext? User, string? Problem)> ValidateAsync(string token, Guid pcId)
    {
        await using var c = await db.OpenConnectionAsync();
        var row = await c.QuerySingleOrDefaultAsync<TokenRow>(
            """
            SELECT t.user_id, t.pc_id, t.expires_at, u.banned OR u.deleted_at IS NOT NULL AS gone
            FROM user_tokens t JOIN users u ON u.id = t.user_id WHERE t.token_hash = @hash
            """,
            new { hash = HashOf(token) });
        if (row is null)
        {
            return (null, "invalid");
        }

        return row.Gone ? (null, "revoked")
            : row.ExpiresAt <= clock.GetUtcNow() ? (null, "expired")
            : row.PcId != pcId ? (null, "boundElsewhere")
            : (new UserContext(row.UserId), null);
    }

    /// <summary>PCs where <paramref name="userId"/> holds a live token (<c>walletUpdated</c> goes to all of them, DESIGN §6.5).</summary>
    public async Task<IReadOnlyList<Guid>> PcsOfAsync(Guid userId)
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<Guid>("SELECT pc_id FROM user_tokens WHERE user_id = @userId AND expires_at > @now", new { userId, now = clock.GetUtcNow() })).ToList();
    }

    private sealed class TokenRow
    {
        public Guid UserId { get; init; }
        public Guid PcId { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public bool Gone { get; init; }
    }
}
