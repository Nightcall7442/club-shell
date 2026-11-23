using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The club server's own copy of every game's trailer (D-97): what it came from (<c>origin</c>: the owner's or the seed's
/// link, one the finder found in the Steam store, or an upload), where (<c>source_url</c>; null for an upload, and for a
/// game still being searched for), how far it got (<c>pending</c> → <c>ready</c>, or <c>failed</c> with a <c>reason</c>),
/// and the stored file: <c>file_id</c> names it on disk (<c>Trailers:StoragePath</c>) and in its URL
/// (<c>/api/v1/trailers/{file_id}</c>), a new id for every new copy, so the PCs may cache it for good. A video is too big
/// for <c>bytea</c> (club media, M0024): it lives on the data volume; one lost with the disk is downloaded again.
/// </summary>
[Migration(2026112312, "game trailers")]
public sealed class M0026_GameTrailers : Migration
{
    public override void Up() => Execute.Sql(
        """
        CREATE TABLE game_trailers (
            game_id      uuid PRIMARY KEY REFERENCES games(id),
            club_id      uuid NOT NULL REFERENCES clubs(id),
            origin       text NOT NULL CHECK (origin IN ('link', 'found', 'upload')),
            source_url   text NULL,
            status       text NOT NULL CHECK (status IN ('pending', 'ready', 'failed')),
            reason       text NULL,
            file_id      uuid NULL,
            content_type text NULL,
            size         bigint NULL,
            sha256       text NULL,
            attempts     int NOT NULL DEFAULT 0,
            next_try_at  timestamptz NOT NULL,
            updated_at   timestamptz NOT NULL
        );
        CREATE INDEX game_trailers_due ON game_trailers (next_try_at) WHERE status = 'pending';
        CREATE UNIQUE INDEX game_trailers_file ON game_trailers (file_id) WHERE file_id IS NOT NULL;
        """);

    public override void Down() => Execute.Sql("DROP TABLE game_trailers;");
}
