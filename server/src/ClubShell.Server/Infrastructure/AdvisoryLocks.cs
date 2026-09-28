using System.Text;

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

    public static long Key(string tag) => Encoding.ASCII.GetBytes(tag).Aggregate(0L, (key, b) => (key << 8) | b);
}
