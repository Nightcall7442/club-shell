using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Cash desk, part 2 (DESIGN §4.2, «Касса, часть 2»): cash put into or taken out of the drawer (append-only, not the wallet
/// ledger — <c>ledger_entries.user_id</c> is required), who closed a shift (a reprinted Z names the closer), and the journal
/// by shift for the operations feed. A guest's cash payout is an <c>adjustment</c> ledger row with <c>method = 'cash'</c>,
/// which M0002 already allows. <c>cash_movements</c> has no composite (club_id, shift_id) key: <c>shifts</c> has no
/// <c>UNIQUE(club_id, id)</c>, and the endpoint takes the shift from the staff member's club.
/// </summary>
[Migration(2026100401, "cash desk part 2: cash movements, who closed a shift, journal by shift")]
public sealed class M0008_CashDesk2 : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE cash_movements (
                id           uuid PRIMARY KEY,
                club_id      uuid NOT NULL REFERENCES clubs(id),
                shift_id     uuid NOT NULL REFERENCES shifts(id),
                staff_id     uuid NOT NULL REFERENCES staff(id),
                staff_name   text NOT NULL,
                kind         text NOT NULL CHECK (kind IN ('in', 'out')),
                amount       bigint NOT NULL CHECK (amount > 0),
                reason_code  text NOT NULL CHECK (reason_code IN ('change', 'collection', 'expenses', 'other')),
                note         text NULL CHECK (note IS NULL OR length(note) BETWEEN 1 AND 200),
                created_at   timestamptz NOT NULL,
                CHECK (reason_code <> 'other' OR length(coalesce(note, '')) >= 3)
            );
            CREATE INDEX cash_movements_shift ON cash_movements (shift_id, created_at);
            CREATE TRIGGER cash_movements_append_only BEFORE UPDATE OR DELETE ON cash_movements
                FOR EACH ROW EXECUTE FUNCTION forbid_change();
            CREATE TRIGGER cash_movements_no_truncate BEFORE TRUNCATE ON cash_movements
                FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();

            ALTER TABLE shifts ADD COLUMN closed_by_staff_id uuid NULL REFERENCES staff(id), ADD COLUMN closed_by_name text NULL;

            CREATE INDEX audit_entries_shift ON audit_entries (shift_id, at DESC, id DESC) WHERE shift_id IS NOT NULL;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP INDEX audit_entries_shift;
            ALTER TABLE shifts DROP COLUMN closed_by_name, DROP COLUMN closed_by_staff_id;
            DROP TABLE cash_movements;
            """);
    }
}
