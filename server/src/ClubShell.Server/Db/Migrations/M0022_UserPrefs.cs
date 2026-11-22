using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The player's Shell preferences on their account (D-86): <c>users.prefs</c> — theme, locale, volume, sounds, the
/// metrics overlay and the starred games and apps (<c>UserPrefs</c>), so they follow the player to any PC of the club.
/// <c>{}</c> — nothing chosen yet: the PC's own settings apply.
/// </summary>
[Migration(2026112201, "users: prefs")]
public sealed class M0022_UserPrefs : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE users ADD COLUMN prefs jsonb NOT NULL DEFAULT '{}';");

    public override void Down() => Execute.Sql("ALTER TABLE users DROP COLUMN prefs;");
}
