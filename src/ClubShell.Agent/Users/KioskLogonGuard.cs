using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using ClubShell.Agent.Saves;
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
/// the Agent itself signed the kiosk out (<see cref="NoteSignedOut"/>: a profile reset after a session or for a debt),
/// auto-logon is the kiosk's and nobody else is working on the PC (console or Remote Desktop), the Agent reboots the PC
/// (auto-logon always runs at boot) and reports <see cref="TelemetryEventKinds.KioskLogonLost"/>. A sign-in screen the
/// Agent did not cause — an administrator holding Shift at boot — is left alone. Never in the first
/// <see cref="MinUptime"/> after the Agent starts (a slow boot is not a lost logon), never during an administrator's
/// maintenance window, never before the last player's saves are collected and the outbox (the session's end with it)
/// is on the server — it lives on C: of a diskless seat — and at most once per <see cref="MinInterval"/> (remembered
/// across the reboot), so a PC whose auto-logon cannot work stays on the sign-in screen, reported as
/// <see cref="LogonLost"/> in the heartbeat, instead of rebooting in a loop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KioskLogonGuard
{
    /// <summary>The Agent does not reboot sooner than this after it started.</summary>
    public static readonly TimeSpan MinUptime = TimeSpan.FromMinutes(5);

    /// <summary>Shortest time between two such reboots.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(15);

    private const string MarkerFileName = "kiosk-logon-reboot.txt";
    private const int RebootDelaySec = 10;

    private readonly TempUserProvisioner _provisioner;
    private readonly AdminMaintenance _maintenance;
    private readonly PowerControl _power;
    private readonly TelemetryBus _telemetry;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<KioskLogonGuard> _logger;
    private readonly IOfflineQueueDepth? _outbox;
    private readonly IPlayerSaves? _saves;
    private readonly DateTimeOffset _startedAt;
    private readonly object _gate = new();
    private bool _signedOutByAgent;
    private bool _logonLost;

    /// <summary>Creates the guard.</summary>
    public KioskLogonGuard(
        TempUserProvisioner provisioner,
        AdminMaintenance maintenance,
        PowerControl power,
        TelemetryBus telemetry,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<KioskLogonGuard> logger,
        IOfflineQueueDepth? outbox = null,
        IPlayerSaves? saves = null)
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
        _outbox = outbox;
        _saves = saves;
        _startedAt = clock.UtcNow;
    }

    /// <summary>
    /// The kiosk is on the sign-in screen because the Agent signed it out, and the reboot for that is spent: the heartbeat
    /// says so (<c>kioskUser.logonLost</c>) and the cash desk shows "PC on the sign-in screen".
    /// </summary>
    public bool LogonLost
    {
        get
        {
            lock (_gate)
            {
                if (_logonLost && WtsSessions.FindByUser(_provisioner.UserName) is not null)
                {
                    _logonLost = false;
                    _signedOutByAgent = false;
                }

                return _logonLost;
            }
        }
    }

    /// <summary>The Agent is about to sign the kiosk out (a profile reset): only then is a missing logon its to repair.</summary>
    public void NoteSignedOut()
    {
        lock (_gate)
        {
            _signedOutByAgent = true;
        }
    }

    /// <summary>The kiosk is logged on again: a later sign-in screen is not the Agent's doing.</summary>
    public void NoteBack()
    {
        lock (_gate)
        {
            _signedOutByAgent = false;
            _logonLost = false;
        }
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
            if (WtsSessions.FindByUser(kiosk) is not null)
            {
                _signedOutByAgent = false;
                _logonLost = false;
                return false;
            }

            if (!_signedOutByAgent)
            {
                // Shift at boot, an administrator who signed the kiosk out: their sign-in screen, not a lost logon.
                _logger.LogDebug("Kiosk {User} is not logged on, but the Agent did not sign it out; no reboot", kiosk);
                return false;
            }

            if (_maintenance.IsActive)
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
                if (!_logonLost)
                {
                    _logonLost = true;
                    _ = _telemetry.Publish(TelemetryEventKinds.KioskLogonLost, new { reason, rebooted = false });
                    _logger.LogError("Kiosk {User} did not log on ({Reason}) and the PC was already rebooted for that at {Last}; left on the sign-in screen", kiosk, reason, last);
                }

                return false;
            }

            // The last player's saves and the end of the session first: on a diskless seat the outbox is on C:.
            if (_saves?.Busy == true || _outbox is { Count: > 0 })
            {
                _logger.LogInformation("Kiosk {User} did not log on ({Reason}); the reboot waits for the saves' detach and the outbox ({Outbox} waiting)", kiosk, reason, _outbox?.Count ?? 0);
                return false;
            }

            _ = _telemetry.Publish(TelemetryEventKinds.KioskLogonLost, new { reason, rebooted = true });
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
