using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// A card's link that did not give a trailer (D-97): a YouTube page, a playlist, a dead or too big file, no answer. The
/// finder then looks in the Steam store as for a game without one; the row keeps the link it replaces and why
/// (<c>failed_link</c>, <c>failed_reason</c>), so the card says so and the seed does not bring the link back.
/// </summary>
[Migration(2026112313, "trailer failed link")]
public sealed class M0027_TrailerFailedLink : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE game_trailers ADD COLUMN failed_link text NULL, ADD COLUMN failed_reason text NULL;");

    public override void Down() => Execute.Sql("ALTER TABLE game_trailers DROP COLUMN failed_link, DROP COLUMN failed_reason;");
}
