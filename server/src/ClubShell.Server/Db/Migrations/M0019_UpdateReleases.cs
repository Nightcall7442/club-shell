using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// ClubShell updates from this server (UPDATES.md §8): the latest release published to each channel
/// (<c>POST /api/v1/updates/{channel}/manifest</c>, the publisher's <c>manifest.json</c> as it came, its packages stored on
/// disk under <c>Updates:StoragePath</c>), and the owner's choice whether the club's PCs take it by themselves at night
/// (<c>clubs.auto_update</c>, off until the owner turns it on; the admin console's «Обновить ClubShell» works either way).
/// </summary>
[Migration(2026111602, "ClubShell releases per channel and the club's automatic updates")]
public sealed class M0019_UpdateReleases : Migration
{
    public override void Up() => Execute.Sql(
        """
        CREATE TABLE update_releases (
            channel       text PRIMARY KEY CHECK (channel IN ('stable', 'beta')),
            version       text NOT NULL,
            manifest      jsonb NOT NULL,
            published_at  timestamptz NOT NULL
        );

        ALTER TABLE clubs ADD COLUMN auto_update boolean NOT NULL DEFAULT false;
        """);

    public override void Down() => Execute.Sql(
        """
        ALTER TABLE clubs DROP COLUMN auto_update;
        DROP TABLE update_releases;
        """);
}
