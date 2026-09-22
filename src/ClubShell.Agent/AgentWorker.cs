using System.Runtime.Versioning;
using ClubShell.Agent.AntiCheat;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Server;
using ClubShell.Agent.Session;
using ClubShell.Agent.Storage;
using ClubShell.Agent.Users;
using ClubShell.Agent.Watchdog;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClubShell.Agent;

/// <summary>Health snapshot of the Agent, for <c>--check-config</c> style diagnostics and telemetry.</summary>
/// <param name="Ready">The startup sequence completed.</param>
/// <param name="PcId">Assigned PC id, when registered.</param>
/// <param name="Connectivity">Server connectivity.</param>
/// <param name="PipeListening">The IPC pipe server is accepting connections.</param>
/// <param name="ShellConnected">The Shell is connected over the pipe.</param>
/// <param name="ShellState">Watchdog state.</param>
/// <param name="KioskProvisioned">The kiosk account is provisioned.</param>
/// <param name="PolicyApplied">A policy has been applied.</param>
/// <param name="OfflineQueue">Events waiting in the offline outbox.</param>
public sealed record AgentStatus(
    bool Ready,
    Guid? PcId,
    ConnectivityState Connectivity,
    bool PipeListening,
    bool ShellConnected,
    ShellState ShellState,
    bool KioskProvisioned,
    bool PolicyApplied,
    int OfflineQueue);

