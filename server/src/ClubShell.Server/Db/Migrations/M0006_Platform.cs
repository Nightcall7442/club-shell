using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Several clubs on one server (platform administration, beyond the contract): every club gets a short code that the
/// console sends with the PIN. A PIN is unique only inside its network, so with more than one club the PIN alone cannot
/// say whose staff member it is; the code does (DESIGN §11, "Платформа").
/// </summary>
[Migration(2026100201, "platform: club codes")]
public sealed class M0006_Platform : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE clubs ADD COLUMN code text NULL CHECK (code ~ '^[A-Z0-9]{3,12}$');
            CREATE UNIQUE INDEX clubs_code ON clubs (code) WHERE code IS NOT NULL;
            """);
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX clubs_code; ALTER TABLE clubs DROP COLUMN code;");
    }
}
