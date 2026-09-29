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
/// issue <c>unstable</c> counts them — and the <c>pcOffline</c> event for the webhooks, in one transaction. A PC that turns
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

    /// <summary>PC → when it was first seen free in its current stretch.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _freeSince = new();

    public async Task RunOnceAsync()
    {
        var now = clock.GetUtcNow();
        var went = new List<PcRow>();
        var free = new List<(Guid ClubId, Guid PcId, DateTimeOffset FreeSince)>();
        foreach (var pc in await pcs.ListAsync())
        {
            var status = pc.Status(hub.IsConnected(pc.Id), now, TimeSpan.FromSeconds(agents.OfflineAfterSec));
            if (_last.TryGetValue(pc.Id, out var before) && before != status && status == PcStatus.Offline)
            {
                went.Add(pc);
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
            await using var tx = await c.BeginTransactionAsync();
            await c.ExecuteAsync(
                """
                INSERT INTO telemetry_events (club_id, pc_id, kind, at, data, received_at)
                SELECT club_id, id, 'pcOffline', @now, jsonb_build_object('lastHeartbeatAt', last_heartbeat_at), @now FROM pcs WHERE id = ANY(@ids)
                """,
                new { now, ids = went.Select(p => p.Id).ToArray() }, tx);
            foreach (var pc in went)
            {
                await Webhooks.EnqueueAsync(c, tx, pc.ClubId, "pcOffline", now, pc.Name, new { pcId = pc.Id });
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
