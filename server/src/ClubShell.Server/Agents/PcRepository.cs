using System.Security.Cryptography;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Agents;

/// <summary>A row of <c>pcs</c> as the agent surface needs it (DESIGN §4.2).</summary>
public sealed class PcRow
{
    public Guid Id { get; init; }
    public Guid ClubId { get; init; }
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string Zone { get; init; } = "";
    public string? Hwid { get; init; }
    public string IpAddress { get; init; } = "";
    public bool Approved { get; init; }
    public bool Maintenance { get; init; }
    public byte[]? SigningSecret { get; init; }
    public int CredentialsVersion { get; init; }
    public string AgentVersion { get; init; } = "";
    public string ShellVersion { get; init; } = "";
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>The open session of the PC: the status and <c>Pc.currentSessionId</c> are derived from it (DESIGN §6.6).</summary>
    public Guid? OpenSessionId { get; init; }

    public string? OpenSessionState { get; init; }

    /// <summary>
    /// Derived status (DESIGN §6.6): maintenance, else offline without a live socket or a fresh heartbeat, else locked or
    /// busy by the open session, else free.
    /// </summary>
    public PcStatus Status(bool connected, DateTimeOffset now, TimeSpan offlineAfter) =>
        Maintenance ? PcStatus.Maintenance
        : !connected && (LastHeartbeatAt is not { } at || now - at >= offlineAfter) ? PcStatus.Offline
        : OpenSessionState == "locked" ? PcStatus.Locked
        : OpenSessionState is not null ? PcStatus.Busy
        : PcStatus.Free;

    /// <summary>The wire <c>Pc</c>; <c>hwid</c> is set only for the PC itself.</summary>
    public Pc ToPc(PcStatus status, bool withHwid) => new(
        Id, Name, Zone, Number, withHwid ? Hwid : null, IpAddress, status, CurrentSessionId: OpenSessionId, AgentVersion,
        ShellVersion.Length > 0 ? ShellVersion : "0.0.0", LastHeartbeatAt ?? CreatedAt);
}

/// <summary>Outcome of <see cref="PcRepository.RefreshAsync"/>; anything but <see cref="Ok"/> is a <c>401</c> reason.</summary>
public enum RefreshOutcome
{
    Ok,
    Revoked,
    Expired,
    Reused,
}

/// <summary>PC registry, agent credentials, heartbeats, telemetry and agent events (DESIGN §3.2, §4.2).</summary>
public sealed class PcRepository(NpgsqlDataSource db)
{
    private const string Columns = """
        id, club_id, number, name, zone, hwid, ip_address, approved, maintenance, signing_secret, credentials_version,
        agent_version, shell_version, last_heartbeat_at, created_at, deleted_at,
        (SELECT s.id FROM sessions s WHERE s.pc_id = pcs.id AND s.state <> 'ended') AS open_session_id,
        (SELECT s.state FROM sessions s WHERE s.pc_id = pcs.id AND s.state <> 'ended') AS open_session_state
        """;

