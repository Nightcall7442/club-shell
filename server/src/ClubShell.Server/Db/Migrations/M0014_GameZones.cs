using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The zones a game is offered in (SHELL_CHANGES 43): <c>getGames</c> leaves a game out on a PC whose zone is not among
/// them. Null (every existing game) = every zone. The seed never writes it, so a seed game keeps the owner's zones.
/// </summary>
[Migration(2026102601, "games: zones the game is offered in")]
public sealed class M0014_GameZones : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE games ADD COLUMN zones text[];");

    public override void Down() => Execute.Sql("ALTER TABLE games DROP COLUMN zones;");
}
