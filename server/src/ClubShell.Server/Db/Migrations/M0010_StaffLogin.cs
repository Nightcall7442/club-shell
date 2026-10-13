using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Staff PIN lockout in the database (D-76, DESIGN §3.6): the wrong PINs of <c>POST /admin/login</c> per club and source, so
/// the limits survive a restart and add up across sources of one club, and the browsers that signed in to a club, which
/// outsiders' wrong PINs do not lock out. Both are working state that maintenance purges: Down drops them.
/// </summary>
[Migration(2026101201, "staff login: wrong PINs per club and source, trusted browsers")]
public sealed class M0010_StaffLogin : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- club_id: Guid.Empty for an unknown club code or a disabled club, so no foreign key. source: 'net:<IPv4 or IPv6 /64>'
            -- or 'dev:<base64url SHA-256 of a trusted browser's device id>'. alerted: the failure that tripped a club-wide limit
            -- and sent the alert, so a limit kept full by an attack alerts once per window.
            CREATE TABLE staff_login_failures (
                id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                club_id  uuid NOT NULL,
                source   text NOT NULL,
                trusted  boolean NOT NULL,
                alerted  boolean NOT NULL DEFAULT false,
                at       timestamptz NOT NULL
            );
            CREATE INDEX staff_login_failures_source ON staff_login_failures (club_id, source, at);
            CREATE INDEX staff_login_failures_club ON staff_login_failures (club_id, trusted, at);

            CREATE TABLE staff_devices (
                club_id         uuid NOT NULL REFERENCES clubs(id),
                device_hash     bytea NOT NULL,
                last_staff_id   uuid NULL REFERENCES staff(id) ON DELETE SET NULL,
                first_login_at  timestamptz NOT NULL,
                last_login_at   timestamptz NOT NULL,
                PRIMARY KEY (club_id, device_hash)
            );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE staff_devices;
            DROP TABLE staff_login_failures;
            """);
    }
}
