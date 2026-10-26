using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// When the agent last closed its socket itself (SHELL_CHANGES 44): it does so when it stops — Windows shutting down or
/// restarting, the service stopped or updated — and never when the PC loses power, crashes or drops off the network. A PC
/// that goes offline after that is not an incident (<c>PcStatusWorker</c>). Cleared when the agent connects again.
/// </summary>
[Migration(2026102602, "pcs: the agent's last own socket close")]
public sealed class M0015_PcGoodbye : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE pcs ADD COLUMN goodbye_at timestamptz;");

    public override void Down() => Execute.Sql("ALTER TABLE pcs DROP COLUMN goodbye_at;");
}
