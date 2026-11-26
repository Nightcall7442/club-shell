using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The player's gender (D-104): <c>male</c> or <c>female</c>, required on every new account (the desk's registration and
/// the player's own on the PC); accounts made before stay valid with NULL («не указан») until the desk sets it.
/// </summary>
[Migration(2026112602, "user gender")]
public sealed class M0029_UserGender : Migration
{
    public override void Up() =>
        Execute.Sql("ALTER TABLE users ADD COLUMN gender text NULL CONSTRAINT users_gender CHECK (gender IN ('male', 'female'));");

    public override void Down() => Execute.Sql("ALTER TABLE users DROP COLUMN gender;");
}
