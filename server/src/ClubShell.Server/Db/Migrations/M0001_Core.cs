using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// Core and agents (DESIGN §4.2, M0001): network and club, PCs, agent credentials, command queue, metrics,
/// telemetry and anti-cheat reports, staff for the bootstrap, idempotency keys. Raw SQL: snake_case, <c>text</c> + CHECK instead of PG enums,
/// UTC <c>timestamptz</c> truncated to milliseconds, composite FKs <c>(club_id, x)</c> keep rows inside one club.
/// </summary>
[Migration(2026092801, "core: networks, clubs, pcs, agent credentials, commands, telemetry, anticheat, staff, idempotency")]
public sealed class M0001_Core : Migration
{
    private const string Now = "date_trunc('milliseconds', now())";

    public override void Up()
    {
        Execute.Sql($"""
            CREATE TABLE networks (
                id          uuid PRIMARY KEY,
                name        text NOT NULL,
                created_at  timestamptz NOT NULL DEFAULT {Now}
            );

            CREATE TABLE clubs (
                id                        uuid PRIMARY KEY,
                network_id                uuid NOT NULL REFERENCES networks(id),
                name                      text NOT NULL,
                city                      text NOT NULL DEFAULT '',
                address                   text NOT NULL DEFAULT '',
                time_zone                 text NOT NULL DEFAULT 'Asia/Tashkent',
                currency                  text NOT NULL DEFAULT 'UZS' CHECK (currency = 'UZS'),
                enrollment_key_hash       bytea NULL,
                prev_enrollment_key_hash  bytea NULL,
                api_key                   text NULL,
                settings                  jsonb NOT NULL DEFAULT jsonb_build_object(),
                health_settings           jsonb NOT NULL DEFAULT jsonb_build_object(),
                settings_version          integer NOT NULL DEFAULT 1,
                config_version            integer NOT NULL DEFAULT 1,
                catalog_version           integer NOT NULL DEFAULT 1,
                policy                    jsonb NULL,
                policy_version            integer NOT NULL DEFAULT 0,
                disabled                  boolean NOT NULL DEFAULT false,
                created_at                timestamptz NOT NULL DEFAULT {Now},
                updated_at                timestamptz NOT NULL DEFAULT {Now}
            );

            CREATE TABLE pcs (
                id                   uuid PRIMARY KEY,
                club_id              uuid NOT NULL REFERENCES clubs(id),
                number               integer NOT NULL CHECK (number BETWEEN 1 AND 9999),
                name                 text NOT NULL,
                zone                 text NOT NULL DEFAULT '',
                x                    integer NOT NULL DEFAULT 0,
                y                    integer NOT NULL DEFAULT 0,
                device_kind          text NOT NULL DEFAULT 'pc' CHECK (device_kind IN ('pc', 'console', 'vr', 'other')),
                hwid                 text NULL,
                mac_address          text NULL,
                machine_name         text NOT NULL DEFAULT '',
                ip_address           text NOT NULL DEFAULT '',
                approved             boolean NOT NULL DEFAULT false,
                maintenance          boolean NOT NULL DEFAULT false,
                credentials_version  integer NOT NULL DEFAULT 1,
                signing_secret       bytea NULL,
                agent_version        text NOT NULL DEFAULT '',
                shell_version        text NOT NULL DEFAULT '',
                last_heartbeat_at    timestamptz NULL,
                last_heartbeat       jsonb NULL,
                hardware             jsonb NULL,
                created_at           timestamptz NOT NULL DEFAULT {Now},
                updated_at           timestamptz NOT NULL DEFAULT {Now},
                deleted_at           timestamptz NULL,
                UNIQUE (club_id, id)
            );
            -- Only approved PCs hold a seat: a pending PC may carry the pre-filled seat of the PC it replaces (DESIGN §3.2,
            -- §9 disk replacement) while that one still exists; approving it into a taken seat is a unique violation (409).
            CREATE UNIQUE INDEX pcs_number ON pcs (club_id, number) WHERE deleted_at IS NULL AND approved;
            -- HWID is unique across the whole network: a HWID of another club answers 409 at registration.
            CREATE UNIQUE INDEX pcs_hwid ON pcs (hwid) WHERE hwid IS NOT NULL AND deleted_at IS NULL;

            CREATE TABLE agent_refresh_tokens (
                token_hash  bytea PRIMARY KEY,
                pc_id       uuid NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
                cv          integer NOT NULL,
                expires_at  timestamptz NOT NULL,
                used_at     timestamptz NULL,
                created_at  timestamptz NOT NULL DEFAULT {Now}
            );
            CREATE INDEX agent_refresh_tokens_pc ON agent_refresh_tokens (pc_id);

            CREATE TABLE staff (
                id          uuid PRIMARY KEY,
                network_id  uuid NOT NULL REFERENCES networks(id),
                club_id     uuid NOT NULL REFERENCES clubs(id),
                name        text NOT NULL,
                role        text NOT NULL CHECK (role IN ('owner', 'cashier')),
                pin_hmac    bytea NOT NULL,
                active      boolean NOT NULL DEFAULT true,
                created_at  timestamptz NOT NULL DEFAULT {Now},
                updated_at  timestamptz NOT NULL DEFAULT {Now},
                UNIQUE (network_id, pin_hmac)
            );

            CREATE TABLE staff_tokens (
                token_hash    bytea PRIMARY KEY,
                staff_id      uuid NOT NULL REFERENCES staff(id) ON DELETE CASCADE,
                club_id       uuid NOT NULL REFERENCES clubs(id),
                created_at    timestamptz NOT NULL DEFAULT {Now},
                last_used_at  timestamptz NOT NULL DEFAULT {Now},
                expires_at    timestamptz NOT NULL,
                revoked_at    timestamptz NULL
            );
            CREATE INDEX staff_tokens_staff ON staff_tokens (staff_id);

            CREATE TABLE agent_commands (
                id                  uuid PRIMARY KEY,
                club_id             uuid NOT NULL,
                pc_id               uuid NOT NULL,
                name                text NOT NULL,
                payload             jsonb NULL,
                issued_by_staff_id  uuid NULL REFERENCES staff(id),
                supersedes          uuid NULL,
                created_at          timestamptz NOT NULL DEFAULT {Now},
                expires_at          timestamptz NULL,
                delivered_at        timestamptz NULL,
                acked_at            timestamptz NULL,
                ack                 jsonb NULL,
                superseded_at       timestamptz NULL,
                FOREIGN KEY (club_id, pc_id) REFERENCES pcs (club_id, id) ON DELETE CASCADE
            );
            CREATE INDEX agent_commands_pending ON agent_commands (pc_id, created_at) WHERE acked_at IS NULL AND superseded_at IS NULL;

            CREATE TABLE pc_metrics (
                pc_id  uuid NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
                at     timestamptz NOT NULL,
                data   jsonb NOT NULL,
                PRIMARY KEY (pc_id, at)
            );

            CREATE TABLE telemetry_events (
                id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                club_id      uuid NOT NULL,
                pc_id        uuid NOT NULL,
                kind         text NOT NULL,
                at           timestamptz NOT NULL,
                data         jsonb NULL,
                received_at  timestamptz NOT NULL DEFAULT {Now},
                FOREIGN KEY (club_id, pc_id) REFERENCES pcs (club_id, id) ON DELETE CASCADE
            );
            CREATE INDEX telemetry_events_received ON telemetry_events (club_id, received_at);

            -- WS event anticheatViolation (S1) and POST /anticheat/report (S3).
            CREATE TABLE anticheat_reports (
                id       uuid PRIMARY KEY,
                club_id  uuid NOT NULL,
                pc_id    uuid NOT NULL,
                data     jsonb NOT NULL,
                at       timestamptz NOT NULL,
                FOREIGN KEY (club_id, pc_id) REFERENCES pcs (club_id, id) ON DELETE CASCADE
            );

            -- principal = pc:<pcId> | club:<clubId>: a key never replays another tenant's response (DESIGN §7.1).
            CREATE TABLE idempotency_keys (
                principal     text NOT NULL,
                method        text NOT NULL,
                path          text NOT NULL,
                key           uuid NOT NULL,
                request_hash  bytea NOT NULL,
                status_code   integer NULL,
                response      jsonb NULL,
                created_at    timestamptz NOT NULL DEFAULT {Now},
                PRIMARY KEY (principal, method, path, key)
            );
            CREATE INDEX idempotency_keys_created ON idempotency_keys (created_at);
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE idempotency_keys;
            DROP TABLE anticheat_reports;
            DROP TABLE telemetry_events;
            DROP TABLE pc_metrics;
            DROP TABLE agent_commands;
            DROP TABLE staff_tokens;
            DROP TABLE staff;
            DROP TABLE agent_refresh_tokens;
            DROP TABLE pcs;
            DROP TABLE clubs;
            DROP TABLE networks;
            """);
    }
}
