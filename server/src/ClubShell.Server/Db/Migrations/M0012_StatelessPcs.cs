using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// A PC without persistent storage (SHELL_CHANGES 25): a diskless seat boots a clean system drive every time, so its
/// agent has lost its signing secret and registers anew at every boot. The owner marks such a PC; its re-registration
/// without a proof is then expected rather than a security event, and <c>Agents:RegisterProof=enforce</c> lets it through
/// while the PC is offline. Defaults to false, so old servers and unmarked PCs behave as before.
/// </summary>
[Migration(2026102501, "pcs: PCs without persistent storage")]
public sealed class M0012_StatelessPcs : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE pcs ADD COLUMN stateless boolean NOT NULL DEFAULT false;");

    public override void Down() => Execute.Sql("ALTER TABLE pcs DROP COLUMN stateless;");
}
