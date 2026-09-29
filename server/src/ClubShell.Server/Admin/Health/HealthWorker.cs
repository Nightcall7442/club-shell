using System.Text.Json;
using ClubShell.Contracts.Pcs;
using ClubShell.Server.Agents;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// Repair tickets from telemetry (DESIGN §8), every 30 s under <c>pg_advisory_lock(CSHlth)</c>, per club: each issue of
/// <see cref="HealthDiagnosis"/> gets one open ticket per PC and kind (<c>health_tickets_open</c>) and the <c>hardware</c>
/// event; a medium ticket whose problem turned high is escalated in place; a problem whose ticket was resolved less than
/// 6 h ago is not reopened (the fix gets a chance to show). With <c>autoMaintenance</c>, a PC with an unresolved high
/// ticket is taken out of service (<c>maintenance</c>) as soon as it is free, and the ticket remembers it
/// (<c>autoMaintenance</c>): resolving it puts the PC back (<see cref="HealthEndpoints"/>). No simulated telemetry (the
/// mock plays PCs of its own). Tests call <see cref="RunOnceAsync"/> with the fixture's clock.
/// </summary>
public sealed class HealthWorker(
    NpgsqlDataSource db, PcRepository pcs, AgentSocketHub hub, AgentOptions agents, TimeProvider clock, ILogger<HealthWorker> logger) : BackgroundService
{
    public static readonly long LockKey = AdvisoryLocks.Key("CSHlth");

    private static readonly TimeSpan ReopenAfter = TimeSpan.FromHours(6);

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["cpuHot"] = "перегрев процессора",
        ["gpuHot"] = "перегрев видеокарты",
        ["cpuTrend"] = "процессор греется сильнее обычного",
        ["gpuTrend"] = "видеокарта греется сильнее обычного",
        ["fpsDrop"] = "упал FPS",
        ["unstable"] = "часто пропадает из сети",
    };

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> clubs;
        await using (var c = await db.OpenConnectionAsync(cancellationToken))
        {
            clubs = [.. await c.QueryAsync<Guid>("SELECT id FROM clubs WHERE NOT disabled ORDER BY created_at")];
        }

        foreach (var clubId in clubs)
        {
            await RunClubAsync(clubId, cancellationToken);
        }
    }

    private async Task RunClubAsync(Guid clubId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var hall = (await pcs.ListAsync(clubId)).Where(p => p.Approved)
            .Select(pc => (Pc: pc, Status: pc.Status(hub.IsConnected(pc.Id), now, TimeSpan.FromSeconds(agents.OfflineAfterSec)))).ToList();
        if (hall.Count == 0)
        {
            return;
        }

        await using var c = await db.OpenConnectionAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        await c.ExecuteAsync("SET LOCAL lock_timeout = '10s'", transaction: tx);
        var settings = await HealthSettings.LoadAsync(c, tx, clubId);
        var diagnoses = await HealthDiagnosis.DiagnoseAsync(c, tx, [.. hall.Select(h => (h.Pc.Id, h.Status))], settings, now);
        foreach (var (pc, _) in hall)
        {
            foreach (var issue in diagnoses[pc.Id].Issues)
            {
                await TicketAsync(c, tx, clubId, pc, issue, now);
            }
        }

        if (settings.AutoMaintenance)
        {
            // A free PC only: nobody is thrown out of a game. The session check is repeated in the UPDATE.
            var free = hall.Where(h => h.Status == PcStatus.Free).Select(h => h.Pc.Id).ToArray();
            var taken = await c.QueryAsync<(Guid Id, Guid PcId)>(
                """
                WITH taken AS (
                    UPDATE pcs SET maintenance = true, updated_at = @now
                    WHERE club_id = @clubId AND id = ANY(@free) AND NOT maintenance
                      AND EXISTS (SELECT 1 FROM health_tickets t WHERE t.pc_id = pcs.id AND t.severity = 'high' AND t.status <> 'resolved')
                      AND NOT EXISTS (SELECT 1 FROM sessions s WHERE s.pc_id = pcs.id AND s.state <> 'ended')
                    RETURNING id)
                UPDATE health_tickets t SET put_maintenance = true, updated_at = @now
                FROM taken WHERE t.club_id = @clubId AND t.pc_id = taken.id AND t.severity = 'high' AND t.status <> 'resolved'
                RETURNING t.id, t.pc_id
                """,
                new { clubId, free, now }, tx);
            foreach (var (id, pcId) in taken)
            {
                logger.LogInformation("PC {PcId} taken out of service for repair ticket {TicketId}", pcId, id);
            }
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static async Task TicketAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, PcRow pc, HealthIssue issue, DateTimeOffset now)
    {
        var parameters = JsonSerializer.Serialize(issue.Params, AdminJson.Options);
        var open = await c.QuerySingleOrDefaultAsync<(Guid Id, string Severity)?>(
            "SELECT id, severity FROM health_tickets WHERE club_id = @clubId AND pc_id = @Id AND kind = @Kind AND status <> 'resolved' FOR UPDATE",
            new { clubId, pc.Id, issue.Kind }, tx);
        if (open is { } ticket)
        {
            if (ticket.Severity == "medium" && issue.Severity == "high")
            {
                await c.ExecuteAsync(
                    "UPDATE health_tickets SET severity = 'high', params = @parameters::jsonb, updated_at = @now WHERE id = @Id AND club_id = @clubId",
                    new { ticket.Id, clubId, parameters, now }, tx);
            }

            return;
        }

        if (await c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM health_tickets WHERE club_id = @clubId AND pc_id = @Id AND kind = @Kind AND status = 'resolved' AND resolved_at > @since)",
                new { clubId, pc.Id, issue.Kind, since = now - ReopenAfter }, tx))
        {
            return;
        }

        var id = Guid.CreateVersion7(now);
        await c.ExecuteAsync(
            """
            INSERT INTO health_tickets (id, club_id, pc_id, pc_name, kind, severity, status, params, created_at, updated_at)
            VALUES (@id, @clubId, @pcId, @Name, @Kind, @Severity, 'open', @parameters::jsonb, @now, @now)
            """,
            new { id, clubId, pcId = pc.Id, pc.Name, issue.Kind, issue.Severity, parameters, now }, tx);
        await Webhooks.EnqueueAsync(c, tx, clubId, "hardware", now,
            $"{pc.Name}: {Titles[issue.Kind]}{(issue.Severity == "high" ? " — серьёзно" : "")}", new { pcId = pc.Id, ticketId = id, kind = issue.Kind });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var held = await db.OpenConnectionAsync(stoppingToken);
        await held.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@key)", new { key = LockKey }, cancellationToken: stoppingToken));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Health pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
        }
    }
}
