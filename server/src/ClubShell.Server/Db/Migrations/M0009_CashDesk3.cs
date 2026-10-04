using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Cash desk, part 3 (DESIGN §4.2, «Касса, часть 3»): bar sales and their voids (append-only, outside the wallet ledger for
/// money paid by a method — <c>ledger_entries.user_id</c> is required; a sale from the balance links its <c>purchase</c> row),
/// where a product came from and who archived it (the seed file no longer deletes or revives desk products, D-58), the
/// admin-call inbox (mutable on purpose: its status changes, every change is journaled) and the lookup of staff session
/// events (a session moved to another PC, D-61). <c>features.callAdmin</c> stored as <c>false</c> is turned on once (D-62):
/// it never had any effect (the server always sent false) and is usually the default the console copied; the owner's
/// switch stays the control, so Down does not turn it back.
/// </summary>
[Migration(2026100501, "cash desk part 3: bar sales, desk products, admin calls, moved sessions")]
public sealed class M0009_CashDesk3 : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE products
                ADD COLUMN source text NOT NULL DEFAULT 'seed' CHECK (source IN ('seed', 'desk')),
                ADD COLUMN deleted_by text NULL CHECK (deleted_by IN ('seed', 'desk'));

            CREATE TABLE shop_sales (
                id           uuid PRIMARY KEY,
                club_id      uuid NOT NULL REFERENCES clubs(id),
                shift_id     uuid NOT NULL REFERENCES shifts(id),
                staff_id     uuid NOT NULL REFERENCES staff(id),
                staff_name   text NOT NULL,
                kind         text NOT NULL CHECK (kind IN ('sale', 'void')),
                void_of      uuid NULL REFERENCES shop_sales(id),
                user_id      uuid NULL REFERENCES users(id),
                pc_id        uuid NULL,
                method       text NOT NULL CHECK (method IN ('cash', 'card', 'payme', 'click', 'uzum', 'balance')),
                total        bigint NOT NULL CHECK (total > 0),
                ledger_id    uuid NULL REFERENCES ledger_entries(id),
                reason_code  text NULL CHECK (reason_code IN ('mistake', 'returned', 'defect', 'other')),
                note         text NULL CHECK (note IS NULL OR length(note) BETWEEN 1 AND 200),
                created_at   timestamptz NOT NULL,
                FOREIGN KEY (club_id, pc_id) REFERENCES pcs (club_id, id),
                CHECK ((kind = 'void') = (void_of IS NOT NULL)),
                CHECK ((kind = 'void') = (reason_code IS NOT NULL)),
                CHECK (reason_code IS DISTINCT FROM 'other' OR length(coalesce(note, '')) >= 3),
                CHECK (method <> 'balance' OR user_id IS NOT NULL),
                CHECK ((method = 'balance') = (ledger_id IS NOT NULL))
            );
            CREATE UNIQUE INDEX shop_sales_one_void ON shop_sales (void_of) WHERE void_of IS NOT NULL;
            CREATE INDEX shop_sales_shift ON shop_sales (shift_id, created_at);
            CREATE INDEX shop_sales_club ON shop_sales (club_id, created_at);
            CREATE TRIGGER shop_sales_append_only BEFORE UPDATE OR DELETE ON shop_sales
                FOR EACH ROW EXECUTE FUNCTION forbid_change();
            CREATE TRIGGER shop_sales_no_truncate BEFORE TRUNCATE ON shop_sales
                FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();

            CREATE TABLE shop_sale_lines (
                sale_id     uuid NOT NULL REFERENCES shop_sales(id),
                line        smallint NOT NULL CHECK (line BETWEEN 1 AND 20),
                product_id  uuid NOT NULL REFERENCES products(id),
                title       text NOT NULL,
                qty         integer NOT NULL CHECK (qty BETWEEN 1 AND 99),
                price       bigint NOT NULL CHECK (price >= 0),
                PRIMARY KEY (sale_id, line)
            );
            CREATE INDEX shop_sale_lines_product ON shop_sale_lines (product_id);
            CREATE TRIGGER shop_sale_lines_append_only BEFORE UPDATE OR DELETE ON shop_sale_lines
                FOR EACH ROW EXECUTE FUNCTION forbid_change();
            CREATE TRIGGER shop_sale_lines_no_truncate BEFORE TRUNCATE ON shop_sale_lines
                FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();

            CREATE TABLE admin_calls (
                id                    uuid PRIMARY KEY,
                club_id               uuid NOT NULL REFERENCES clubs(id),
                pc_id                 uuid NOT NULL,
                pc_name               text NOT NULL,
                pc_number             integer NOT NULL,
                user_id               uuid NULL REFERENCES users(id),
                user_name             text NULL,
                category              text NOT NULL CHECK (category IN ('help', 'technical', 'order', 'other', 'problem')),
                message               text NULL CHECK (message IS NULL OR length(message) BETWEEN 1 AND 2000),
                source                text NOT NULL CHECK (source IN ('direct', 'telemetry', 'report')),
                at                    timestamptz NOT NULL,
                received_at           timestamptz NOT NULL,
                status                text NOT NULL CHECK (status IN ('open', 'acked', 'resolved')),
                repeat                boolean NOT NULL DEFAULT false,
                acked_at              timestamptz NULL,
                acked_by_staff_id     uuid NULL REFERENCES staff(id),
                acked_by_name         text NULL,
                resolved_at           timestamptz NULL,
                resolved_by_staff_id  uuid NULL REFERENCES staff(id),
                resolved_by_name      text NULL,
                FOREIGN KEY (club_id, pc_id) REFERENCES pcs (club_id, id),
                UNIQUE (pc_id, at),
                CHECK (status <> 'acked' OR acked_at IS NOT NULL),
                CHECK (status <> 'resolved' OR resolved_at IS NOT NULL)
            );
            CREATE INDEX admin_calls_live ON admin_calls (club_id, received_at DESC) WHERE status <> 'resolved';
            CREATE INDEX admin_calls_pc ON admin_calls (pc_id, received_at DESC);

            CREATE INDEX session_events_server ON session_events (session_id, type) WHERE source <> 'agent';

            UPDATE clubs
            SET settings = jsonb_set(settings, '{features,callAdmin}', 'true'),
                settings_version = settings_version + 1,
                config_version = config_version + 1
            WHERE settings #>> '{features,callAdmin}' = 'false';
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP INDEX session_events_server;
            DROP TABLE admin_calls;
            DROP TABLE shop_sale_lines;
            DROP TABLE shop_sales;
            ALTER TABLE products DROP COLUMN deleted_by, DROP COLUMN source;
            """);
    }
}
