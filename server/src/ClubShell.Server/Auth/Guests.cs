using System.Security.Cryptography;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Auth;

/// <summary>
/// Temporary guest accounts (<c>users.role = 'guest'</c>, <c>transient</c>): the kiosk's «Гость» (<c>guestLogin</c>) and the
/// desk's walk-in guest (<c>POST /admin/sessions/guest</c>, D-24) create them the same way — username
/// <c>guest-&lt;PC number&gt;-&lt;8 hex&gt;</c>, a zero wallet, no password. A desk guest has no login code: the guest presses
/// «Гость» on that PC and is signed in to the session the desk opened there (D-25).
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

    /// <summary>
    /// The guest whose session the desk opened on <paramref name="pcId"/> and that is still open: <c>origin = 'cashier'</c>,
    /// a staff member opened it and the account is transient (D-25) — or a transient guest's session (a kiosk guest's too)
    /// that the desk moved onto this PC (D-61: a <c>staff</c>/<c>moved</c> event), so «Гость» signs in there. A kiosk guest's
    /// session where it was started, or a member's, is not one.
    /// </summary>
    public static Task<Guid?> DeskGuestOfPcAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid pcId) =>
        c.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT s.user_id FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.pc_id = @pcId AND s.state <> 'ended' AND u.transient
              AND ((s.origin = 'cashier' AND s.created_by_staff_id IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM session_events e WHERE e.session_id = s.id AND e.source = 'staff' AND e.type = 'moved'))
            """,
            new { pcId },
            tx);
}
