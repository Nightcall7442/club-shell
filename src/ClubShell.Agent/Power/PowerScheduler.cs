using System.Net.Sockets;
using System.Runtime.Versioning;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Power;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Power;

/// <summary>
/// Interactive-session idle state as seen by the Agent. The Session module declares its own idle signal
/// (<c>ClubShell.Agent.Session.IIdleSignal</c>) with the same shape; the DI layer registers one implementation
/// for both so this module has no compile-time dependency on the Session module.
/// </summary>
public interface IIdleMonitor
{
    /// <summary><see langword="true"/> while no input has been seen for longer than the configured threshold.</summary>
    bool IsIdle { get; }

    /// <summary>Time since the last input.</summary>
    TimeSpan Idle { get; }

    /// <summary>Raised when <see cref="IsIdle"/> flips.</summary>
    event EventHandler<bool>? IdleChanged;
}

/// <summary>
/// Implements <c>PowerPolicy</c>: a daily scheduled shutdown at <c>power.scheduledShutdown</c> (deferred while a
/// session is open and fired as soon as it ends, within <see cref="ScheduledShutdownWindow"/>; re-armed the next
/// day), an idle shutdown after <c>power.idleShutdownMin</c> without a session, and the Wake-on-LAN relay for the
/// server <c>wake</c> command.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerScheduler : BackgroundService
{
    /// <summary>How long after <c>scheduledShutdown</c> a deferred shutdown may still fire.</summary>
    public static TimeSpan ScheduledShutdownWindow { get; } = TimeSpan.FromHours(3);

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly PowerCommands _commands;
    private readonly ISessionService _sessions;
    private readonly IIdleMonitor _idle;
    private readonly IPolicyEnforcer _policies;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<PowerScheduler> _logger;
    private DateOnly? _lastScheduledDate;
    private bool _deferralLogged;

    /// <summary>Creates the scheduler.</summary>
    public PowerScheduler(PowerCommands commands, ISessionService sessions, IIdleMonitor idle, IPolicyEnforcer policies, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<PowerScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(idle);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _commands = commands;
        _sessions = sessions;
        _idle = idle;
        _policies = policies;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Local date on which the scheduled shutdown last fired.</summary>
    public DateOnly? LastScheduledShutdownDate => _lastScheduledDate;

    /// <summary>Sends a Wake-on-LAN magic packet for <paramref name="targetMac"/> (server <c>wake</c> command); <see langword="false"/> when disabled or the send failed.</summary>
    public async Task<bool> WakeAsync(string targetMac, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMac);
        if (!_settings.CurrentValue.Power.WolEnabled)
        {
            _logger.LogWarning("Wake-on-LAN relay for {Mac} refused: power.wolEnabled is false", targetMac);
            return false;
        }

        try
        {
            await WakeOnLan.SendAsync(targetMac, cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Wake-on-LAN packet sent to {Mac}", targetMac);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or SocketException)
        {
            _logger.LogWarning(ex, "Wake-on-LAN packet to {Mac} failed", targetMac);
            return false;
        }
    }

    /// <summary>Evaluates the power policy once (called every 30 s by the service loop; public for tests and diagnostics).</summary>
    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var policy = _policies.Current?.Power;
        if (policy is null || _commands.IsPending)
        {
            return;
        }

        var sessionOpen = _sessions.State.IsOpen();
        var localNow = _clock.LocalNow.DateTime;

        if (policy.ScheduledShutdown is { } scheduled)
        {
            var today = DateOnly.FromDateTime(localNow);
            var time = TimeOnly.FromDateTime(localNow);
            var due = time >= scheduled && time - scheduled < ScheduledShutdownWindow && _lastScheduledDate != today;
            if (due)
            {
                if (sessionOpen)
                {
                    if (!_deferralLogged)
                    {
                        _deferralLogged = true;
                        _logger.LogInformation("Scheduled shutdown at {Time} deferred: a session is open", scheduled);
                    }

                    return;
                }

                _lastScheduledDate = today;
                _deferralLogged = false;
                _ = await _commands.ShutdownAsync(null, "Scheduled shutdown", force: true, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (policy.IdleShutdownMin is int minutes && minutes > 0 && !sessionOpen && _idle.Idle >= TimeSpan.FromMinutes(minutes))
        {
            _logger.LogInformation("PC idle for {Idle} without a session; idle shutdown after {Minutes} min", _idle.Idle, minutes);
            _ = await _commands.ShutdownAsync(null, $"Idle for {minutes} minutes", force: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _clock.Provider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await EvaluateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Power policy evaluation failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }
}
