using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Agent.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Sessions;

namespace ClubShell.Agent.Watchdog;

/// <summary>
/// Ends an administrator's maintenance window as soon as the PC is back in a player's hands (SHELL_CHANGES 49): a
/// player's session starts, or the Shell connects again. An administrator who exits with the PIN and walks away without
/// signing out of Windows no longer leaves the next player up to half an hour with USB storage and without the deny-list.
/// When a window opens, the administrator's desktop is told until when it lasts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdminMaintenanceCloser : IHostedService
{
    private static readonly TimeSpan NoticeTimeout = TimeSpan.FromSeconds(30);

    private readonly AdminMaintenance _maintenance;
    private readonly ISessionService _sessions;
    private readonly IpcEventBridge _shell;
    private readonly ILogger<AdminMaintenanceCloser> _logger;

    /// <summary>Creates the closer; <see cref="StartAsync"/> subscribes it.</summary>
    public AdminMaintenanceCloser(AdminMaintenance maintenance, ISessionService sessions, IpcEventBridge shell, ILogger<AdminMaintenanceCloser> logger)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(logger);
        _maintenance = maintenance;
        _sessions = sessions;
        _shell = shell;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.Changed += OnSessionChanged;
        _shell.ConnectedChanged += OnShellConnectedChanged;
        _maintenance.Changed += OnMaintenanceChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.Changed -= OnSessionChanged;
        _shell.ConnectedChanged -= OnShellConnectedChanged;
        _maintenance.Changed -= OnMaintenanceChanged;
        return Task.CompletedTask;
    }

    private void OnSessionChanged(object? sender, SessionEvent e)
    {
        if (e.Type == SessionEventType.Started)
        {
            _maintenance.End("a player's session started");
        }
    }

    private void OnShellConnectedChanged(object? sender, bool connected)
    {
        if (connected)
        {
            _maintenance.End("the Shell connected again");
        }
    }

    private void OnMaintenanceChanged(object? sender, bool active)
    {
        if (!active || _maintenance.Until is not { } until || _maintenance.Session is not { } session)
        {
            return;
        }

        string time = TimeZoneInfo.ConvertTime(until, TimeZoneInfo.Local).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string text =
            $"Обслуживание до {time}: оболочка не перезапускается, флешки и служебные программы разрешены.\n" +
            "Оболочка вернётся сама в это время, при начале сеанса игрока или после блокировки ПК из кассы.\n\n" +
            $"Maintenance until {time}. The Shell comes back then, when a player's session starts, or when the cash desk locks this PC.";
        try
        {
            _ = WtsSessions.SendMessage(session, "ClubShell", text, NoticeTimeout, wait: false);
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "Maintenance notice not shown in session {SessionId}", session);
        }
    }
}
