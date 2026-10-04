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
    /// One PC's sign-ins, desk opens and desk ends, one after another (D-27, §4.4): taken in the caller's transaction,
    /// released with it, before any row lock. A sign-in then sees the session a desk open committed (and refuses with
    /// <c>pcOccupied</c>) or a desk end ended (and issues no token for it), and a desk open or end deletes the token of a
    /// sign-in that committed before it — a token is never left on a PC whose session belongs to someone else or is over.
    /// </summary>
    public static Task PcAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid pcId) =>
        c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('pc:' || @pcId::text, 0))", new { pcId }, tx);

    /// <summary>
    /// <see cref="PcAsync"/> without waiting, for the session tick, which already holds its sessions' rows: false when a
    /// sign-in, desk open or desk end of the PC is in progress (waiting there could deadlock with a desk end, which takes
    /// this lock before the session row), and the tick leaves that session to its next run.
    /// </summary>
    public static Task<bool> TryPcAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid pcId) =>
        c.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_xact_lock(hashtextextended('pc:' || @pcId::text, 0))", new { pcId }, tx);
}