    public async Task<PcRow?> FindAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<PcRow>($"SELECT {Columns} FROM pcs WHERE id = @id", new { id });
    }

    /// <summary>The live PCs and seats of a club by seat number (the hall map, <c>PcStatusWorker</c>).</summary>
    public async Task<IReadOnlyList<PcRow>> ListAsync(Guid? clubId = null)
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<PcRow>(
            $"SELECT {Columns} FROM pcs WHERE deleted_at IS NULL AND (@clubId IS NULL OR club_id = @clubId) ORDER BY number, created_at", new { clubId })).ToList();
    }

    /// <summary>
    /// Registration by HWID (DESIGN §3.2, §9) in one transaction; registrations of a club are serialized on its row.
    /// Known HWID: same PC, inventory updated, approval/status untouched. Unknown: a new PC, pre-filled with the seat of
    /// the club's live PC with the same MAC (<paramref name="request"/>'s <c>previousPcId</c> preferred) — such a PC is
    /// always pending, it may only take the seat once approved. An approved PC gets a new signing secret,
    /// <c>credentials_version + 1</c> (every earlier token of the PC is revoked) and a fresh refresh token.
    /// A pending PC gets no credentials. A HWID of another club is <c>409 conflict</c>.
    /// </summary>
    public async Task<(PcRow Pc, string? RefreshToken)> RegisterAsync(
        Guid clubId, AgentRegisterRequest request, string mac, bool autoApprove, TimeSpan refreshTtl, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("SELECT 1 FROM clubs WHERE id = @clubId FOR UPDATE", new { clubId }, tx);

        var hardware = ServerJson.Jsonb(JsonDefaults.Serialize(request.Hardware));
        var known = await c.QuerySingleOrDefaultAsync<PcRow>(
            $"SELECT {Columns} FROM pcs WHERE hwid = @hwid AND deleted_at IS NULL", new { hwid = request.Hwid }, tx);
        Guid id;
        if (known is not null)
        {
            if (known.ClubId != clubId)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "HWID belongs to a PC of another club", new { reason = "hwidTaken" });
            }

            id = known.Id;
            await c.ExecuteAsync(
                """
                UPDATE pcs SET machine_name = @machineName, mac_address = @mac, ip_address = @ip, hardware = @hardware::jsonb,
                               agent_version = @agentVersion, updated_at = @now
                WHERE id = @id
                """,
                new { id, machineName = request.MachineName, mac, ip = request.IpAddress, hardware, agentVersion = request.AgentVersion, now },
                tx);
        }
        else
        {
            var seat = await c.QuerySingleOrDefaultAsync<SeatRow>(
                """
                SELECT number, name, zone, x, y, device_kind FROM pcs
                WHERE club_id = @clubId AND deleted_at IS NULL AND mac_address = @mac
                ORDER BY id = @previous DESC, created_at DESC LIMIT 1
                """,
                new { clubId, mac, previous = request.PreviousPcId ?? Guid.Empty },
                tx);
            var number = seat?.Number ?? await c.ExecuteScalarAsync<int>(
                "SELECT coalesce(max(number), 0) + 1 FROM pcs WHERE club_id = @clubId AND deleted_at IS NULL", new { clubId }, tx);
            var approved = autoApprove && seat is null;
            id = Guid.CreateVersion7(now);
            await c.ExecuteAsync(
                """
                INSERT INTO pcs (id, club_id, number, name, zone, x, y, device_kind, hwid, mac_address, machine_name, ip_address,
                                 hardware, approved, maintenance, agent_version, created_at, updated_at)
                VALUES (@id, @clubId, @number, @name, @zone, @x, @y, @deviceKind, @hwid, @mac, @machineName, @ip,
                        @hardware::jsonb, @approved, NOT @approved, @agentVersion, @now, @now)
                """,
                new
                {
                    id, clubId, number, name = seat?.Name ?? $"PC-{number:D2}", zone = seat?.Zone ?? "", x = seat?.X ?? 0, y = seat?.Y ?? 0,
                    deviceKind = seat?.DeviceKind ?? "pc", hwid = request.Hwid, mac, machineName = request.MachineName,
                    ip = request.IpAddress, hardware, approved, agentVersion = request.AgentVersion, now,
                },
                tx);
        }

        string? refresh = null;
        if (await c.ExecuteScalarAsync<bool>("SELECT approved FROM pcs WHERE id = @id", new { id }, tx))
        {
            await c.ExecuteAsync(
                """
                UPDATE pcs SET signing_secret = @secret, credentials_version = credentials_version + 1 WHERE id = @id;
                DELETE FROM agent_refresh_tokens WHERE pc_id = @id;
                """,
                new { id, secret = RandomNumberGenerator.GetBytes(32) },
                tx);
            refresh = await IssueRefreshTokenAsync(c, tx, id, refreshTtl, now);
        }

        var pc = await c.QuerySingleAsync<PcRow>($"SELECT {Columns} FROM pcs WHERE id = @id", new { id }, tx);
        await tx.CommitAsync();
        return (pc, refresh);
    }

    /// <summary>
    /// One-time rotation (DESIGN §3.2): the token is consumed by <c>UPDATE … WHERE used_at IS NULL RETURNING</c>; a used
    /// token means it leaked, so the PC's <c>cv</c> is bumped and all its refresh tokens deleted (<see cref="RefreshOutcome.Reused"/>).
    /// A deleted PC, an outdated <c>cv</c> or another HWID is <see cref="RefreshOutcome.Revoked"/>. The signing secret is
    /// not rotated in v1.
    /// </summary>
    public async Task<(RefreshOutcome Outcome, PcRow? Pc, string? RefreshToken)> RefreshAsync(string token, string hwid, TimeSpan refreshTtl, DateTimeOffset now)
    {
        var hash = TokenService.HashRefreshToken(token);
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var row = await c.QuerySingleOrDefaultAsync<RefreshRow>(
            "UPDATE agent_refresh_tokens SET used_at = @now WHERE token_hash = @hash AND used_at IS NULL RETURNING pc_id, cv, expires_at",
            new { hash, now },
            tx);
        if (row is null)
        {
            var reusedBy = await c.QuerySingleOrDefaultAsync<Guid?>("SELECT pc_id FROM agent_refresh_tokens WHERE token_hash = @hash", new { hash }, tx);
            if (reusedBy is { } pcId)
            {
                await c.ExecuteAsync(
                    """
                    UPDATE pcs SET credentials_version = credentials_version + 1 WHERE id = @pcId;
                    DELETE FROM agent_refresh_tokens WHERE pc_id = @pcId;
                    """,
                    new { pcId },
                    tx);
            }

            var owner = reusedBy is { } id ? await c.QuerySingleAsync<PcRow>($"SELECT {Columns} FROM pcs WHERE id = @id", new { id }, tx) : null;
            await tx.CommitAsync();
            return (owner is null ? RefreshOutcome.Revoked : RefreshOutcome.Reused, owner, null);
        }

        var pc = await c.QuerySingleOrDefaultAsync<PcRow>($"SELECT {Columns} FROM pcs WHERE id = @id", new { id = row.PcId }, tx);
        var outcome = row.ExpiresAt <= now ? RefreshOutcome.Expired
            : pc is null || pc.DeletedAt is not null || pc.CredentialsVersion != row.Cv || !SameHwid(pc.Hwid, hwid) ? RefreshOutcome.Revoked
            : RefreshOutcome.Ok;
        var refresh = outcome == RefreshOutcome.Ok ? await IssueRefreshTokenAsync(c, tx, row.PcId, refreshTtl, now) : null;
        await tx.CommitAsync();
        return (outcome, pc, refresh);
    }

    public async Task RecordHeartbeatAsync(Guid id, HeartbeatRequest request, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE pcs SET last_heartbeat_at = @now, last_heartbeat = @heartbeat::jsonb, agent_version = @agentVersion,
                           shell_version = @shellVersion, ip_address = @ip
            WHERE id = @id
            """,
            new
            {
                id, now, heartbeat = ServerJson.Jsonb(JsonDefaults.Serialize(request)), agentVersion = request.AgentVersion,
                shellVersion = request.ShellVersion, ip = request.IpAddress,
            });
    }

    /// <summary>
    /// Telemetry batch (DESIGN §4.2): samples to <c>pc_metrics</c> (a repeated sample time is kept once), diagnostic
    /// events — <c>callAdmin</c> included, it is the only channel of an admin call in v1 — to <c>telemetry_events</c>,
    /// inventory to <c>pcs.hardware</c>. One transaction, two array inserts.
    /// </summary>
    public async Task IngestTelemetryAsync(PcRow pc, TelemetryBatch batch, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO pc_metrics (pc_id, at, data)
            SELECT @pcId, at, data::jsonb FROM unnest(@ats, @data) AS s(at, data)
            ON CONFLICT DO NOTHING
            """,
            new { pcId = pc.Id, ats = batch.Samples.Select(s => Utc(s.At)).ToArray(), data = batch.Samples.Select(s => ServerJson.Jsonb(JsonDefaults.Serialize(s))).ToArray() },
            tx);
        await c.ExecuteAsync(
            """
            INSERT INTO telemetry_events (club_id, pc_id, kind, at, data, received_at)
            SELECT @clubId, @pcId, kind, at, data::jsonb, @now FROM unnest(@kinds, @ats, @data) AS e(kind, at, data)
            """,
            new
            {
                clubId = pc.ClubId, pcId = pc.Id, now,
                kinds = batch.Events.Select(e => e.Kind).ToArray(),
                ats = batch.Events.Select(e => Utc(e.At)).ToArray(),
                data = batch.Events.Select(e => ServerJson.Jsonb(e.Data.GetRawText())).ToArray(),
            },
            tx);
        if (batch.Hardware is { } hardware)
        {
            await c.ExecuteAsync("UPDATE pcs SET hardware = @hardware::jsonb WHERE id = @id", new { id = pc.Id, hardware = ServerJson.Jsonb(JsonDefaults.Serialize(hardware)) }, tx);
        }

        await tx.CommitAsync();
    }

    /// <summary>
    /// A WS <c>event</c> frame (DESIGN §6.3): <c>anticheatViolation</c> → <c>anticheat_reports</c>; <c>hardwareChanged</c>
    /// → <c>pcs.hardware</c> and <c>telemetry_events</c>; the other five → <c>telemetry_events</c> only (billing and status
    /// come from REST).
    /// </summary>
    public async Task WriteAgentEventAsync(PcRow pc, AgentEventType type, string name, DateTimeOffset at, JsonElement payload, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        var data = ServerJson.Jsonb(payload.GetRawText());
        if (type == AgentEventType.AnticheatViolation)
        {
            await c.ExecuteAsync(
                "INSERT INTO anticheat_reports (id, club_id, pc_id, data, at) VALUES (@id, @clubId, @pcId, @data::jsonb, @at)",
                new { id = Guid.CreateVersion7(now), clubId = pc.ClubId, pcId = pc.Id, data, at });
            return;
        }

        if (type == AgentEventType.HardwareChanged && payload.TryGetProperty("hardware", out var hardware) && hardware.ValueKind == JsonValueKind.Object)
        {
            await c.ExecuteAsync("UPDATE pcs SET hardware = @hardware::jsonb WHERE id = @id", new { id = pc.Id, hardware = ServerJson.Jsonb(hardware.GetRawText()) });
        }

        await c.ExecuteAsync(
            "INSERT INTO telemetry_events (club_id, pc_id, kind, at, data, received_at) VALUES (@clubId, @pcId, @name, @at, @data::jsonb, @now)",
            new { clubId = pc.ClubId, pcId = pc.Id, name, at, data, now });
    }

    private static async Task<string> IssueRefreshTokenAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid pcId, TimeSpan ttl, DateTimeOffset now)
    {
        var token = TokenService.NewRefreshToken();
        await c.ExecuteAsync(
            """
            INSERT INTO agent_refresh_tokens (token_hash, pc_id, cv, expires_at, created_at)
            SELECT @hash, id, credentials_version, @expires, @now FROM pcs WHERE id = @pcId
            """,
            new { hash = TokenService.HashRefreshToken(token), pcId, expires = now + ttl, now },
            tx);
        return token;
    }

    private sealed class SeatRow
    {
        public int Number { get; init; }
        public string Name { get; init; } = "";
        public string Zone { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public string DeviceKind { get; init; } = "pc";
    }

    private sealed class RefreshRow
    {
        public Guid PcId { get; init; }
        public int Cv { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private static bool SameHwid(string? stored, string presented) =>
        stored is not null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(stored), System.Text.Encoding.UTF8.GetBytes(presented));

    /// <summary>Array parameters bypass the Dapper handler: UTC and truncated to ms here (DESIGN §4).</summary>
    private static DateTime Utc(DateTimeOffset at) => new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
}
