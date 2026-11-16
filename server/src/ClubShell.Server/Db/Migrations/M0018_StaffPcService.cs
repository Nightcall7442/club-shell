using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// PC service sign-in (SERVER_API.md §4.1): a staff member the owner marked may sign in to their own Windows account on a
/// club PC with their PIN, to install games, launchers and programs (<c>POST /agents/{pcId}/staff/verify</c>). An owner
/// always may; a cashier only with the mark, which nobody has until the owner sets it.
/// </summary>
[Migration(2026111601, "staff: PC service sign-in")]
public sealed class M0018_StaffPcService : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE staff ADD COLUMN pc_service boolean NOT NULL DEFAULT false;");

    public override void Down() => Execute.Sql("ALTER TABLE staff DROP COLUMN pc_service;");
}
