using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Counter (DESIGN §4.2, M0004): cash shifts, at most one open per club, and the staff audit journal (append-only; the
/// API key <c>ck_</c> writes with <c>staff_id</c> null and the name "API key"). Every ledger row carries the open
/// shift's id (<c>ledger_entries.shift_id</c>, M0002), which the X/Z reports sum.
/// </summary>
[Migration(2026093002, "counter: shifts, audit entries")]
public sealed class M0004_Counter : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE shifts (
                id             uuid PRIMARY KEY,
                club_id        uuid NOT NULL REFERENCES clubs(id),
                staff_id       uuid NULL REFERENCES staff(id),
                staff_name     text NOT NULL,
                opened_at      timestamptz NOT NULL,
                closed_at      timestamptz NULL,
                opening_cash   bigint NOT NULL CHECK (opening_cash >= 0),
                closing_cash   bigint NULL CHECK (closing_cash >= 0),
                expected_cash  bigint NULL,
                totals         jsonb NULL
            );
            CREATE UNIQUE INDEX shifts_open ON shifts (club_id) WHERE closed_at IS NULL;
            CREATE INDEX shifts_closed ON shifts (club_id, closed_at DESC) WHERE closed_at IS NOT NULL;

            CREATE TABLE audit_entries (
                id          uuid PRIMARY KEY,
                club_id     uuid NOT NULL REFERENCES clubs(id),
                at          timestamptz NOT NULL,
                staff_id    uuid NULL REFERENCES staff(id),
                staff_name  text NOT NULL,
                shift_id    uuid NULL REFERENCES shifts(id),
                action      text NOT NULL,
                user_id     uuid NULL,
                pc_id       uuid NULL,
                amount      bigint NOT NULL DEFAULT 0,
                detail      text NOT NULL DEFAULT '',
                meta        jsonb NOT NULL DEFAULT jsonb_build_object()
            );
            CREATE INDEX audit_entries_club ON audit_entries (club_id, at DESC);
            CREATE INDEX audit_entries_staff ON audit_entries (club_id, staff_id, at DESC);
            CREATE TRIGGER audit_entries_append_only BEFORE UPDATE OR DELETE ON audit_entries
                FOR EACH ROW EXECUTE FUNCTION forbid_change();
            CREATE TRIGGER audit_entries_no_truncate BEFORE TRUNCATE ON audit_entries
                FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE audit_entries;
            DROP TABLE shifts;
            """);
    }
}
