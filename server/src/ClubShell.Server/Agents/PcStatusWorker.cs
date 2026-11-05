using System.Collections.Concurrent;
using ClubShell.Contracts.Pcs;
using ClubShell.Server.Admin;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Agents;

/// <summary>
/// Derived PC status watcher (DESIGN §6.6, §8), every 5 s under <c>pg_advisory_lock(CSPcSt)</c>: a PC that turns
/// <c>offline</c> (no live socket, no fresh heartbeat) gets a <c>telemetry_events kind=pcOffline</c> row — the health
/// issue <c>unstable</c> counts them — and the <c>pcOffline</c> event for the webhooks, in one transaction. Not when the
/// stop was planned (SHELL_CHANGES 44): a <c>reboot</c>/<c>shutdown</c> command went to it, or its agent closed the socket
/// itself (it does when Windows shuts down or restarts and when the service stops), within
/// <see cref="AgentOptions.PlannedOfflineMin"/>; a PC in maintenance is never offline. A planned reboot — a <c>reboot</c>
/// command, or the agent's goodbye saying it started one (<c>pcs.goodbye_reason</c>) — is expected back: still offline
/// <see cref="AgentOptions.PlannedOfflineMin"/> later (no boot, a blue screen, stuck at PXE), it is an incident after all,
/// with <c>afterPlannedReboot</c> in its data (SHELL_CHANGES 60). A planned shutdown, or a goodbye without a reason (Windows
/// shut down by someone, the service stopped), never is. A PC that turns
/// <c>free</c> starts its idle stretch for the <c>pcIdleMinutes</c> automation (<see cref="AutomationService.IdleAsync"/>).
/// No push: <c>pcStatusChanged</c> is notImplemented, the console polls.
/// ponytail: the last status lives in memory, so the first pass after a restart only records, and an idle stretch starts
/// again at the restart (a rule fires once per stretch, <c>rule_firings</c>).
/// </summary>
public sealed class PcStatusWorker(
    NpgsqlDataSource db, PcRepository pcs, AgentSocketHub hub, AgentOptions agents, AutomationService automation, TimeProvider clock,
    ILogger<PcStatusWorker> logger) : BackgroundService
{
    public static readonly long LockKey = AdvisoryLocks.Key("CSPcSt");

    private readonly ConcurrentDictionary<Guid, PcStatus> _last = new();

    /// <summary>PC → when it went offline for a planned reboot it has not come back from yet.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _rebooting = new();

    /// <summary>PC → when it was first seen free in its current stretch.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _freeSince = new();

    public async Task RunOnceAsync()
    {
        var now = clock.GetUtcNow();
        var window = TimeSpan.FromMinutes(agents.PlannedOfflineMin);
        var went = new List<PcRow>();
        var late = new List<PcRow>();
        var free = new List<(Guid ClubId, Guid PcId, DateTimeOffset FreeSince)>();
        foreach (var pc in await pcs.ListAsync())
        {
            var status = pc.Status(hub.IsConnected(pc.Id), now, TimeSpan.FromSeconds(agents.OfflineAfterSec));
            if (_last.TryGetValue(pc.Id, out var before) && before != status && status == PcStatus.Offline)
            {
                went.Add(pc);
            }

            if (status != PcStatus.Offline)
            {
                _rebooting.TryRemove(pc.Id, out _);
            }
            else if (_rebooting.TryGetValue(pc.Id, out var since) && now - since >= window)
            {
                _rebooting.TryRemove(pc.Id, out _);
                late.Add(pc);
            }

            _last[pc.Id] = status;
            if (status == PcStatus.Free)
            {
                free.Add((pc.ClubId, pc.Id, _freeSince.GetOrAdd(pc.Id, now)));
            }
            else
            {
                _freeSince.TryRemove(pc.Id, out _);
            }
        }

        if (went.Count > 0)
        {
            await using var c = await db.OpenConnectionAsync();
            var planned = (await c.QueryAsync<(Guid Id, bool Reboot)>(
                """
                SELECT p.id,
                       (p.goodbye_at > @since AND p.goodbye_reason = 'reboot')
                       OR EXISTS (SELECT 1 FROM agent_commands a
                                  WHERE a.pc_id = p.id AND a.name = 'reboot' AND a.created_at > @since AND a.superseded_at IS NULL)
                FROM pcs p
                WHERE p.id = ANY(@ids)
                  AND (p.goodbye_at > @since
                       OR EXISTS (SELECT 1 FROM agent_commands a
                                  WHERE a.pc_id = p.id AND a.name IN ('reboot', 'shutdown') AND a.created_at > @since AND a.superseded_at IS NULL))
                """,
                new { ids = went.Select(p => p.Id).ToArray(), since = now - window })).ToDictionary(p => p.Id, p => p.Reboot);
            foreach (var pc in went.Where(p => planned.ContainsKey(p.Id)))
            {
                if (planned[pc.Id])
                {
                    _rebooting[pc.Id] = now;
                }

                logger.LogInformation("PC {Name} ({PcId}) went offline after a planned {Stop}", pc.Name, pc.Id, planned[pc.Id] ? "reboot" : "stop");
            }

            went.RemoveAll(p => planned.ContainsKey(p.Id));
        }

        foreach (var pc in late)
        {
            logger.LogWarning("PC {Name} ({PcId}) has not come back {Window} after a planned reboot", pc.Name, pc.Id, window);
        }

        if (went.Count > 0 || late.Count > 0)
        {
            await using var c = await db.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await c.ExecuteAsync(
                """
                INSERT INTO telemetry_events (club_id, pc_id, kind, at, data, received_at)
                SELECT club_id, id, 'pcOffline', @now,
                       jsonb_build_object('lastHeartbeatAt', last_heartbeat_at) || CASE WHEN id = ANY(@late) THEN '{"afterPlannedReboot":true}'::jsonb ELSE '{}'::jsonb END,
                       @now
                FROM pcs WHERE id = ANY(@ids)
                """,
                new { now, ids = went.Concat(late).Select(p => p.Id).ToArray(), late = late.Select(p => p.Id).ToArray() }, tx);
            foreach (var pc in went)
            {
                await Webhooks.EnqueueAsync(c, tx, pc.ClubId, "pcOffline", now, pc.Name, new { pcId = pc.Id });
            }

            foreach (var pc in late)
            {
                await Webhooks.EnqueueAsync(c, tx, pc.ClubId, "pcOffline", now, pc.Name, new { pcId = pc.Id, afterPlannedReboot = true });
            }

            await tx.CommitAsync();
        }

        await automation.IdleAsync(free);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var held = await db.OpenConnectionAsync(stoppingToken);
        await held.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@key)", new { key = LockKey }, cancellationToken: stoppingToken));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "PC status pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
        }
    }
}
