using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using ClubShell.Agent.Server;
using ClubShell.Agent.Watchdog;
using ClubShell.Contracts.Commands;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Power;
using ClubShell.Windows.Registry;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Users;

/// <summary>
/// The kiosk account did not come back (SHELL_CHANGES 77): a session ended, the profile was reset, and the PC sits on the
/// Windows sign-in screen, where nobody can type the kiosk's random password and the cash desk sees nothing wrong. When
/// auto-logon is the kiosk's and nobody else is working on the PC (console or Remote Desktop), the Agent reboots the PC (auto-logon always
/// runs at boot) and reports <see cref="TelemetryEventKinds.KioskLogonLost"/>. Never in the first
/// <see cref="MinUptime"/> after the Agent starts (a slow boot is not a lost logon), never during an administrator's
/// maintenance window, and at most once per <see cref="MinInterval"/> (remembered across the reboot), so a PC whose
/// auto-logon cannot work stays on the sign-in screen instead of rebooting in a loop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KioskLogonGuard
{
    /// <summary>The Agent does not reboot sooner than this after it started.</summary>
    public static readonly TimeSpan MinUptime = TimeSpan.FromMinutes(5);

    /// <summary>Shortest time between two such reboots.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);

    private const string MarkerFileName = "kiosk-logon-reboot.txt";
    private const int RebootDelaySec = 10;

    private readonly TempUserProvisioner _provisioner;
    private readonly AdminMaintenance _maintenance;
    private readonly PowerControl _power;
    private readonly TelemetryBus _telemetry;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<KioskLogonGuard> _logger;
    private readonly DateTimeOffset _startedAt;
    private readonly object _gate = new();

    /// <summary>Creates the guard.</summary>
    public KioskLogonGuard(
        TempUserProvisioner provisioner,
        AdminMaintenance maintenance,
        PowerControl power,
        TelemetryBus telemetry,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<KioskLogonGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _provisioner = provisioner;
        _maintenance = maintenance;
        _power = power;
        _telemetry = telemetry;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _startedAt = clock.UtcNow;
    }

    /// <summary>
    /// Reboots the PC when the kiosk is not logged on and nothing above says otherwise; <see langword="true"/> when the
    /// reboot was started. <paramref name="reason"/> goes into the log and the telemetry event.
    /// </summary>
    public bool Recover(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            string kiosk = _provisioner.UserName;
            if (WtsSessions.FindByUser(kiosk) is not null || _maintenance.IsActive)
            {
                return false;
            }

            AutoLogonState autoLogon = ShellRegistry.GetAutoLogon();
            if (!autoLogon.Enabled || !string.Equals(autoLogon.UserName, kiosk, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Kiosk {User} is not logged on, but auto-logon is not the kiosk's ({AutoLogonUser}); the PC is left as it is", kiosk, autoLogon.UserName);
                return false;
            }

            // Someone else at work: at the console, or an administrator over Remote Desktop.
            if (WtsSessions.Enumerate().FirstOrDefault(s => s.HasUser && s.IsActive) is { } other)
            {
                _logger.LogInformation("Kiosk {User} is not logged on, {Other} is signed in (session {SessionId}); no reboot", kiosk, other.UserName, other.Id);
                return false;
            }

            DateTimeOffset now = _clock.UtcNow;
            if (now - _startedAt < MinUptime)
            {
                return false;
            }

            if (LastReboot() is { } last && now - last < MinInterval)
            {
                _logger.LogError("Kiosk {User} did not log on ({Reason}) and the PC was already rebooted for that at {Last}; left on the sign-in screen", kiosk, reason, last);
                return false;
            }

            _ = _telemetry.Publish(TelemetryEventKinds.KioskLogonLost, new { reason });
            try
            {
                _power.Reboot(RebootDelaySec, message: null, force: true);
            }
            catch (Win32Exception ex)
            {
                _logger.LogError(ex, "Kiosk {User} did not log on again ({Reason}) and the reboot was refused", kiosk, reason);
                return false;
            }

            RememberReboot(now);
            _logger.LogError("Kiosk {User} did not log on again ({Reason}); rebooting the PC in {Delay} s to bring the Shell back", kiosk, reason, RebootDelaySec);
            return true;
        }
    }

    private string MarkerPath()
    {
        AgentSettings settings = _settings.CurrentValue;
        return Path.Combine(settings.ResolvePersistentPath(settings.Paths.Cache), MarkerFileName);
    }

    private DateTimeOffset? LastReboot()
    {
        try
        {
            string path = MarkerPath();
            return File.Exists(path) && DateTimeOffset.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RememberReboot(DateTimeOffset at)
    {
        try
        {
            string path = MarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, at.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remember the kiosk logon reboot");
        }
    }
}
