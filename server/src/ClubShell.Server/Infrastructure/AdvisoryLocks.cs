using System.Text;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Infrastructure;

/// <summary>
/// PostgreSQL advisory-lock keys (DESIGN §8): the ASCII bytes of a short tag packed into a bigint, so a key is
/// readable in <c>pg_locks</c>. Workers and the hub add theirs (<c>CSSess</c>, <c>CSHub</c>, …) with their slices.
/// </summary>
public static class AdvisoryLocks
{
    /// <summary>Migrations: two server processes never migrate at once.</summary>
    public static readonly long Migrations = Key("CSMig");

    /// <summary>First-start bootstrap of the network and club (transaction-scoped).</summary>
    public static readonly long Bootstrap = Key("CSBoot");

    /// <summary>The single server instance that holds the WS registry (D-20, DESIGN §6.8); session-scoped for the process life.</summary>
    public static readonly long Hub = Key("CSHub");

    /// <summary>The session tick (DESIGN §8): held by <c>SessionTickWorker</c> for its life, a second instance waits.</summary>
    public static readonly long Sessions = Key("CSSess");

    public static long Key(string tag) => Encoding.ASCII.GetBytes(tag).Aggregate(0L, (key, b) => (key << 8) | b);

    /// <summary>
    /// One PC's sign-ins and desk opens, one after another (D-27, §4.4): taken in the caller's transaction, released with it.
    /// A sign-in then sees the session a desk open committed (and refuses with <c>pcOccupied</c>), and a desk open deletes the
    /// token of a sign-in that committed before it — a token is never left on a PC whose session belongs to someone else.
    /// </summary>
    public static Task PcAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid pcId) =>
        c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('pc:' || @pcId::text, 0))", new { pcId }, tx);
}
