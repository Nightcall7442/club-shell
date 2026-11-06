using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Why the agent closed its socket (SHELL_CHANGES 60): <c>reboot</c> or <c>shutdown</c> when the agent started that itself,
/// else <c>NULL</c>. A PC that went for a reboot and did not come back within <c>Agents:PlannedOfflineMin</c> is an incident
/// after all (<c>PcStatusWorker</c>). Cleared with <c>goodbye_at</c>.
/// </summary>
[Migration(2026110501, "pcs: why the agent closed its socket")]
public sealed class M0016_PcGoodbyeReason : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE pcs ADD COLUMN goodbye_reason text;");

    public override void Down() => Execute.Sql("ALTER TABLE pcs DROP COLUMN goodbye_reason;");
}
