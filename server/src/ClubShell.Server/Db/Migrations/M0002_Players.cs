using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Players, money and sessions (DESIGN §4.2, M0002): users and their tokens, login failures, QR logins, club client
/// profiles, wallets and the append-only ledger, tariffs (soft-deleted), sessions with the price snapshot and the agent's
/// session events. Discounts (groups, happy hours, loyalty levels) live in <c>clubs.settings</c>, not in tables (§4.2).
/// Open-session races are settled by the partial unique indexes <c>sessions_open_pc</c>/<c>sessions_open_user</c> (§4.4).
/// </summary>
[Migration(2026092901, "players: users, tokens, wallets, ledger, tariffs, sessions, session events")]
public sealed class M0002_Players : Migration
{
    private const string Now = "date_trunc('milliseconds', now())";

    public override void Up()
    {
        Execute.Sql($"""
            -- Rows of an append-only table are never changed: corrections are new rows (ledger: type adjustment).
            CREATE FUNCTION forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
            END
            $$;

            -- Network level: one account and one wallet in every club of the network (DESIGN §4.1).
            CREATE TABLE users (
                id               uuid PRIMARY KEY,
                network_id       uuid NOT NULL REFERENCES networks(id),
                username         text NOT NULL CHECK (length(username) BETWEEN 3 AND 32),
                display_name     text NOT NULL,
                avatar_url       text NULL,
                role             text NOT NULL DEFAULT 'member' CHECK (role IN ('guest', 'member', 'vip', 'admin')),
                locale           text NOT NULL DEFAULT 'ru' CHECK (locale IN ('en', 'ru', 'uz')),
                flags            text[] NOT NULL DEFAULT ARRAY[]::text[],
                password_hash    text NULL,
                unlock_pin_hash  text NULL,
                card_id          text NULL,
                banned           boolean NOT NULL DEFAULT false,
                transient        boolean NOT NULL DEFAULT false,
                created_at       timestamptz NOT NULL DEFAULT {Now},
                last_seen_at     timestamptz NULL,
                deleted_at       timestamptz NULL
            );
            CREATE UNIQUE INDEX users_username ON users (network_id, lower(username));
            CREATE UNIQUE INDEX users_card ON users (network_id, lower(card_id)) WHERE card_id IS NOT NULL;

            -- One player per PC: a new login on the PC displaces the previous player's token (DESIGN §3.4).
            CREATE TABLE user_tokens (
                token_hash  bytea PRIMARY KEY,
                user_id     uuid NOT NULL REFERENCES users(id),
                pc_id       uuid NOT NULL UNIQUE REFERENCES pcs(id) ON DELETE CASCADE,
                created_at  timestamptz NOT NULL,
                expires_at  timestamptz NOT NULL
            );
            CREATE INDEX user_tokens_user ON user_tokens (user_id);

            -- Failed password logins per lower-cased username, known or not (attemptsLeft must not tell which names exist).
            -- The agent repeats a 401 once after a refresh with the same X-Trace-Id (N2): the first repeat of a trace is
            -- free, every further one counts (attempts).
            CREATE TABLE login_failures (
                network_id  uuid NOT NULL REFERENCES networks(id),
                username    text NOT NULL,
                trace_id    uuid NOT NULL,
                attempts    integer NOT NULL DEFAULT 1,
                at          timestamptz NOT NULL,
                PRIMARY KEY (network_id, username, trace_id)
            );

            CREATE TABLE qr_logins (
                token_hash    bytea PRIMARY KEY,
                club_id       uuid NOT NULL,
                pc_id         uuid NOT NULL,
                user_id       uuid NULL REFERENCES users(id),
                created_at    timestamptz NOT NULL,
                expires_at    timestamptz NOT NULL,
                confirmed_at  timestamptz NULL,
                consumed_at   timestamptz NULL,
                FOREIGN KEY (club_id, pc_id) REFERENCES pcs (club_id, id) ON DELETE CASCADE
            );

            -- Club level: what a club knows about a player (group, blacklist, birth year for the minors' curfew).
            CREATE TABLE client_profiles (
                club_id      uuid NOT NULL REFERENCES clubs(id),
                user_id      uuid NOT NULL REFERENCES users(id),
                group_id     text NULL,
                note         text NOT NULL DEFAULT '',
                blacklisted  boolean NOT NULL DEFAULT false,
                phone        text NOT NULL DEFAULT '',
                birth_year   integer NULL,
                updated_at   timestamptz NOT NULL DEFAULT {Now},
                PRIMARY KEY (club_id, user_id)
            );

            -- main_balance caches SUM(ledger_entries.amount) of the user; it is changed only under this row's lock
            -- together with the ledger rows (Wallet/Ledger.cs, DESIGN §4.3). bonus_balance stays 0 in v1 (D-9).
            CREATE TABLE wallets (
                user_id         uuid PRIMARY KEY REFERENCES users(id),
                network_id      uuid NOT NULL REFERENCES networks(id),
                currency        text NOT NULL DEFAULT 'UZS' CHECK (currency = 'UZS'),
                main_balance    bigint NOT NULL DEFAULT 0,
                bonus_balance   bigint NOT NULL DEFAULT 0 CHECK (bonus_balance >= 0),
                lifetime_spent  bigint NOT NULL DEFAULT 0 CHECK (lifetime_spent >= 0),
                version         bigint NOT NULL DEFAULT 0,
                updated_at      timestamptz NOT NULL DEFAULT {Now}
            );

            CREATE TABLE tariffs (
                id               uuid PRIMARY KEY,
                club_id          uuid NOT NULL REFERENCES clubs(id),
                name             text NOT NULL,
                price_per_hour   bigint NOT NULL CHECK (price_per_hour >= 0),
                min_minutes      integer NOT NULL DEFAULT 1 CHECK (min_minutes >= 0),
                max_minutes      integer NULL,
                zones            text[] NOT NULL DEFAULT ARRAY[]::text[],
                time_windows     jsonb NOT NULL DEFAULT jsonb_build_array(),
                is_package       boolean NOT NULL DEFAULT false,
                package_minutes  integer NULL,
                package_price    bigint NULL,
                created_at       timestamptz NOT NULL DEFAULT {Now},
                updated_at       timestamptz NOT NULL DEFAULT {Now},
                deleted_at       timestamptz NULL,
                UNIQUE (club_id, id),
                CHECK (NOT is_package OR (package_minutes > 0 AND package_price >= 0))
            );

            -- Billing clock (DESIGN §5.4): used(t) = used_before_sec + (running ? t - running_since : 0).
            -- price_per_hour_snapshot/day_pct/discount_pct freeze the postpaid price at the start (§5.3).
            CREATE TABLE sessions (
                id                       uuid PRIMARY KEY,
                club_id                  uuid NOT NULL,
                pc_id                    uuid NOT NULL,
                user_id                  uuid NOT NULL REFERENCES users(id),
                tariff_id                uuid NOT NULL,
                state                    text NOT NULL CHECK (state IN ('active', 'paused', 'locked', 'ending', 'ended')),
                is_prepaid               boolean NOT NULL,
                origin                   text NOT NULL CHECK (origin IN ('kiosk', 'cashier', 'offline')),
                started_at               timestamptz NOT NULL,
                ended_at                 timestamptz NULL,
                end_reason               text NULL CHECK (end_reason IN ('user', 'timeUp', 'admin', 'idle', 'agentRestart', 'error')),
                purchased_sec            integer NOT NULL DEFAULT 0 CHECK (purchased_sec >= 0),
                used_before_sec          integer NOT NULL DEFAULT 0 CHECK (used_before_sec >= 0),
                running_since            timestamptz NULL,
                paused_at                timestamptz NULL,
                ends_at                  timestamptz NULL,
                last_transition_at       timestamptz NOT NULL,
                price_per_hour_snapshot  bigint NOT NULL,
                day_pct                  integer NOT NULL,
                discount_pct             integer NOT NULL,
                discount_reason          text NULL,
                charged_total            bigint NOT NULL DEFAULT 0,
                refunded_total           bigint NOT NULL DEFAULT 0,
                warnings_sent            integer[] NOT NULL DEFAULT ARRAY[]::integer[],
                created_by_staff_id      uuid NULL REFERENCES staff(id),
                client_session_id        uuid NULL,
                agent_reported           jsonb NULL,
                created_at               timestamptz NOT NULL,
                updated_at               timestamptz NOT NULL,
                FOREIGN KEY (club_id, pc_id) REFERENCES pcs (club_id, id),
                FOREIGN KEY (club_id, tariff_id) REFERENCES tariffs (club_id, id),
                CHECK ((state = 'ended') = (ended_at IS NOT NULL))
            );
            CREATE UNIQUE INDEX sessions_open_pc ON sessions (pc_id) WHERE state <> 'ended';
            CREATE UNIQUE INDEX sessions_open_user ON sessions (user_id) WHERE state <> 'ended';
            CREATE INDEX sessions_due ON sessions (ends_at) WHERE is_prepaid AND state IN ('active', 'locked', 'ending');
            CREATE INDEX sessions_user ON sessions (user_id, started_at DESC);
            CREATE INDEX sessions_client ON sessions (pc_id, client_session_id) WHERE client_session_id IS NOT NULL;

            -- Money (DESIGN §4.3): one operation = rows with one op_id; amount is signed, balance_after is taken under
            -- the wallet row lock; overdraft marks a row that took main_balance below 0 (postpaid settlement, replay).
            CREATE TABLE ledger_entries (
                id             uuid PRIMARY KEY,
                op_id          uuid NOT NULL,
                user_id        uuid NOT NULL REFERENCES wallets(user_id),
                network_id     uuid NOT NULL REFERENCES networks(id),
                club_id        uuid NULL REFERENCES clubs(id),
                type           text NOT NULL CHECK (type IN ('topUp', 'charge', 'refund', 'bonus', 'purchase', 'adjustment')),
                amount         bigint NOT NULL CHECK (amount <> 0),
                balance_after  bigint NOT NULL,
                method         text NULL CHECK (method IN ('cash', 'card', 'payme', 'click', 'uzum')),
                description    text NOT NULL,
                ref            text NULL,
                session_id     uuid NULL REFERENCES sessions(id),
                shift_id       uuid NULL,
                staff_id       uuid NULL REFERENCES staff(id),
                pc_id          uuid NULL,
                overdraft      boolean NOT NULL DEFAULT false,
                meta           jsonb NULL,
                created_at     timestamptz NOT NULL
            );
            CREATE INDEX ledger_entries_user ON ledger_entries (user_id, created_at DESC);
            CREATE INDEX ledger_entries_club ON ledger_entries (club_id, created_at);
            CREATE INDEX ledger_entries_shift ON ledger_entries (shift_id);
            CREATE INDEX ledger_entries_session ON ledger_entries (session_id);
            CREATE TRIGGER ledger_entries_append_only BEFORE UPDATE OR DELETE ON ledger_entries
                FOR EACH ROW EXECUTE FUNCTION forbid_change();
            CREATE TRIGGER ledger_entries_no_truncate BEFORE TRUNCATE ON ledger_entries
                FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();

            -- Agent session events (DESIGN §5.12); a repeated (session, type, at) of the agent is the same event.
            CREATE TABLE session_events (
                id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                session_id   uuid NOT NULL REFERENCES sessions(id),
                club_id      uuid NOT NULL REFERENCES clubs(id),
                source       text NOT NULL CHECK (source IN ('agent', 'server', 'staff')),
                type         text NOT NULL,
                at           timestamptz NOT NULL,
                received_at  timestamptz NOT NULL,
                data         jsonb NULL,
                applied      boolean NOT NULL
            );
            CREATE UNIQUE INDEX session_events_dedup ON session_events (session_id, type, at) WHERE source = 'agent';
            CREATE TRIGGER session_events_append_only BEFORE UPDATE OR DELETE ON session_events
                FOR EACH ROW EXECUTE FUNCTION forbid_change();
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE session_events;
            DROP TABLE ledger_entries;
            DROP TABLE sessions;
            DROP TABLE tariffs;
            DROP TABLE wallets;
            DROP TABLE client_profiles;
            DROP TABLE qr_logins;
            DROP TABLE login_failures;
            DROP TABLE user_tokens;
            DROP TABLE users;
            DROP FUNCTION forbid_change();
            """);
    }
}
