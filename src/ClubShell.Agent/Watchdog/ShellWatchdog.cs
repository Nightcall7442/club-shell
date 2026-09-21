using System.Runtime.Versioning;
using ClubShell.Agent.Server;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Watchdog;

/// <summary>Watchdog lifecycle state (exposed for telemetry and diagnostics).</summary>
public enum ShellState
{
    /// <summary>No Shell tracked and none being launched.</summary>
    Stopped,

    /// <summary>Waiting for an active kiosk session to appear.</summary>
    WaitingForSession,

    /// <summary>Launching the Shell.</summary>
    Launching,

    /// <summary>Shell running and expected to be connected.</summary>
    Running,

    /// <summary>Backing off before the next relaunch.</summary>
    Restarting,

    /// <summary>Crash loop: fallback shell active, relaunches paused until the session recovers.</summary>
    SafeMode,

    /// <summary>Restarts suspended by a profile reset / Shell update.</summary>
    Suspended,
}

/// <summary>
/// Ensures exactly one kiosk Shell runs in the interactive session (ARCHITECTURE.md §5.1, §6.1, §7). Waits for the
/// active kiosk console session, launches the Shell through <see cref="ShellLauncher"/>, watches its process for exit,
/// and relaunches per the <see cref="CrashRecovery"/> decision (immediate, delayed, or safe mode after a crash loop).
/// A Shell that stays alive but never connects to the pipe for over <see cref="HealthTimeout"/> is killed and
/// restarted. Reacts promptly to <see cref="SessionChangeWatcher"/> logon/logoff, honours restart suspension from
/// <see cref="IShellRelauncher"/>, and leaves the Shell running on Agent shutdown unless configured otherwise.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellWatchdog : BackgroundService
{
    /// <summary>How long the Shell may run without a live pipe connection before it is force-restarted.</summary>
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(5);

    private readonly ShellLauncher _launcher;
    private readonly SessionChangeWatcher _sessionWatcher;
    private readonly IShellConnectionState _shell;
    private readonly CrashRecovery _recovery;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<ShellWatchdog> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    private ShellState _status = ShellState.Stopped;
    private DateTimeOffset _startedAt;
    private TimeSpan _pendingDelay = TimeSpan.Zero;
    private bool _safeMode;

    /// <summary>Creates the watchdog.</summary>
    public ShellWatchdog(
        ShellLauncher launcher,
        SessionChangeWatcher sessionWatcher,
        IShellConnectionState shell,
        CrashRecovery recovery,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<ShellWatchdog> logger)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(sessionWatcher);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _launcher = launcher;
        _sessionWatcher = sessionWatcher;
        _shell = shell;
        _recovery = recovery;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<ShellState>? StatusChanged;

    /// <summary>Current watchdog state.</summary>
    public ShellState Status => _status;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _sessionWatcher.SessionChanged += OnSessionChanged;
        try
        {
            _sessionWatcher.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Starting the session-change watcher failed; falling back to polling only");
        }

        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _sessionWatcher.SessionChanged -= OnSessionChanged;
        }

        // ARCHITECTURE.md §7: an Agent restart must not disrupt the player, so the Shell is left running by default.
        if (!_launcher.KeepShellOnAgentExit)
        {
            try
            {
                await _launcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Stopping the Shell during Agent shutdown failed");
            }
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(500, _settings.CurrentValue.Shell.WatchdogIntervalMs));

            if (_launcher.RestartsSuspended)
            {
                SetStatus(ShellState.Suspended);
                await WaitAsync(interval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (_launcher.ActiveSessionId is null)
            {
                SetStatus(ShellState.WaitingForSession);
                await WaitAsync(interval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (!_launcher.IsRunning)
            {
                if (_safeMode)
                {
                    SetStatus(ShellState.SafeMode);
                    await WaitAsync(interval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (_pendingDelay > TimeSpan.Zero)
                {
                    SetStatus(ShellState.Restarting);
                    await Task.Delay(_pendingDelay, stoppingToken).ConfigureAwait(false);
                    _pendingDelay = TimeSpan.Zero;
                }

                SetStatus(ShellState.Launching);
                var process = await _launcher.LaunchAsync(stoppingToken).ConfigureAwait(false);
                if (process is null)
                {
                    SetStatus(ShellState.WaitingForSession);
                    await WaitAsync(interval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _startedAt = _clock.UtcNow;
                SetStatus(ShellState.Running);
            }

            await MonitorAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync(CancellationToken stoppingToken)
    {
        var startedAt = _startedAt;
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var exitTask = _launcher.WaitForExitAsync(monitorCts.Token);
        var healthTask = MonitorHealthAsync(startedAt, monitorCts.Token);
        var completed = await Task.WhenAny(exitTask, healthTask).ConfigureAwait(false);
        await monitorCts.CancelAsync().ConfigureAwait(false);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (_launcher.RestartsSuspended)
        {
            // Intentional stop (profile reset / Shell update); the relaunch is driven by IShellRelauncher.
            return;
        }

        int? exitCode = null;
        if (completed == exitTask)
        {
            try
            {
                exitCode = await exitTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignored: the process handle was released.
            }

            _logger.LogWarning("Shell (pid {Pid}) exited with code {Code}", _launcher.Current?.Pid, exitCode);
        }
        else
        {
            _logger.LogWarning("Shell has not connected to the pipe within {Timeout}; restarting", HealthTimeout);
            await _launcher.StopAsync(stoppingToken).ConfigureAwait(false);
        }

        await HandleExitAsync(exitCode, _clock.UtcNow - startedAt, stoppingToken).ConfigureAwait(false);
    }

    private async Task MonitorHealthAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var lastConnected = startedAt;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HealthPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_shell.IsConnected)
            {
                lastConnected = _clock.UtcNow;
                continue;
            }

            if (_clock.UtcNow - lastConnected > HealthTimeout)
            {
                return;
            }
        }
    }

    private async Task HandleExitAsync(int? exitCode, TimeSpan uptime, CancellationToken cancellationToken)
    {
        var report = _recovery.BuildReport(exitCode, uptime, graceful: false, _startedAt);
        var action = _recovery.Decide(exitCode, uptime, graceful: false);
        await _recovery.ReportAsync(report, action.Kind == RecoveryActionKind.SafeMode, cancellationToken).ConfigureAwait(false);

        switch (action.Kind)
        {
            case RecoveryActionKind.SafeMode:
                _safeMode = true;
                SetStatus(ShellState.SafeMode);
                _ = await _launcher.LaunchExplorerAsync(cancellationToken).ConfigureAwait(false);
                break;
            case RecoveryActionKind.RestartAfter:
                _pendingDelay = action.Delay;
                SetStatus(ShellState.Restarting);
                break;
            default:
                _pendingDelay = TimeSpan.Zero;
                SetStatus(ShellState.Restarting);
                break;
        }
    }

    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionChangeReason.Logon:
            case SessionChangeReason.ConsoleConnect:
            case SessionChangeReason.SessionCreate:
            case SessionChangeReason.Unlock:
                if (_safeMode)
                {
                    _logger.LogInformation("Kiosk session {Session} recovered; leaving safe mode", e.SessionId);
                }

                _safeMode = false;
                _pendingDelay = TimeSpan.Zero;
                _recovery.Reset();
                Wake();
                break;
            case SessionChangeReason.Logoff:
            case SessionChangeReason.ConsoleDisconnect:
            case SessionChangeReason.SessionRemove:
                Wake();
                break;
            default:
                break;
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the loop will pick it up.
        }
    }

    private async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _wake.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void SetStatus(ShellState status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        _logger.LogDebug("Watchdog status: {Status}", status);
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Watchdog StatusChanged listener threw");
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
