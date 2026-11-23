using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// A player's own account from a PC (D-92): <c>users.signup_pc_id</c> — the PC that created it (null for the desk's and
/// older accounts; set null when the PC is deleted), indexed with the creation time for the per-PC limit.
/// </summary>
[Migration(2026112301, "users: signup_pc_id")]
public sealed class M0023_SelfSignup : Migration
{
    public override void Up() => Execute.Sql("""
        ALTER TABLE users ADD COLUMN signup_pc_id uuid NULL REFERENCES pcs(id) ON DELETE SET NULL;
        CREATE INDEX users_signup_pc ON users (signup_pc_id, created_at) WHERE signup_pc_id IS NOT NULL;
        """);

    public override void Down() => Execute.Sql("""
        DROP INDEX users_signup_pc;
        ALTER TABLE users DROP COLUMN signup_pc_id;
        """);
}
