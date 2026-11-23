using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The code a walk-in guest types on the PC (D-96): the desk's guest seat keeps only its PBKDF2 hash; the plain four
/// digits are shown to the cashier once. Null — not a desk guest's seat (a member's, a kiosk session).
/// </summary>
[Migration(2026112311, "guest code")]
public sealed class M0025_GuestCode : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE sessions ADD COLUMN guest_code_hash text NULL;");

    public override void Down() => Execute.Sql("ALTER TABLE sessions DROP COLUMN guest_code_hash;");
}
