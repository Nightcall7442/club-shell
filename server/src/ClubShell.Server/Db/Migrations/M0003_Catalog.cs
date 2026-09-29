using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Catalog (DESIGN §4.2, M0003): the club's games from the seed JSON (<c>data</c> is the contract <c>Game</c> without the
/// local fields; <c>settings_paths</c> stays on the server, S5 edits it) and the agent's launch reports. A report the
/// agent repeats from its offline queue is the same (session, phase, started_at): stored once.
/// </summary>
[Migration(2026093001, "catalog: games, launch reports")]
public sealed class M0003_Catalog : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE games (
                id              uuid PRIMARY KEY,
                club_id         uuid NOT NULL REFERENCES clubs(id),
                title           text NOT NULL,
                settings_paths  text[] NULL,
                data            jsonb NOT NULL,
                updated_at      timestamptz NOT NULL,
                deleted_at      timestamptz NULL,
                UNIQUE (club_id, id)
            );

            CREATE TABLE launch_reports (
                id          uuid PRIMARY KEY,
                club_id     uuid NOT NULL,
                pc_id       uuid NOT NULL,
                game_id     uuid NOT NULL,
                user_id     uuid NULL,
                session_id  uuid NULL,
                phase       text NOT NULL CHECK (phase IN ('launch', 'exit')),
                started_at  timestamptz NOT NULL,
                data        jsonb NOT NULL,
                created_at  timestamptz NOT NULL,
                FOREIGN KEY (club_id, pc_id) REFERENCES pcs (club_id, id) ON DELETE CASCADE,
                FOREIGN KEY (club_id, game_id) REFERENCES games (club_id, id)
            );
            CREATE UNIQUE INDEX launch_reports_dedup ON launch_reports (session_id, phase, started_at);
            -- Game.lastPlayedAt: the newest successful launch of (user, game).
            CREATE INDEX launch_reports_user ON launch_reports (user_id, game_id) WHERE phase = 'launch';
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE launch_reports;
            DROP TABLE games;
            """);
    }
}
