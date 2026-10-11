using System.Text.Json;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// PC health at the counter (slice S5): <c>adminHealth</c> — every live PC of the club with its <see cref="HealthDiagnosis"/>,
/// open ticket and games volume (D-73), the thresholds and the last 100 tickets; <c>adminUpdateTicket</c> — staff move a
/// repair ticket (journal <c>pcCommand</c>, <c>meta.kind = repair</c>); resolving the last unresolved ticket that took its PC out of service
/// puts the PC back (<c>maintenance = false</c>); <c>adminSaveHealthSettings</c> — the owner's thresholds, clamped
/// (<c>x-clamp</c>). Tickets are opened, escalated and acted on by the <see cref="HealthWorker"/>.
/// </summary>
public static class HealthEndpoints
{
    public static readonly string[] Operations = ["adminHealth", "adminUpdateTicket", "adminSaveHealthSettings"];

    private static readonly string[] Statuses = ["open", "inWork", "resolved"];

    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/health");
        api.MapGet("", ReportAsync).WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapPatch("/tickets/{id}", UpdateTicketAsync).WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapPatch("/settings", SaveSettingsAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
    }

    private static async Task<IResult> ReportAsync(HttpContext context, NpgsqlDataSource db, PcRepository pcs, AgentSocketHub hub, AgentOptions agents, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var now = clock.GetUtcNow();
        var hall = (await pcs.ListAsync(staff.ClubId))
            .Select(pc => (Pc: pc, Status: pc.Status(hub.IsConnected(pc.Id), now, TimeSpan.FromSeconds(agents.OfflineAfterSec)))).ToList();
        await using var c = await db.OpenConnectionAsync();
        var settings = await HealthSettings.LoadAsync(c, null, staff.ClubId);
        var diagnoses = await HealthDiagnosis.DiagnoseAsync(c, null, [.. hall.Select(h => (h.Pc.Id, h.Status))], settings, now);
        var open = (await c.QueryAsync<TicketRow>(
                $"SELECT DISTINCT ON (pc_id) {TicketRow.Columns} FROM health_tickets WHERE club_id = @ClubId AND status <> 'resolved' ORDER BY pc_id, created_at DESC, id DESC",
                new { staff.ClubId }))
            .ToDictionary(t => t.PcId, t => t.ToWire());
        var tickets = (await c.QueryAsync<TicketRow>(
                $"SELECT {TicketRow.Columns} FROM health_tickets WHERE club_id = @ClubId ORDER BY created_at DESC, id DESC LIMIT 100", new { staff.ClubId }))
            .Select(t => t.ToWire()).ToList();
        var volumes = (await c.QueryAsync<(Guid Id, string GamesVolume)>(
                """
                SELECT id, (last_heartbeat -> 'gamesVolume')::text FROM pcs
                WHERE club_id = @ClubId AND deleted_at IS NULL AND last_heartbeat -> 'gamesVolume' IS NOT NULL
                """,
                new { staff.ClubId }))
            .ToDictionary(v => v.Id, v => JsonElement.Parse(v.GamesVolume));
        var items = hall.Select(h =>
        {
            var d = diagnoses[h.Pc.Id];
            return new PcHealth(
                h.Pc.Id, h.Pc.Name, h.Pc.Zone, h.Status, d.Score, d.Live, d.Baseline, d.Hourly, d.Issues, open.GetValueOrDefault(h.Pc.Id),
                volumes.TryGetValue(h.Pc.Id, out var volume) ? volume : null);
        }).ToList();
        return AdminJson.Ok(new HealthReport(settings, items, tickets));
    }

    private static async Task<IResult> UpdateTicketAsync(HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<TicketUpdateRequest>(body, "status");
        if (!Statuses.Contains(r.Status, StringComparer.Ordinal))
        {
            throw ApiException.Validation("status", "unknown");
        }

        var note = AdminInput.OptionalText(r.Note, "note", 500);
        var ticketId = Guid.TryParse(id, out var parsed) ? parsed : throw ApiException.NotFound("ticket");
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("SET LOCAL lock_timeout = '10s'", transaction: tx);
        var now = clock.GetUtcNow();
        var ticket = await c.QuerySingleOrDefaultAsync<TicketRow>(
            $"SELECT {TicketRow.Columns} FROM health_tickets WHERE id = @ticketId AND club_id = @ClubId FOR UPDATE", new { ticketId, staff.ClubId }, tx)
            ?? throw ApiException.NotFound("ticket");
        await c.ExecuteAsync("SAVEPOINT ticket", transaction: tx);
        try
        {
            await c.ExecuteAsync(
                """
                UPDATE health_tickets SET status = @Status, note = coalesce(@note, note), updated_at = @now,
                                          resolved_at = CASE WHEN @Status = 'resolved' THEN @now END
                WHERE id = @ticketId AND club_id = @ClubId
                """,
                new { ticketId, staff.ClubId, r.Status, note, now }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "health_tickets_open")
        {
            // Reopening a resolved ticket while a newer one for the same PC and problem is open.
            await c.ExecuteAsync("ROLLBACK TO SAVEPOINT ticket", transaction: tx);
            throw ApiException.Validation("status", "taken");
        }

        if (r.Status == "resolved" && ticket.PutMaintenance)
        {
            await c.ExecuteAsync(
                """
                UPDATE pcs SET maintenance = false, updated_at = @now
                WHERE id = @PcId AND club_id = @ClubId AND maintenance
                  AND NOT EXISTS (SELECT 1 FROM health_tickets WHERE pc_id = @PcId AND put_maintenance AND status <> 'resolved')
                """,
                new { ticket.PcId, staff.ClubId, now }, tx);
        }

        await Audit.WriteAsync(c, tx, staff, now, "pcCommand", pcId: ticket.PcId, detail: $"{ticket.PcName} · {r.Status}",
            meta: new { kind = "repair", ticketId, status = r.Status });
        var updated = await c.QuerySingleAsync<TicketRow>(
            $"SELECT {TicketRow.Columns} FROM health_tickets WHERE id = @ticketId AND club_id = @ClubId", new { ticketId, staff.ClubId }, tx);
        await tx.CommitAsync();
        return AdminJson.Ok(new TicketResponse(updated.ToWire()));
    }

    private static async Task<IResult> SaveSettingsAsync(HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("body", "schema", "JSON object expected");
        }

        ContractSchemas.Validate("AdminHealthSettingsPatch", body);
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("SELECT 1 FROM clubs WHERE id = @ClubId FOR UPDATE", new { staff.ClubId }, tx);
        var settings = HealthSettings.Patch(body, await HealthSettings.LoadAsync(c, tx, staff.ClubId));
        var now = clock.GetUtcNow();
        await c.ExecuteAsync(
            "UPDATE clubs SET health_settings = @json::jsonb, updated_at = @now WHERE id = @ClubId",
            new { staff.ClubId, now, json = JsonSerializer.Serialize(settings, AdminJson.Options) }, tx);
        await Audit.WriteAsync(c, tx, staff, now, "healthSettings", detail: staff.Name, meta: settings);
        await tx.CommitAsync();
        return AdminJson.Ok(new HealthSettingsResponse(settings));
    }
}