/// <summary>
/// Orchestrates the Agent startup sequence (ARCHITECTURE.md §6.1) and runs the periodic self-health / offline-flush
/// loop. The individual long-running loops (pipe server, server connection, heartbeat, telemetry, power scheduler,
/// storage mount, anti-cheat, updater, shell watchdog) are their own hosted services; this worker is registered last
/// so every dependency is constructed, then it performs the steps that no hosted loop owns — identity, a kiosk
/// provisioning retry (the first attempt is the <c>TempUserProvisioner</c> hosted start), the initial policy apply,
/// session restore, and the online maintenance loop. Each step is bounded by a
/// timeout, retried where it makes sense, and logged; the subsystems degrade gracefully when a step is still pending.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentWorker : BackgroundService
{
    private static readonly TimeSpan ProvisionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PolicyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SessionRestoreTimeout = TimeSpan.FromSeconds(30);

    // Logging the kiosk user off, deleting the profile, relaunching the Shell and waiting for the profile to come
    // back so the preserved anti-cheat directories can be restored — around a minute on a healthy PC.
    private static readonly TimeSpan DirtyResetTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StepRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(15);
    private const int MaxStepAttempts = 3;

    private readonly Hwid _hwid;
    private readonly TempUserProvisioner _provisioner;
    private readonly IProfileResetTrigger _profileReset;
    private readonly PolicyStore _policyStore;
    private readonly IPolicyEnforcer _policy;
    private readonly SessionManager _sessions;
    private readonly OfflineSessionStore _store;
    private readonly IServerClient _server;
    private readonly ServerConnection _connection;
    private readonly Ipc.PipeServer _pipe;
    private readonly ShellWatchdog _watchdog;
    private readonly GamesShareMounter _share;
    private readonly IAgentEventSink _events;
    private readonly TelemetryBus _telemetry;
    private readonly ShellUserContext _users;
    private readonly IIpcEventPublisher _publisher;
    private readonly IClock _clock;
    private readonly SettingsLoader _settingsLoader;
    private readonly ILogger<AgentWorker> _logger;

    private volatile bool _ready;
    private Guid _persistedPcId;

    /// <summary>Creates the worker.</summary>
    public AgentWorker(
        Hwid hwid,
        TempUserProvisioner provisioner,
        IProfileResetTrigger profileReset,
        PolicyStore policyStore,
        IPolicyEnforcer policy,
        SessionManager sessions,
        OfflineSessionStore store,
        IServerClient server,
        ServerConnection connection,
        Ipc.PipeServer pipe,
        ShellWatchdog watchdog,
        GamesShareMounter share,
        IAgentEventSink events,
        TelemetryBus telemetry,
        ShellUserContext users,
        IIpcEventPublisher publisher,
        IClock clock,
        SettingsLoader settingsLoader,
        ILogger<AgentWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(hwid);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(profileReset);
        ArgumentNullException.ThrowIfNull(policyStore);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(watchdog);
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settingsLoader);
        ArgumentNullException.ThrowIfNull(logger);

        _hwid = hwid;
        _provisioner = provisioner;
        _profileReset = profileReset;
        _policyStore = policyStore;
        _policy = policy;
        _sessions = sessions;
        _store = store;
        _server = server;
        _connection = connection;
        _pipe = pipe;
        _watchdog = watchdog;
        _share = share;
        _events = events;
        _telemetry = telemetry;
        _users = users;
        _publisher = publisher;
        _clock = clock;
        _settingsLoader = settingsLoader;
        _logger = logger;
    }

    /// <summary><see langword="true"/> once the startup sequence completed.</summary>
    public bool IsReady => _ready;

    /// <summary>Current health snapshot (never throws).</summary>
    public AgentStatus Snapshot() => new(
        _ready,
        _connection.PcId,
        _connection.Connectivity,
        // The pipe server is a hosted BackgroundService; if its accept loop faulted the host would be stopping, so
        // while this worker runs the pipe is listening. A named pipe means it is configured.
        _pipe.PipeName is { Length: > 0 },
        _pipe.IsShellConnected,
        _watchdog.Status,
        _provisioner.IsProvisioned,
        _policy.Current is not null,
        SafeQueueDepth());

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        AgentSettings settings = _settingsLoader.Current;
        _logger.LogInformation(
            "ClubShell Agent {Version} starting (pc {PcName}, zone {Zone}, offline {Offline})",
            ClubShellVersion.Current,
            settings.PcName ?? Environment.MachineName,
            settings.Zone ?? "-",
            settings.Offline.Enabled);

        try
        {
            await StartupSequenceAsync(stoppingToken).ConfigureAwait(false);
            _ready = true;
            _logger.LogInformation("Agent ready");
            await MaintenanceLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Agent worker stopped unexpectedly");
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent stopping");
        try
        {
            if (_connection.IsOnline)
            {
                await FlushOutboxAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Final offline flush on shutdown failed");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- startup ---------------------------------------------------------------------------

    private async Task StartupSequenceAsync(CancellationToken cancellationToken)
    {
        // 1. Identity / hardware id.
        try
        {
            string hwid = await _hwid.GetAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Hardware id {Hwid}", hwid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Hardware id could not be computed; a fallback id will be used");
        }

        // 2. Kiosk user provisioning (§6.1 step 9) already ran as the first hosted service (TempUserProvisioner.StartAsync,
        //    before the pipe server); retry here only when that attempt failed.
        if (!_provisioner.IsProvisioned)
        {
            await RunStepAsync(
                "kiosk-provision",
                async ct => await _provisioner.ProvisionAsync(ct).ConfigureAwait(false),
                ProvisionTimeout,
                critical: false,
                cancellationToken).ConfigureAwait(false);
        }

        // 3. Policy: cache/file first (§6.1 step 7); the server copy is picked up later by the heartbeat.
        await RunStepAsync("policy-apply", ApplyInitialPolicyAsync, PolicyTimeout, critical: false, cancellationToken).ConfigureAwait(false);
        _policyStore.Watch();

        // 4/5. GamesShareMounter and ServerConnection are hosted services already running; log their state only.
        _logger.LogInformation("Games share: {State}", _share.IsEnabled ? (_share.IsMounted ? "mounted" : "mounting") : "disabled");

        // 6. Restore the persisted session (§6.1 step 6 equivalent; timer resumes from persisted endsAt).
        await RunStepAsync(
            "session-restore",
            ct => _sessions.RestoreAsync(ct),
            SessionRestoreTimeout,
            critical: false,
            cancellationToken).ConfigureAwait(false);

        // 7. Profile hygiene (§6.1 step 9): a session that never closed cleanly leaves the previous player's profile
        //    on disk. Runs after the restore so a session that is still legitimately open is left alone.
        await RunStepAsync(
            "profile-dirty-reset",
            _profileReset.ResetIfDirtyAsync,
            DirtyResetTimeout,
            critical: false,
            cancellationToken).ConfigureAwait(false);

        // 8. Prepare the offline store so the maintenance loop can flush.
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Offline store initialization failed");
        }
    }

    private async Task ApplyInitialPolicyAsync(CancellationToken cancellationToken)
    {
        var (policy, source) = await _policyStore.LoadAsync(force: false, cancellationToken).ConfigureAwait(false);
        var result = await _policy.ApplyAsync(policy, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Policy v{Version} from {Source} applied={Applied} changed=[{Changed}] errors={Errors}",
            policy.Version,
            source,
            result.Applied,
            string.Join(", ", result.Changed),
            result.Errors.Count);
    }

    private async Task RunStepAsync(string name, Func<CancellationToken, Task> step, TimeSpan timeout, bool critical, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxStepAttempts; attempt++)
        {
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timed.CancelAfter(timeout);
            long started = _clock.GetTimestamp();
            try
            {
                await step(timed.Token).ConfigureAwait(false);
                _logger.LogInformation("Startup step {Step} completed in {ElapsedMs} ms", name, (int)_clock.GetElapsedTime(started).TotalMilliseconds);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Startup step {Step} timed out after {Timeout}s (attempt {Attempt}/{Max})", name, timeout.TotalSeconds, attempt, MaxStepAttempts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup step {Step} failed (attempt {Attempt}/{Max})", name, attempt, MaxStepAttempts);
            }

            if (attempt < MaxStepAttempts)
            {
                await Task.Delay(StepRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            else if (critical)
            {
                throw new InvalidOperationException($"Startup step '{name}' did not complete after {MaxStepAttempts} attempts.");
            }
            else
            {
                _logger.LogWarning("Startup step {Step} did not complete; the subsystem will retry on its own schedule", name);
            }
        }
    }

    // ---- maintenance -----------------------------------------------------------------------

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MaintenanceInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ExpireUserIfNeededAsync(cancellationToken).ConfigureAwait(false);
                await PersistIdentityIfNeededAsync(cancellationToken).ConfigureAwait(false);
                // A reset owed but not paid at startup — the deletion failed, or the debt was incurred since — would
                // otherwise wait for the next Agent restart. Throttles itself; a no-op when nothing is owed.
                await _profileReset.ResetIfDirtyAsync(cancellationToken).ConfigureAwait(false);
                if (_connection.IsOnline)
                {
                    await FlushOutboxAsync(cancellationToken).ConfigureAwait(false);
                    await _sessions.SyncWithServerAsync(cancellationToken).ConfigureAwait(false);
                }

                ReportHealth();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent maintenance tick failed");
            }
        }
    }

    /// <summary>
    /// IPC_PROTOCOL.md §8 <c>auth.expired{tokenExpired}</c>: the Agent-held user access token (or the offline grace
    /// window) has expired and cannot be refreshed, so the user context is dropped and the Shell returns to login. An
    /// open session is left running: <c>auth.login</c> re-binds it to the same user.
    /// </summary>
    private async Task ExpireUserIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_users.User is not { } user || _users.ExpiresAt is not { } expiresAt || expiresAt > _clock.UtcNow || !_users.Clear())
        {
            return;
        }

        _logger.LogInformation("Access token of user {UserId} expired at {ExpiresAt}; user context dropped", user.Id, expiresAt);
        await _publisher.PublishAsync(IpcMessages.Events.AuthExpired, new AuthExpired(AuthExpiredReason.TokenExpired), cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistIdentityIfNeededAsync(CancellationToken cancellationToken)
    {
        // ServerConnection stores the assigned pcId in its own identity file and the agent tokens, but several
        // subsystems (PolicyStore server load, config refresh) read it from agent.json via AgentSettings.PcId. Bridge
        // it once so a freshly registered PC starts pulling server policy/config instead of only cached copies.
        if (_connection.PcId is not { } pcId || pcId == _persistedPcId || pcId == _settingsLoader.Current.PcId)
        {
            return;
        }

        try
        {
            await _settingsLoader.SetPcIdentityAsync(pcId, _settingsLoader.Current.PcName, _settingsLoader.Current.Zone, cancellationToken).ConfigureAwait(false);
            _persistedPcId = pcId;
            _logger.LogInformation("Persisted PC identity {PcId} to agent.json", pcId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Persisting PC identity to agent.json failed");
        }
    }

    private async Task FlushOutboxAsync(CancellationToken cancellationToken)
    {
        OfflineFlushResult result = await _store.FlushAsync(_server, cancellationToken).ConfigureAwait(false);
        if (result.Sent == 0 && result.DeadLettered == 0)
        {
            return;
        }

        DateTimeOffset now = _clock.UtcNow;
        var payload = new OfflineQueueFlushedEvent(result.Sent, result.DeadLettered, result.OfflineFrom ?? now, result.OfflineTo ?? now);
        try
        {
            await _events.PublishAsync(AgentEvent.Of(AgentEventType.OfflineQueueFlushed, now, payload), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Publishing offlineQueueFlushed failed");
        }
    }

    private void ReportHealth()
    {
        AgentStatus status = Snapshot();
        bool degraded = status.Connectivity == ConnectivityState.Offline
            || status.ShellState is ShellState.SafeMode or ShellState.Restarting
            || status.OfflineQueue > 0;

        if (degraded)
        {
            _logger.LogWarning(
                "Health: connectivity={Connectivity} shell={ShellState} shellConnected={ShellConnected} kiosk={Kiosk} policy={Policy} outbox={Outbox}",
                status.Connectivity,
                status.ShellState,
                status.ShellConnected,
                status.KioskProvisioned,
                status.PolicyApplied,
                status.OfflineQueue);
            _ = _telemetry.Publish("agentHealth", status);
        }
        else
        {
            _logger.LogDebug("Health OK: shell={ShellState} shellConnected={ShellConnected}", status.ShellState, status.ShellConnected);
        }
    }

    private int SafeQueueDepth()
    {
        try
        {
            return _store.GetQueueStatsAsync(CancellationToken.None).GetAwaiter().GetResult().Pending;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Queue depth read failed");
            return 0;
        }
    }
}
