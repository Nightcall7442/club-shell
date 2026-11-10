using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The debt limit of a postpaid session, taken when it opens (SERVER_API.md §4.5): how far below zero the player's
/// balance may run before the tick ends it, in tiyin; <c>NULL</c> — no cap (and every prepaid session). A kiosk session
/// (the player's own pay-as-you-go) gets 0, a desk session the club's <c>limits.memberDebtLimit</c>/<c>guestDebtLimit</c>
/// or no cap. The tick, a resume and a move read the session's own limit instead of working it out again from the role
/// and the club.
/// <para>
/// Open postpaid sessions get the limit the server applied to them until now: a guest the club's
/// <c>limits.guestDebtLimit</c> (none — no cap), anyone else <c>limits.memberDebtLimit</c>, without one 0 (the default of
/// the retired <c>Sessions:PostpaidCreditLimit</c>; a server that ran with another value keeps 0 for those few sessions).
/// </para>
/// </summary>
[Migration(2026111001, "sessions: the debt limit of a postpaid session")]
public sealed class M0017_SessionDebtLimit : Migration
{
    public override void Up() => Execute.Sql(
        """
        ALTER TABLE sessions ADD COLUMN debt_limit bigint NULL CHECK (debt_limit >= 0);

        UPDATE sessions s
        SET debt_limit = CASE
            WHEN u.role = 'guest' THEN nullif(greatest(coalesce((cl.settings -> 'limits' ->> 'guestDebtLimit')::numeric, 0), 0), 0)::bigint
            ELSE greatest(coalesce((cl.settings -> 'limits' ->> 'memberDebtLimit')::numeric, 0), 0)::bigint
        END
        FROM users u, clubs cl
        WHERE u.id = s.user_id AND cl.id = s.club_id AND s.state <> 'ended' AND NOT s.is_prepaid;
        """);

    public override void Down() => Execute.Sql("ALTER TABLE sessions DROP COLUMN debt_limit;");
}
