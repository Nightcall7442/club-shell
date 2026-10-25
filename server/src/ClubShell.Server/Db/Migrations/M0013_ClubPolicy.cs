using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// A club's own PC policy (SHELL_CHANGES 27): once the owner saves the policy in the console, the seed applied at every
/// startup (D-14) leaves that club's policy alone. Defaults to false, so every club keeps following the seed until its
/// owner changes something; going back to the seed clears the flag.
/// </summary>
[Migration(2026102502, "clubs: the club's own PC policy")]
public sealed class M0013_ClubPolicy : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE clubs ADD COLUMN policy_custom boolean NOT NULL DEFAULT false;");

    public override void Down() => Execute.Sql("ALTER TABLE clubs DROP COLUMN policy_custom;");
}
