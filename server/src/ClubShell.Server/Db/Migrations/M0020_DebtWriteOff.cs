using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Debt write-off (SHELL_CHANGES 90, <c>POST /admin/wallet/write-off</c>): an owner always may write a debt off, a cashier
/// only with the owner's mark (<c>staff.debt_write_off</c>, off until set).
/// </summary>
[Migration(2026111701, "staff: debt write-off")]
public sealed class M0020_DebtWriteOff : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE staff ADD COLUMN debt_write_off boolean NOT NULL DEFAULT false;");

    public override void Down() => Execute.Sql("ALTER TABLE staff DROP COLUMN debt_write_off;");
}
