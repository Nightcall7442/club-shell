using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Pcs;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The hall editor (slice S4, DESIGN §9): <c>adminPcs</c> (seats with map position, device, hardware and last telemetry),
/// and for the owner <c>adminAddPc</c> (a seat without a PC yet: approved, <c>offline</c>, no HWID), <c>adminUpdatePc</c>
/// (partial; <c>maintenance:false</c> is also how a PC waiting for approval is approved, D-7) and <c>adminDeletePc</c> (soft
/// delete: the agent's tokens answer <c>401 revoked</c>, its socket closes 4401; <c>409 pcBusy</c> during a session). A
/// taken seat number is <c>400 number taken</c> (the contract declares no 409 there). Changing name, zone or number bumps
/// <c>config_version</c> (they are in <c>AgentServerConfig</c>), a zone also <c>catalog_version</c>, and the PC gets
/// <c>refreshConfig</c> with explicit flags (§5.9).
/// </summary>
public static class PcAdminEndpoints
{
    public static readonly string[] Operations = ["adminPcs", "adminAddPc", "adminUpdatePc", "adminDeletePc"];

    private static readonly string[] Devices = ["pc", "console", "vr", "other"];

    public static void MapPcAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/pcs");
        api.MapGet("", ListAsync).WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapPost("", AddAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapPatch("/{pcId:guid}", UpdateAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapDelete("/{pcId:guid}", DeleteAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
    }

    private static async Task<IResult> ListAsync(
        HttpContext context, NpgsqlDataSource db, PcRepository pcs, AgentSocketHub hub, AgentOptions agents, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        var map = (await c.QueryAsync<(Guid Id, int X, int Y, string DeviceKind, string? Hardware, string? Metrics)>(
            """
            SELECT p.id, p.x, p.y, p.device_kind, p.hardware::text,
                   (SELECT m.data::text FROM pc_metrics m WHERE m.pc_id = p.id ORDER BY m.at DESC LIMIT 1)
            FROM pcs p WHERE p.club_id = @ClubId AND p.deleted_at IS NULL
            """,
            new { staff.ClubId })).ToDictionary(m => m.Id);
        var items = (await pcs.ListAsync(staff.ClubId)).Select(pc =>
        {
            var wire = pc.ToPc(pc.Status(hub.IsConnected(pc.Id), now, TimeSpan.FromSeconds(agents.OfflineAfterSec)), withHwid: false);
            var m = map[pc.Id];
            return new AdminHallPc(
                wire.Id, wire.Name, wire.Zone, wire.Number, wire.Hwid, wire.IpAddress, wire.Status, wire.CurrentSessionId, wire.AgentVersion,
                wire.ShellVersion, wire.LastHeartbeatAt, m.X, m.Y, m.DeviceKind,
                m.Hardware is null ? null : JsonElement.Parse(m.Hardware), m.Metrics is null ? null : JsonElement.Parse(m.Metrics));
        }).ToList();
        return AdminJson.Ok(new AdminHallPcList(items, await CounterEndpoints.ZonesAsync(c, staff.ClubId)));
    }

    private static async Task<IResult> AddAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminPcChange>(body, "zone", "number");
        Check(r);
        var device = r.Device ?? "pc";
        var number = r.Number!.Value;
        var name = r.Name ?? $"{(device == "pc" ? "PC" : device.ToUpperInvariant())}-{number:D2}";
        return await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7(now);
            await SeatAsync(c, tx, () => c.ExecuteAsync(
                """
                INSERT INTO pcs (id, club_id, number, name, zone, x, y, device_kind, approved, maintenance, created_at, updated_at)
                VALUES (@id, @ClubId, @number, @name, @Zone, @x, @y, @device, true, false, @now, @now)
                """,
                new { id, staff.ClubId, number, name, r.Zone, x = r.X ?? 0, y = r.Y ?? 0, device, now },
                tx));
            await Audit.WriteAsync(c, tx, staff, now, "pcAdd", pcId: id, detail: $"{r.Zone} · {number}", meta: new { device });
            var pc = new Pc(id, name, r.Zone!, number, null, "", PcStatus.Offline, null, "", "0.0.0", now);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminPcResponse(pc)));
        });
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context, Guid pcId, [FromBody] JsonElement body, NpgsqlDataSource db, PcRepository pcs, CommandDispatcher commands,
        AgentSocketHub hub, AgentOptions agents, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminPcChange>(body);
        Check(r);
        var pc = await CounterEndpoints.LivePcAsync(pcs, staff, pcId);
        var now = clock.GetUtcNow();
        var config = (r.Name is not null && r.Name != pc.Name) || (r.Number is not null && r.Number != pc.Number);
        var zone = r.Zone is not null && r.Zone != pc.Zone;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await SeatAsync(c, tx, () => c.ExecuteAsync(
                """
                UPDATE pcs SET zone = coalesce(@Zone, zone), name = coalesce(@Name, name), number = coalesce(@Number, number),
                               x = coalesce(@X, x), y = coalesce(@Y, y), device_kind = coalesce(@Device, device_kind),
                               maintenance = coalesce(@Maintenance, maintenance),
                               approved = approved OR coalesce(NOT @Maintenance, false), updated_at = @now
                WHERE id = @pcId
                """,
                new { pcId, r.Zone, r.Name, r.Number, r.X, r.Y, r.Device, r.Maintenance, now },
                tx));
            if (config || zone)
            {
                await c.ExecuteAsync(
                    "UPDATE clubs SET config_version = config_version + 1, catalog_version = catalog_version + @catalog, updated_at = @now WHERE id = @ClubId",
                    new { staff.ClubId, catalog = zone ? 1 : 0, now }, tx);
            }

            await Audit.WriteAsync(c, tx, staff, now, "pcUpdate", pcId: pc.Id, detail: pc.Name, meta: r);
            await tx.CommitAsync();
        }

        if (config || zone)
        {
            // Zone decides the catalog and the tariffs the PC shows (§5.9); the flags must be explicit.
            await commands.EnqueueAsync(pc.ClubId, pc.Id, NewCommand.RefreshConfig(new RefreshConfigCommand(Config: true, Games: zone ? true : null, Tariffs: zone ? true : null)));
        }

        var updated = (await pcs.FindAsync(pc.Id))!;
        return AdminJson.Ok(new AdminPcResponse(updated.ToPc(updated.Status(hub.IsConnected(pc.Id), clock.GetUtcNow(), TimeSpan.FromSeconds(agents.OfflineAfterSec)), withHwid: false)));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, Guid pcId, NpgsqlDataSource db, PcRepository pcs, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var pc = await CounterEndpoints.LivePcAsync(pcs, staff, pcId);
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();

            // A session insert takes KEY SHARE on this row (its FK): FOR UPDATE orders the two, so the check below sees a
            // session committed first. ponytail: one racing in right after the delete lands on the deleted seat; its agent
            // is revoked at once, the cashier ends it from the map.
            await c.ExecuteAsync("SELECT 1 FROM pcs WHERE id = @pcId FOR UPDATE", new { pcId }, tx);
            if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM sessions WHERE pc_id = @pcId AND state <> 'ended')", new { pcId }, tx))
            {
                throw SessionService.Conflict("pcBusy");
            }

            await c.ExecuteAsync("UPDATE pcs SET deleted_at = @now, updated_at = @now WHERE id = @pcId", new { pcId, now }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "pcDelete", pcId: pcId, detail: pc.Name);
            await tx.CommitAsync();
        }

        await hub.RevokeAsync(pcId);
        return AdminJson.Ok(AdminJson.OkBody);
    }

    /// <summary>A seat number another approved PC holds is <c>400 number taken</c> (<c>pcs_number</c>).</summary>
    private static async Task SeatAsync(NpgsqlConnection c, NpgsqlTransaction tx, Func<Task<int>> write)
    {
        await c.ExecuteAsync("SAVEPOINT seat", transaction: tx);
        try
        {
            await write();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "pcs_number")
        {
            await c.ExecuteAsync("ROLLBACK TO SAVEPOINT seat", transaction: tx);
            throw ApiException.Validation("number", "taken");
        }
    }

    private static void Check(AdminPcChange r)
    {
        if (r.Zone is { } zone && (zone.Length is 0 or > 32))
        {
            throw ApiException.Validation("zone", zone.Length == 0 ? "min" : "max");
        }

        if (r.Name is { } name && (name.Length is 0 or > 32))
        {
            throw ApiException.Validation("name", name.Length == 0 ? "min" : "max");
        }

        if (r.Number is < 1 or > 9999)
        {
            throw ApiException.Validation("number", r.Number < 1 ? "min" : "max");
        }

        if (r.X is < 0 or > 200 || r.Y is < 0 or > 200)
        {
            throw ApiException.Validation(r.X is < 0 or > 200 ? "x" : "y", "range");
        }

        if (r.Device is { } device && !Devices.Contains(device, StringComparer.Ordinal))
        {
            throw ApiException.Validation("device", "enum");
        }
    }
}
