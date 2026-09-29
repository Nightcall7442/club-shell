using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The rest of the console (DESIGN §4.2, M0005). Part A: promo codes (the <c>promoCodes</c> array of <c>clubs.settings</c>
/// lives here because of its server counters <c>used</c>/<c>uses_left</c>, §4.2) and their redemptions — one per client
/// and code (D-15), the primary key is what refuses a second one (§4.4); the shop's products with the tracked stock
/// (<c>stock_qty</c> NULL = not tracked, never below 0; changed atomically, §4.4); a login name of 1–32 characters, as the
/// console's registration allows (<c>AdminClientCreateRequest.username</c>).
/// Part B: the club API key <c>ck_</c> no longer in plain text — an HMAC under the PIN pepper for the constant-time check and
/// an AES-GCM box under a key derived from the same pepper, so <c>GET /admin/club/api-key</c> can still show it (§3.5); the
/// arrays of <c>clubs.settings</c> with server counters, automation rules and webhooks (§4.2), in the owner's order
/// (<c>position</c>); rule firings, the dedup of every automation firing that must survive a restart (§4.4); the webhook
/// outbox the <c>WebhookWorker</c> drains (§8); repair tickets of PC health, one open per PC and kind; the hash of the live
/// banner set the <c>ClubTickWorker</c> compares (§8, OQ-21).
/// </summary>
[Migration(2026093003, "admin: promo codes, redemptions, products, api key, automation, webhooks, health tickets")]
public sealed class M0005_Admin : Migration
{
    private const string Now = "date_trunc('milliseconds', now())";

    public override void Up()
    {
        Execute.Sql($"""
            ALTER TABLE users DROP CONSTRAINT users_username_check;
            ALTER TABLE users ADD CONSTRAINT users_username_check CHECK (length(username) BETWEEN 1 AND 32);

            -- Codes compare case-insensitively; a code removed from the settings is soft-deleted, so its redemptions stay.
            CREATE TABLE promo_codes (
                id          uuid PRIMARY KEY,
                club_id     uuid NOT NULL REFERENCES clubs(id),
                code        text NOT NULL CHECK (length(code) BETWEEN 1 AND 32),
                kind        text NOT NULL CHECK (kind IN ('bonus', 'discountPct')),
                value       bigint NOT NULL CHECK (value >= 0),
                uses_left   integer NULL CHECK (uses_left >= 0),
                used        integer NOT NULL DEFAULT 0 CHECK (used >= 0),
                expires_at  timestamptz NULL,
                created_at  timestamptz NOT NULL DEFAULT {Now},
                deleted_at  timestamptz NULL,
                UNIQUE (club_id, id)
            );
            CREATE UNIQUE INDEX promo_codes_code ON promo_codes (club_id, upper(code)) WHERE deleted_at IS NULL;

            -- op_id: the ledger operation of the bonus (NULL for a code worth 0); staff_id NULL = the club API key.
            CREATE TABLE promo_redemptions (
                promo_code_id  uuid NOT NULL REFERENCES promo_codes(id),
                user_id        uuid NOT NULL REFERENCES users(id),
                op_id          uuid NULL,
                staff_id       uuid NULL REFERENCES staff(id),
                redeemed_at    timestamptz NOT NULL,
                PRIMARY KEY (promo_code_id, user_id)
            );

            CREATE TABLE products (
                id          uuid PRIMARY KEY,
                club_id     uuid NOT NULL REFERENCES clubs(id),
                title       text NOT NULL,
                category    text NOT NULL CHECK (category IN ('food', 'drink', 'snack', 'service', 'merch', 'time')),
                price       bigint NOT NULL CHECK (price >= 0),
                image_url   text NOT NULL DEFAULT '',
                in_stock    boolean NOT NULL DEFAULT true,
                stock_qty   integer NULL CHECK (stock_qty >= 0),
                tags        text[] NOT NULL DEFAULT ARRAY[]::text[],
                created_at  timestamptz NOT NULL DEFAULT {Now},
                updated_at  timestamptz NOT NULL DEFAULT {Now},
                deleted_at  timestamptz NULL,
                UNIQUE (club_id, id)
            );
            CREATE INDEX products_club ON products (club_id) WHERE deleted_at IS NULL;

            ALTER TABLE clubs DROP COLUMN api_key;
            ALTER TABLE clubs ADD COLUMN api_key_hash bytea NULL, ADD COLUMN api_key_sealed bytea NULL, ADD COLUMN banners_hash text NULL;

            -- AdminAutomationRule; ids come from the console (UUID text), the server's counters are fired/last_fired_at.
            CREATE TABLE automation_rules (
                club_id        uuid NOT NULL REFERENCES clubs(id),
                id             text NOT NULL CHECK (length(id) BETWEEN 1 AND 64),
                position       integer NOT NULL,
                name           text NOT NULL,
                enabled        boolean NOT NULL,
                trigger        jsonb NOT NULL,
                action         jsonb NOT NULL,
                fired          integer NOT NULL DEFAULT 0 CHECK (fired >= 0),
                last_fired_at  timestamptz NULL,
                PRIMARY KEY (club_id, id)
            );

            -- One row per (rule, target) that fired: a session for minutesLeft/sessionStarted/visitCount, a top-up, an idle
            -- stretch of a PC. No FK: a rule saved again under the same id keeps its firings.
            CREATE TABLE rule_firings (
                club_id     uuid NOT NULL REFERENCES clubs(id),
                rule_id     text NOT NULL,
                target_key  text NOT NULL,
                fired_at    timestamptz NOT NULL,
                PRIMARY KEY (club_id, rule_id, target_key)
            );

            CREATE TABLE webhooks (
                club_id      uuid NOT NULL REFERENCES clubs(id),
                id           text NOT NULL CHECK (length(id) BETWEEN 1 AND 64),
                position     integer NOT NULL,
                url          text NOT NULL,
                events       text[] NOT NULL,
                enabled      boolean NOT NULL,
                last_status  integer NULL,
                last_at      timestamptz NULL,
                PRIMARY KEY (club_id, id)
            );

            -- sent_at: delivered, refused (SSRF) or given up after the retries; a webhook removed from the settings takes its
            -- pending deliveries with it.
            CREATE TABLE webhook_outbox (
                id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                club_id     uuid NOT NULL,
                webhook_id  text NOT NULL,
                event       text NOT NULL,
                payload     jsonb NOT NULL,
                attempts    integer NOT NULL DEFAULT 0,
                next_at     timestamptz NOT NULL,
                sent_at     timestamptz NULL,
                created_at  timestamptz NOT NULL,
                FOREIGN KEY (club_id, webhook_id) REFERENCES webhooks (club_id, id) ON DELETE CASCADE
            );
            CREATE INDEX webhook_outbox_due ON webhook_outbox (next_at) WHERE sent_at IS NULL;

            -- AdminHealthTicket; put_maintenance = autoMaintenance (the system took the PC out of service for it).
            CREATE TABLE health_tickets (
                id               uuid PRIMARY KEY,
                club_id          uuid NOT NULL,
                pc_id            uuid NOT NULL,
                pc_name          text NOT NULL,
                kind             text NOT NULL CHECK (kind IN ('cpuHot', 'gpuHot', 'cpuTrend', 'gpuTrend', 'fpsDrop', 'unstable')),
                severity         text NOT NULL CHECK (severity IN ('high', 'medium')),
                status           text NOT NULL CHECK (status IN ('open', 'inWork', 'resolved')),
                params           jsonb NOT NULL,
                note             text NOT NULL DEFAULT '',
                put_maintenance  boolean NOT NULL DEFAULT false,
                created_at       timestamptz NOT NULL,
                updated_at       timestamptz NOT NULL,
                resolved_at      timestamptz NULL,
                FOREIGN KEY (club_id, pc_id) REFERENCES pcs (club_id, id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX health_tickets_open ON health_tickets (pc_id, kind) WHERE status <> 'resolved';
            CREATE INDEX health_tickets_club ON health_tickets (club_id, created_at DESC);
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE health_tickets;
            DROP TABLE webhook_outbox;
            DROP TABLE webhooks;
            DROP TABLE rule_firings;
            DROP TABLE automation_rules;
            ALTER TABLE clubs DROP COLUMN api_key_hash, DROP COLUMN api_key_sealed, DROP COLUMN banners_hash;
            ALTER TABLE clubs ADD COLUMN api_key text NULL;
            DROP TABLE products;
            DROP TABLE promo_redemptions;
            DROP TABLE promo_codes;
            ALTER TABLE users DROP CONSTRAINT users_username_check;
            ALTER TABLE users ADD CONSTRAINT users_username_check CHECK (length(username) BETWEEN 3 AND 32);
            """);
    }
}
