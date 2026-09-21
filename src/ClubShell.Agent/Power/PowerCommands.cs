using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Power;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Power;

/// <summary>Delivers a <c>shell.command</c> event to the connected Shell (implemented by the IPC server).</summary>
public interface IPowerNotifier
{
    /// <summary>Publishes <paramref name="command"/>; a disconnected Shell is not an error.</summary>
    ValueTask NotifyAsync(ShellCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Power operations exposed to the server command dispatcher and the IPC handlers (<c>sys.reboot</c>,
/// <c>sys.shutdown</c>, <c>sys.lockScreen</c>): every shutdown / reboot / sleep / logoff first notifies the Shell,
/// then ends an open play session (<see cref="SessionEndReason.Admin"/>) and only then calls
/// <see cref="PowerControl"/>. Session start switches the machine to the high-performance plan and keeps it awake
/// until the session ends.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerCommands : IDisposable
{
    private const uint NoSession = 0xFFFFFFFF;

    private readonly PowerControl _power;
    private readonly ISessionService _sessions;
    private readonly IPowerNotifier _notifier;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<PowerCommands> _logger;
    private readonly object _gate = new();
    private DateTimeOffset? _pendingAt;
    private bool _pendingReboot;

    /// <summary>Creates the command surface and subscribes to session changes for the power profile.</summary>
    public PowerCommands(PowerControl power, ISessionService sessions, IPowerNotifier notifier, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<PowerCommands> logger)
    {
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _power = power;
        _sessions = sessions;
        _notifier = notifier;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _sessions.Changed += OnSessionChanged;
    }

    /// <summary>When the pending shutdown / reboot fires, or <see langword="null"/> when none is pending (a countdown that already elapsed, e.g. aborted with <c>shutdown /a</c>, counts as not pending).</summary>
    public DateTimeOffset? PendingAt
    {
        get
        {
            lock (_gate)
            {
                return _pendingAt is { } at && at > _clock.UtcNow ? at : null;
            }
        }
    }

    /// <summary><see langword="true"/> while a shutdown or reboot countdown is running.</summary>
    public bool IsPending => PendingAt is not null;

    /// <summary><see langword="true"/> when the pending action is a reboot.</summary>
    public bool IsPendingReboot
    {
        get
        {
            lock (_gate)
            {
                return _pendingAt is not null && _pendingReboot;
            }
        }
    }

    /// <summary>Shuts the PC down after <paramref name="delaySec"/> seconds (default <c>power.shutdownGraceSec</c>).</summary>
    public Task<ScheduledResult> ShutdownAsync(int? delaySec, string? reason, bool force, CancellationToken cancellationToken) =>
        InitiateAsync(reboot: false, delaySec, reason, force, cancellationToken);

    /// <summary>Reboots the PC after <paramref name="delaySec"/> seconds (default <c>power.shutdownGraceSec</c>).</summary>
    public Task<ScheduledResult> RebootAsync(int? delaySec, string? reason, bool force, CancellationToken cancellationToken) =>
        InitiateAsync(reboot: true, delaySec, reason, force, cancellationToken);

    /// <summary>Ends an open session and suspends the PC to RAM.</summary>
    public async Task SleepAsync(string? reason, CancellationToken cancellationToken)
    {
        await NotifyAsync(ShellCommand.Of(ShellCommandKind.ShowMessage, new ShowMessageArgs("Sleep", Describe("The PC is going to sleep", reason), NotificationLevel.Warning, 10), Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
        await EndSessionAsync(cancellationToken).ConfigureAwait(false);
        _power.PreventSleep(false);
        _power.Sleep();
        _logger.LogInformation("Sleep requested: {Reason}", reason ?? "-");
    }

    /// <summary>Locks the kiosk: an open session is locked through the session service, otherwise the Shell shows its lock screen.</summary>
    public async Task LockAsync(string? reason, CancellationToken cancellationToken)
    {
        if (_sessions.State.IsOpen())
        {
            _ = await _sessions.LockAsync(reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        await NotifyAsync(ShellCommand.Of(ShellCommandKind.Lock, new LockCommand(reason, null), Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Lock screen requested: {Reason}", reason ?? "-");
    }

    /// <summary>Ends an open session and logs the interactive console session off.</summary>
    public async Task LogoffAsync(string? reason, CancellationToken cancellationToken)
    {
        await EndSessionAsync(cancellationToken).ConfigureAwait(false);
        var sessionId = WtsSessions.GetActiveConsoleSessionId();
        if (sessionId is 0 or NoSession)
        {
            _logger.LogInformation("Logoff requested but no interactive console session is active");
            return;
        }

        _power.Logoff(sessionId);
        _logger.LogInformation("Console session {SessionId} logged off: {Reason}", sessionId, reason ?? "-");
    }

    /// <summary>Aborts a pending shutdown / reboot countdown; <see langword="false"/> when none was pending.</summary>
    public bool Abort()
    {
        bool aborted;
        try
        {
            aborted = _power.Abort();
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "Abort of the pending shutdown failed");
            aborted = false;
        }

        lock (_gate)
        {
            _pendingAt = null;
            _pendingReboot = false;
        }

        if (aborted)
        {
            _ = NotifySafeAsync(ShellCommand.Of(ShellCommandKind.ShowMessage, new ShowMessageArgs("Cancelled", "The scheduled shutdown was cancelled.", NotificationLevel.Info, 5), Guid.NewGuid()));
        }

        return aborted;
    }

    /// <summary>
    /// Applies the session power profile: <paramref name="sessionActive"/> activates the high-performance plan and
    /// keeps system and display awake; <see langword="false"/> releases the keep-awake (the plan is left as is).
    /// </summary>
    public async Task SetSessionPowerProfileAsync(bool sessionActive, CancellationToken cancellationToken)
    {
        _power.PreventSleep(sessionActive);
        if (!sessionActive)
        {
            return;
        }

        try
        {
            var active = await _power.GetActivePowerPlanAsync(cancellationToken).ConfigureAwait(false);
            if (active != PowerControl.HighPerformancePlan && active != PowerControl.UltimatePerformancePlan)
            {
                await _power.SetPowerPlanAsync(PowerControl.HighPerformancePlan, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "High-performance power plan could not be activated");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sessions.Changed -= OnSessionChanged;
        GC.SuppressFinalize(this);
    }

    private static string Describe(string what, string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? what + "." : what + " (" + reason.Trim() + ").";

    private async Task<ScheduledResult> InitiateAsync(bool reboot, int? delaySec, string? reason, bool force, CancellationToken cancellationToken)
    {
        var delay = Math.Max(0, delaySec ?? _settings.CurrentValue.Power.ShutdownGraceSec);
        var action = reboot ? "restart" : "shut down";
        var message = Describe($"The PC will {action} in {delay} seconds", reason);
        var at = _clock.UtcNow + TimeSpan.FromSeconds(delay);

        var notice = reboot
            ? ShellCommand.Of(ShellCommandKind.Reboot, new ShellRebootArgs(delay, message), Guid.NewGuid())
            : ShellCommand.Of(ShellCommandKind.ShowMessage, new ShowMessageArgs("Shutdown", message, NotificationLevel.Warning, Math.Max(delay, 5)), Guid.NewGuid());
        await NotifyAsync(notice, cancellationToken).ConfigureAwait(false);
        await EndSessionAsync(cancellationToken).ConfigureAwait(false);

        if (IsPending)
        {
            // Re-arm with the new delay; a second InitiateSystemShutdownEx would fail with ERROR_SHUTDOWN_IN_PROGRESS.
            _ = _power.Abort();
        }

        if (reboot)
        {
            _power.Reboot(delay, message, force);
        }
        else
        {
            _power.Shutdown(delay, message, force);
        }

        lock (_gate)
        {
            _pendingAt = at;
            _pendingReboot = reboot;
        }

        _logger.LogWarning("{Action} scheduled at {At} (delay {Delay}s, force {Force}): {Reason}", reboot ? "Reboot" : "Shutdown", at, delay, force, reason ?? "-");
        return new ScheduledResult(at);
    }

    private async Task EndSessionAsync(CancellationToken cancellationToken)
    {
        if (!_sessions.State.IsOpen())
        {
            return;
        }

        try
        {
            _ = await _sessions.EndAsync(SessionEndReason.Admin, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Session ended before the power action");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Session could not be ended gracefully before the power action");
        }
    }

    private async ValueTask NotifyAsync(ShellCommand command, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.NotifyAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Shell notification {Command} failed", command.Command);
        }
    }

    private async Task NotifySafeAsync(ShellCommand command) =>
        await NotifyAsync(command, CancellationToken.None).ConfigureAwait(false);

    private void OnSessionChanged(object? sender, SessionEvent e)
    {
        if (e.Type is not (SessionEventType.Started or SessionEventType.Ended))
        {
            return;
        }

        _ = ApplyProfileSafeAsync(e.Type == SessionEventType.Started);
    }

    private async Task ApplyProfileSafeAsync(bool sessionActive)
    {
        try
        {
            await SetSessionPowerProfileAsync(sessionActive, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Session power profile (active={Active}) could not be applied", sessionActive);
        }
    }
}
