using System.Security.Cryptography;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Auth;

/// <summary>
/// Temporary guest accounts (<c>users.role = 'guest'</c>, <c>transient</c>): the kiosk's «Гость» (<c>guestLogin</c>) and the
/// desk's walk-in guest (<c>POST /admin/sessions/guest</c>, D-24) create them the same way — username
/// <c>guest-&lt;PC number&gt;-&lt;8 hex&gt;</c>, a zero wallet, no password. A desk guest's seat carries a four-digit code
/// (D-96): the desk names it to the guest, who presses «Гость» on that PC and types it; no PC signs anyone in to a seat
/// on the server's word alone.
/// </summary>
internal static class Guests
{
    /// <summary>Inserts the account and its wallet in the caller's transaction; the new user id.</summary>
    public static async Task<Guid> CreateAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid networkId, int pcNumber, string displayName, string locale, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7(now);
        await c.ExecuteAsync(
            """
            INSERT INTO users (id, network_id, username, display_name, role, locale, transient, created_at, last_seen_at)
            VALUES (@id, @networkId, @username, @displayName, 'guest', @locale, true, @now, @now);
            INSERT INTO wallets (user_id, network_id, updated_at) VALUES (@id, @networkId, @now);
            """,
            new { id, networkId, username = $"guest-{pcNumber}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}", displayName, locale, now },
            tx);
        return id;
    }

    /// <summary>A fresh four-digit code for a desk guest's seat (D-96), from the system's cryptographic generator.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The guest whose seat the desk opened on <paramref name="pcId"/> (or moved there, D-61) and that is still open, with
    /// the hash of its code (D-96): a transient account's session that carries a <c>guest_code_hash</c>. A kiosk guest's
    /// session where it was started, or a member's, is not one.
    /// </summary>
    public static Task<(Guid UserId, string CodeHash)?> DeskGuestOfPcAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid pcId) =>
        c.QuerySingleOrDefaultAsync<(Guid UserId, string CodeHash)?>(
            """
            SELECT s.user_id, s.guest_code_hash FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.pc_id = @pcId AND s.state <> 'ended' AND u.transient AND s.guest_code_hash IS NOT NULL
            """,
            new { pcId },
            tx);

    /// <summary>
    /// A guest seat the desk opened on <paramref name="pcId"/> (or moved there), still open and without a code: the PC signs
    /// the guest in at once (D-98, the owner's choice — the desk no longer issues codes). A seat with a code from before
    /// (<see cref="DeskGuestOfPcAsync"/>) still asks for it; a kiosk guest's session where it was started, or a member's, is
    /// not one.
    /// </summary>
    public static Task<Guid?> CodelessDeskGuestOfPcAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid pcId) =>
        c.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT s.user_id FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.pc_id = @pcId AND s.state <> 'ended' AND u.transient AND s.guest_code_hash IS NULL
              AND ((s.origin = 'cashier' AND s.created_by_staff_id IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM session_events e WHERE e.session_id = s.id AND e.source = 'staff' AND e.type = 'moved'))
            """,
            new { pcId },
            tx);
}
