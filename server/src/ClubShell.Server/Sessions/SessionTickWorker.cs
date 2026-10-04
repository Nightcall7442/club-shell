using System.Collections.Concurrent;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Sessions;
using ClubShell.Server.Agents;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions.Billing;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Sessions;

/// <summary>
/// The session tick (DESIGN §5.10, §8), every <see cref="SessionsOptions.TickMs"/> under <c>pg_advisory_lock(CSSess)</c>,
/// for PCs with a fresh heartbeat and an empty agent outbox: a prepaid session becomes <c>ending</c> at <c>ends_at</c> and
/// is ended with <c>timeUp</c> after <see cref="SessionsOptions.GraceSec"/> (grace is free); a postpaid one ends at the
/// minute boundary where the next minute exceeds balance + its limit (<see cref="SessionService.PostpaidLimit"/>, D-10). Any
/// other PC is left to its agent (it is the authority then, §5.11) until <see cref="SessionsOptions.MaxOfflineMinutes"/>
/// of silence. A server-side end queues <c>endSession</c> for the agent (§6.4). Every <see cref="SessionsOptions.ResyncSec"/> an open session is pushed to its PC again. Tests call
/// <see cref="RunOnceAsync"/> with the fixture's clock; the hosted loop runs only with <c>Workers:Enabled</c>.
/// </summary>
public sealed class SessionTickWorker(
    NpgsqlDataSource db, SessionService sessions, AgentSocketHub hub, SessionsOptions options, AgentOptions agents, TimeProvider clock,
    ILogger<SessionTickWorker> logger) : BackgroundService
{
    /// <summary>Session id → last resync push.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _resynced = new();

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var effects = new SessionEffects();
        var (c, tx) = await SessionService.BeginAsync(db);
        await using (c)
        await using (tx)
        {
            var now = clock.GetUtcNow();

            // The tick acts only for a PC whose fresh heartbeat reports an empty outbox: a PC back from offline first
            // sends the events it queued (pauses, extensions, its own end), and settling before them would bill the
            // offline stretch wrong (§5.10). Others are left to their agent, except a PC silent for MaxOfflineMinutes.
            // Filtering in SQL keeps sessions of offline PCs from filling the batch.
            // ponytail: 100 per tick and all open postpaid re-read each second; page by ends_at/id if a club outgrows it.
            var open = (await c.QueryAsync<TickRow>(
                """
                SELECT s.*, p.last_heartbeat_at, w.main_balance, t.settled, u.role, u.transient, cl.time_zone, cl.settings::text AS club_settings
                FROM sessions s JOIN pcs p ON p.id = s.pc_id JOIN wallets w ON w.user_id = s.user_id
                JOIN users u ON u.id = s.user_id JOIN clubs cl ON cl.id = s.club_id
                CROSS JOIN LATERAL (SELECT coalesce(p.last_heartbeat_at > @fresh AND coalesce((p.last_heartbeat->>'offlineQueue')::int, 0) = 0, false) AS settled) t
                WHERE s.state <> 'ended'
                  AND ((t.settled AND s.state IN ('active', 'locked', 'ending') AND (NOT s.is_prepaid OR s.ends_at <= @now))
                       OR greatest(p.last_heartbeat_at, s.last_transition_at) <= @silent)
                ORDER BY s.ends_at NULLS LAST
                LIMIT 100
                FOR UPDATE OF s SKIP LOCKED
                """,
                new { now, fresh = now - TimeSpan.FromSeconds(agents.OfflineAfterSec), silent = now - TimeSpan.FromMinutes(options.MaxOfflineMinutes) },
                tx)).ToList();

            foreach (var s in open)
            {
                if (!s.Settled)
                {
                    var lastContact = Max(s.LastHeartbeatAt ?? s.LastTransitionAt, s.LastTransitionAt);
                    if (now - lastContact >= TimeSpan.FromMinutes(options.MaxOfflineMinutes) && !hub.IsConnected(s.PcId))
                    {
                        // Ended at the last sign of life: a PC switched off must not run up a postpaid bill. Marked, so
                        // that the agent's own later end reopens it and bills the offline play (§5.12).
                        await c.ExecuteAsync(
                            """
                            INSERT INTO session_events (session_id, club_id, source, type, at, received_at, data, applied)
                            VALUES (@Id, @ClubId, 'server', 'offlineTimeout', @lastContact, @now, jsonb_build_object('running', @Running), true)
                            """,
                            new { s.Id, s.ClubId, lastContact, now, s.Running }, tx);
                        await EndAsync(c, tx, s, lastContact, effects, signOut: false);
                    }

                    continue;
                }

                if (s.IsPrepaid && s.EndsAt is { } endsAt && endsAt <= now)
                {
                    // The club extends from the balance instead of ending (limits.autoExtendMinutes), while the grace lasts.
                    if (now < endsAt.AddSeconds(options.GraceSec) && await AutoExtendAsync(c, tx, s, effects))
                    {
                        continue;
                    }

                    if (now >= endsAt.AddSeconds(options.GraceSec))
                    {
                        if (!await SignOutLockAsync(c, tx, s))
                        {
                            continue;
                        }

                        await EndAsync(c, tx, s, now, effects, signOut: true);
                    }
                    else if (s.State != "ending")
                    {
                        s.State = "ending";
                        await SessionService.SaveAsync(c, tx, s, now);
                        effects.Sessions.Add(s.ToWire(now));
                    }
                }
                else if (!s.IsPrepaid && sessions.PostpaidLimit(s.Role, ClubPricing.Parse(s.TimeZone, s.ClubSettings)) is { } limit)
                {
                    // D-10: stop at the minute boundary where the next second would start a minute beyond balance + limit;
                    // a late tick still ends there, so only fully played minutes are charged and the limit holds.
                    var used = s.Used(now);
                    if (Pricing.Frozen(s.PricePerHourSnapshot, used + 1, s.DayPct, s.DiscountPct) > s.MainBalance + limit
                        && await SignOutLockAsync(c, tx, s))
                    {
                        await EndAsync(c, tx, s, Max(now.AddSeconds((used / 60 * 60) - used), s.LastTransitionAt), effects, signOut: true);
                    }
                }
            }

            await tx.CommitAsync(cancellationToken);
        }

        await sessions.PublishAsync(effects);
        await ResyncAsync(effects);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Held for the worker's life on its own connection: a second instance waits here (DESIGN §8).
        await using var held = await db.OpenConnectionAsync(stoppingToken);
        await held.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLocks.Sessions }, cancellationToken: stoppingToken));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Session tick failed");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(options.TickMs), clock, stoppingToken);
        }
    }

    /// <summary>
    /// Settles with <c>timeUp</c> and queues <c>endSession</c> (§5.7 step 7) — without the <c>sessionUpdated</c> push: the
    /// agent would take a pushed end first and close it as <c>admin</c>; the command carries the reason and its
    /// <c>/end</c> reconciles through <c>409 details.session</c>. With <paramref name="signOut"/> (the time is up or the
    /// postpaid limit reached, not a PC gone silent) a transient guest is also signed out of the PC (D-28): a throwaway
    /// account has nothing more to play for there. A member whose prepaid time ran out stays signed in.
    /// </summary>
    private async Task EndAsync(NpgsqlConnection c, NpgsqlTransaction tx, TickRow s, DateTimeOffset end, SessionEffects effects, bool signOut)
    {
        await sessions.SettleAsync(c, tx, s, end, SessionEndReason.TimeUp, effects);
        effects.Sessions.RemoveAll(pushed => pushed.Id == s.Id);
        effects.Commands.Add((s.ClubId, s.PcId, NewCommand.EndSession(new EndSessionCommand(s.Id, SessionEndReason.TimeUp))));
        if (signOut && s.Transient)
        {
            await SessionService.SignOutAsync(c, tx, s.UserId, s.PcId, effects);
        }
    }

    /// <summary>
    /// A transient guest's end signs the guest out, so it is serialized with the PC's sign-ins like a desk end (D-27): the
    /// PC's lock, taken without waiting (the tick already holds the session row; a desk end takes the PC lock first). False
    /// while a sign-in, desk open or desk end of that PC is in progress: the session is left to the next tick, a second on.
    /// A member's end signs nobody out and needs no lock.
    /// </summary>
    private static async Task<bool> SignOutLockAsync(NpgsqlConnection c, NpgsqlTransaction tx, TickRow s) =>
        !s.Transient || await AdvisoryLocks.TryPcAsync(c, tx, s.PcId);

    /// <summary>Every <c>ResyncSec</c> each open session of a connected PC is pushed again (the agent resyncs its timer).</summary>
    private async Task ResyncAsync(SessionEffects pushed)
    {
        var now = clock.GetUtcNow();
        foreach (var session in pushed.Sessions)
        {
            _resynced[session.Id] = now;
        }

        List<SessionRow> open;
        await using (var c = await db.OpenConnectionAsync())
        {
            open = (await c.QueryAsync<SessionRow>($"SELECT {SessionRow.Columns} FROM sessions WHERE state <> 'ended'")).ToList();
        }

        foreach (var s in open.Where(s => hub.IsConnected(s.PcId)))
        {
            if (!_resynced.TryGetValue(s.Id, out var last) || now - last >= TimeSpan.FromSeconds(options.ResyncSec))
            {
                _resynced[s.Id] = now;
                await sessions.PublishAsync(new SessionEffects { Sessions = { s.ToWire(now) } });
            }
        }

        foreach (var gone in _resynced.Keys.Except(open.Select(s => s.Id)).ToList())
        {
            _resynced.TryRemove(gone, out _);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>
    /// Extends a prepaid session that ran out by the club's <c>limits.autoExtendMinutes</c>, through the same
    /// <see cref="SessionService.ExtendAsync"/> as the cashier (rules, price, charge, <c>extendSession</c> to the agent).
    /// A refusal — balance short, tariff window, curfew, maintenance, the tariff's maximum — is rolled back to a savepoint
    /// and the session ends as before.
    /// </summary>
    private async Task<bool> AutoExtendAsync(NpgsqlConnection c, NpgsqlTransaction tx, TickRow s, SessionEffects effects)
    {
        var minutes = ClubPricing.Parse(s.TimeZone, s.ClubSettings).AutoExtendMinutes;
        if (minutes <= 0)
        {
            return false;
        }

        await tx.SaveAsync("auto_extend");
        try
        {
            var (_, charged) = await sessions.ExtendAsync(c, tx, s.Id, s.UserId, s.PcId, minutes, null, effects, notifyAgent: true);
            await tx.ReleaseAsync("auto_extend");
            logger.LogInformation("Session {SessionId} auto-extended by {Minutes} min for {Charged}", s.Id, minutes, charged);
            return true;
        }
        catch (ApiException)
        {
            await tx.RollbackAsync("auto_extend");
            return false;
        }
    }

    private sealed class TickRow : SessionRow
    {
        public DateTimeOffset? LastHeartbeatAt { get; init; }
        public long MainBalance { get; init; }

        /// <summary>The player's role and the club's zone and settings: a guest's postpaid limit is the club's (<c>limits.guestDebtLimit</c>).</summary>
        public string Role { get; init; } = "";

        /// <summary>A temporary guest account: signed out when its session ends here (D-28).</summary>
        public bool Transient { get; init; }

        public string TimeZone { get; init; } = "Asia/Tashkent";
        public string? ClubSettings { get; init; }

        /// <summary>Fresh heartbeat with an empty outbox: the server's clock decides (§5.10).</summary>
        public bool Settled { get; init; }
    }
}
