using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The club's own pictures (D-93): logos, wallpapers and banners the owner uploads in «Интерфейс и логотипы», kept in the
/// database (the server's disk may be wiped by a redeploy) and served at <c>GET /api/v1/media/{id}</c>.
/// </summary>
[Migration(2026112310, "club media")]
public sealed class M0024_ClubMedia : Migration
{
    public override void Up() => Execute.Sql(
        """
        CREATE TABLE club_media (
            id           uuid PRIMARY KEY,
            club_id      uuid NOT NULL REFERENCES clubs(id),
            kind         text NOT NULL CHECK (kind IN ('logo', 'wallpaper', 'banner', 'screensaver', 'other')),
            content_type text NOT NULL,
            bytes        bytea NOT NULL,
            sha256       text NOT NULL,
            created_at   timestamptz NOT NULL
        );
        CREATE INDEX club_media_club ON club_media (club_id, created_at);
        """);

    public override void Down() => Execute.Sql("DROP TABLE club_media;");
}
