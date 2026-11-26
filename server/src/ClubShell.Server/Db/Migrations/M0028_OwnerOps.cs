using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The owner's own money at the desk (D-101): a top-up, a seat, a payout, a write-off or a bar sale the owner makes joins no
/// cashier shift. <c>owner_op</c> marks those ledger rows, journal entries and bar sales (their <c>shift_id</c> stays NULL),
/// so X/Z, the expected drawer, «Принято за смену» and the cashier's feed leave them out and the <c>noShift</c> control flag
/// does not count them; a bar sale may now have no shift (only as an owner operation).
/// </summary>
[Migration(2026112601, "owner operations")]
public sealed class M0028_OwnerOps : Migration
{
    public override void Up() => Execute.Sql(
        """
        ALTER TABLE ledger_entries ADD COLUMN owner_op boolean NOT NULL DEFAULT false;
        ALTER TABLE audit_entries ADD COLUMN owner_op boolean NOT NULL DEFAULT false;
        ALTER TABLE shop_sales ADD COLUMN owner_op boolean NOT NULL DEFAULT false, ALTER COLUMN shift_id DROP NOT NULL,
            ADD CONSTRAINT shop_sales_shift_or_owner CHECK (shift_id IS NOT NULL OR owner_op);
        CREATE INDEX audit_entries_owner_op ON audit_entries (club_id, at DESC, id DESC) WHERE owner_op;
        """);

    public override void Down() => Execute.Sql(
        """
        DROP INDEX audit_entries_owner_op;
        ALTER TABLE shop_sales DROP CONSTRAINT shop_sales_shift_or_owner, DROP COLUMN owner_op;
        ALTER TABLE audit_entries DROP COLUMN owner_op;
        ALTER TABLE ledger_entries DROP COLUMN owner_op;
        """);
}
