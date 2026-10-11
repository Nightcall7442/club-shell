using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Re-registration proof (D-78): until when an approved PC may get new credentials without proving it holds its signing
/// secret. The owner's <c>maintenance:true</c> sets it to now + <c>Agents:ReenrollWindowMin</c>, <c>maintenance:false</c>
/// and any new credentials clear it; only <c>Agents:RegisterProof=enforce</c> reads it. Nullable, so old servers ignore it.
/// </summary>
[Migration(2026101202, "pcs: re-enroll window of the re-registration proof")]
public sealed class M0011_ReenrollWindow : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE pcs ADD COLUMN reenroll_until timestamptz NULL;");

    public override void Down() => Execute.Sql("ALTER TABLE pcs DROP COLUMN reenroll_until;");
}
