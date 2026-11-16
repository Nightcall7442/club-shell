using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The hall as it stands (SHELL_CHANGES 94): <c>pcs.rotation</c> — where the PC's monitor faces on the plan (0, 90, 180,
/// 270 degrees clockwise from «up»), and <c>clubs.hall_plan</c> — the plan's grid size, its objects (walls, bar, entrance,
/// aisles, labels) and zone areas (<c>HallPlan</c>); null — none drawn yet. Every PC keeps its <c>x</c>/<c>y</c>: PCs that
/// already share a cell stay so, and the hall editor shows them to be moved apart (a new placement onto a taken cell is
/// <c>409 cellTaken</c>).
/// </summary>
[Migration(2026111801, "pcs: rotation; clubs: hall plan")]
public sealed class M0021_HallLayout : Migration
{
    public override void Up() => Execute.Sql(
        """
        ALTER TABLE pcs ADD COLUMN rotation smallint NOT NULL DEFAULT 0 CHECK (rotation IN (0, 90, 180, 270));
        ALTER TABLE clubs ADD COLUMN hall_plan jsonb;
        """);

    public override void Down() => Execute.Sql(
        """
        ALTER TABLE pcs DROP COLUMN rotation;
        ALTER TABLE clubs DROP COLUMN hall_plan;
        """);
}
