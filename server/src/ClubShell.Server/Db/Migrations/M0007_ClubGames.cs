using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Games the club owns (beyond the contract): the owner adds, edits and deletes games in the console. <c>origin</c> tells
/// them from the seed's: <c>seed</c> rows follow the seed file on every start, <c>club</c> rows — added by the owner, or a
/// seed game the owner changed or deleted — are left alone by it (DESIGN D-14, server/README.md "Каталог игр и товаров").
/// </summary>
[Migration(2026100202, "games: club-owned games")]
public sealed class M0007_ClubGames : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TABLE games ADD COLUMN origin text NOT NULL DEFAULT 'seed' CHECK (origin IN ('seed', 'club'));");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE games DROP COLUMN origin;");
    }
}
