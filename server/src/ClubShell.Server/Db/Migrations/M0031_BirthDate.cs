using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The player's date of birth (D-107): required on every new account (the desk's registration and the player's own on the
/// PC), the input of the minors' night curfew (D-108) and of the birthday gift. Accounts made before stay valid with NULL
/// («не указана») until the desk sets it or the player gives it once on the PC; the year-only <c>client_profiles.birth_year</c>
/// (M0002) is kept and follows the date. One per person, so on <c>users</c> (network level), not per club.
/// </summary>
[Migration(2026112801, "user birth date")]
public sealed class M0031_BirthDate : Migration
{
    public override void Up() =>
        Execute.Sql("ALTER TABLE users ADD COLUMN birth_date date NULL CONSTRAINT users_birth_date CHECK (birth_date >= DATE '1900-01-01');");

    public override void Down() => Execute.Sql("ALTER TABLE users DROP COLUMN birth_date;");
}
