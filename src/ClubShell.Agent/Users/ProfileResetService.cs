using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Saves;
using ClubShell.Agent.Server;
using ClubShell.Agent.Session;
using ClubShell.Agent.Watchdog;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Hardware;
using ClubShell.Windows.Power;
using ClubShell.Windows.Sessions;
using ClubShell.Windows.Users;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Users;

/// <summary>Requests a kiosk profile reset (referenced by the Session module at session end).</summary>
public interface IProfileResetTrigger
{
    /// <summary>Resets the kiosk profile unless a session is open, a reset is running or the cooldown has not elapsed.</summary>
    Task RequestResetAsync(string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Resets the kiosk profile when a previous session never closed cleanly (see <c>ProfileReset.DirtyUser</c>).
    /// Called at Agent start, after the session has been restored so a session that is legitimately still running is
    /// not wiped from under the player, and then on every maintenance tick: cheap and silent when there is no debt,
    /// self-throttled when there is one it cannot pay.
    /// </summary>
    Task ResetIfDirtyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// «Сбросить профиль игрока на этом ПК» from the cash desk or the Shell's administrator menu (SHELL_CHANGES 76): the
    /// games are closed, the saves collected, the kiosk signed out and its profile deleted as after a session, and the
    /// open session goes on in the fresh profile (its time keeps running). Starts and returns at once;
    /// <paramref name="by"/> goes into the log. Throws <see cref="IpcException"/> <c>conflict</c> when it cannot start.
    /// </summary>
    Task<ResetProfileResult> ResetForStaffAsync(string by, CancellationToken cancellationToken);
}

/// <summary>Control over the interactive Shell process (implemented by the watchdog).</summary>
public interface IShellRelauncher
{
    /// <summary>Stops the Shell and suspends automatic restarts until <see cref="RelaunchAsync"/>.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resumes the watchdog, which starts the Shell once the kiosk session exists. The Agent does not log the kiosk on:
    /// Winlogon does, through auto-logon with <c>ForceAutoLogon</c> (<c>ShellRegistry.SetAutoLogon</c>).
    /// </summary>
    Task RelaunchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deletes the kiosk user's profile between sessions (<c>shell.kioskUser.resetProfileOnLogout</c>): stops the Shell,
/// logs the kiosk user off, runs <see cref="ProfileReset.ResetProfile(string, IReadOnlyList{string})"/>, re-applies
/// the shell-folder redirection, relaunches the Shell and moves the preserved anti-cheat directories
/// (<c>shell.kioskUser.preserveOnReset</c>) back into the recreated profile. Resets are serialized and skipped while a
/// session is open. Triggered automatically on <see cref="SessionEventType.Ended"/> (subscribed while the hosted service
/// runs), always: a player's session dirties the profile however soon after the last reset it ended (SHELL_CHANGES 42).
/// <see cref="Cooldown"/> rate-limits only the on-demand requests through <see cref="IProfileResetTrigger"/> and the
/// retries of a reset that failed.
/// <para>
/// After a session the kiosk is logged on again by Winlogon (<c>ForceAutoLogon</c>, SHELL_CHANGES 76); when it does not
/// come back within <see cref="RestoreTimeout"/>, <see cref="KioskLogonGuard"/> reboots the PC (SHELL_CHANGES 77). A PC
/// whose system disk is reset at every boot (<c>shell.kioskUser.resetMode</c> <c>reboot</c>, or <c>auto</c> on a PC the
/// server marks stateless — <c>pcs.stateless</c>, else on an iSCSI system disk) is rebooted after a session instead of
/// having its profile deleted (SHELL_CHANGES 78) — once the end of the session is on the server: the outbox is on C:
/// and would go with the reboot. While it is not, the profile is reset as on any PC and the reboot waits for an idle
/// moment with an empty outbox. A debt of such a PC is not paid by a reset: one from before the boot is gone with C:,
/// one from after it (the Agent restarted) is paid by that idle reboot.
/// </para>
/// <para>
/// A debt is never paid under a player who signed in and has not started a session yet. A reset that leaves the
/// profile behind twice in a row reboots the PC at the next idle moment (the next Agent start pays the debt), once;
/// a reset that fails after that reboot sets <see cref="ResetFailed"/>, which the heartbeat reports, until one succeeds.
/// </para>
/// <para>
/// When the diskless helper laid the player's saves out (SHELL_CHANGES 23), neither happens before it has collected
/// them: the reset waits for the detach (<see cref="IPlayerSaves.WaitReleasedAsync"/>), which itself starts once the
/// games have exited — the profile's folders would be empty by then otherwise.
/// </para>
/// <para>
/// A session that never ends — the PC loses power, the Agent is killed — produces no <see cref="SessionEventType.Ended"/>
/// and would leave the profile for the next player. <see cref="SessionEventType.Started"/> therefore writes a debt
/// marker that outlives the crash, and <see cref="ResetIfDirtyAsync"/> pays it at the next Agent start and on every
/// maintenance tick after that. The marker is cleared only once a reset has actually run, so a deletion that failed
/// is retried rather than forgotten.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProfileResetService : IProfileResetTrigger, IHostedService, IDisposable
{
    /// <summary><c>MSFT_PhysicalDisk.BusType</c> of an iSCSI disk.</summary>
    private const int IscsiBusType = 9;
    private const int RebootDelaySec = 5;
    private const string RebootMarkerFileName = "profile-reset-reboot.txt";
    private static readonly TimeSpan SessionSettleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RestorePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExplorerWatch = TimeSpan.FromSeconds(30);

    private readonly ProfileReset _reset;
    private readonly TempUserProvisioner _provisioner;
    private readonly IShellRelauncher _relauncher;
    private readonly ISessionService _sessions;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<ProfileResetService> _logger;
    private readonly PolicyEnforcer? _enforcer;
    private readonly KioskLogonGuard? _logonGuard;
    private readonly PowerControl? _power;
    private readonly WmiQueries? _wmi;
    private readonly IPlayerSaves? _saves;
    private readonly ServerPcFlags? _flags;
    private readonly ShellUserContext? _users;
    private readonly OfflineSessionStore? _outbox;
    private readonly AdminMaintenance? _maintenance;
    private readonly IGameSessionCleanup? _games;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _staffReset;
    private bool? _systemDiskIscsi;
    private bool _busTypeAsked;
    private string? _resetMode;
    private int _failedResets;
    private volatile bool _rebootForDebt;
    private volatile bool _rebootOwed;
    private volatile bool _resetFailed;
    private volatile bool _rebootRefused;
    private DateTimeOffset? _lastResetAt;
    private DateTimeOffset? _lastDirtyAttemptAt;
    private bool _disposed;

    /// <summary>Creates the service; <see cref="StartAsync"/> subscribes it to session changes.</summary>
    public ProfileResetService(
        ProfileReset reset,
        TempUserProvisioner provisioner,
        IShellRelauncher relauncher,
        ISessionService sessions,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<ProfileResetService> logger,
        PolicyEnforcer? enforcer = null,
        KioskLogonGuard? logonGuard = null,
        PowerControl? power = null,
        WmiQueries? wmi = null,
        IPlayerSaves? saves = null,
        ServerPcFlags? flags = null,
        ShellUserContext? users = null,
        OfflineSessionStore? outbox = null,
        AdminMaintenance? maintenance = null,
        IGameSessionCleanup? games = null)
    {
        ArgumentNullException.ThrowIfNull(reset);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(relauncher);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _reset = reset;
        _provisioner = provisioner;
        _relauncher = relauncher;
        _sessions = sessions;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _enforcer = enforcer;
        _logonGuard = logonGuard;
        _power = power;
        _wmi = wmi;
        _saves = saves;
        _flags = flags;
        _users = users;
        _outbox = outbox;
        _maintenance = maintenance;
        _games = games;
        _lastResetAt = reset.LastReset;
    }

    /// <summary>
    /// The profile could not be deleted again after the one reboot for that (SHELL_CHANGES 76): reported in the heartbeat
    /// (<c>kioskUser.profileResetFailed</c>) until a reset succeeds.
    /// </summary>
    public bool ResetFailed => _resetFailed;

    /// <summary>How the profile is reset after a session now (<c>logoff</c> or <c>reboot</c>); <see langword="null"/> until first decided.</summary>
    public string? ResetMode => _resetMode;

    /// <summary>Hosted-service start: subscribes to <see cref="ISessionService.Changed"/> for the automatic reset after a session.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sessions.Changed += OnSessionChanged;
        return Task.CompletedTask;
    }

    /// <summary>Hosted-service stop: unsubscribes; a reset already running completes on its own.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.Changed -= OnSessionChanged;
        return Task.CompletedTask;
    }

    /// <summary>Minimum time between two on-demand resets, and between two retries of a profile left behind; the reset after a session ignores it.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Time of the last completed reset (persisted by <see cref="ProfileReset"/>).</summary>
    public DateTimeOffset? LastResetAt => _lastResetAt;

    /// <summary><see langword="true"/> while a reset is running.</summary>
    public bool IsResetting => _lock.CurrentCount == 0;

    /// <inheritdoc />
    public Task RequestResetAsync(string reason, CancellationToken cancellationToken) => RequestResetAsync(reason, force: false, cancellationToken);

    /// <inheritdoc />
    public async Task ResetIfDirtyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Checked before the marker is read: an open session owns the profile until it ends, and this runs on every
        // maintenance tick, so the common case must not touch the disk at all. A player who signed in and has not
        // started a session yet owns it as much (SHELL_CHANGES 76): signing the kiosk out would throw them out.
        if (_sessions.State.IsOpen() || _users?.User is not null)
        {
            return;
        }

        // A PC whose reboot Windows refused pays its debts by a reset like any other, until the Agent restarts.
        if (!_rebootRefused && await ResetModeAsync().ConfigureAwait(false) == "reboot")
        {
            await PayByRebootAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_rebootForDebt)
        {
            if (await IdleForRebootAsync(cancellationToken).ConfigureAwait(false))
            {
                // One try: a reboot Windows refuses is reported like a reset that failed after it.
                _rebootForDebt = false;
                if (Reboot("the profile could not be deleted twice in a row"))
                {
                    RememberDebtReboot();
                }
                else
                {
                    _resetFailed = true;
                }
            }

            return;
        }

        if (_reset.DirtyUser is not { } dirtyUser)
        {
            return;
        }

        // Throttled on the last attempt made by this process rather than on Cooldown, which counts from the last
        // reset — including the one that failed to clear the debt. So the first call after a start always tries
        // (a reboot is exactly what releases the locks that made it fail), and the maintenance loop that calls this
        // every few seconds retries quietly at most once per Cooldown instead of hammering a profile it cannot
        // delete.
        var now = _clock.UtcNow;
        if (_lastDirtyAttemptAt is { } attempted && now - attempted < Cooldown)
        {
            return;
        }

        _lastDirtyAttemptAt = now;
        _logger.LogWarning("Profile of {User} was left behind by a session that did not close cleanly; resetting before the next player", dirtyUser);
        await RequestResetAsync("dirtyProfile", force: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ResetProfileResult> ResetForStaffAsync(string by, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_provisioner.IsProvisioned)
        {
            throw IpcError.Conflict("The player account of this PC is not ready", "kioskNotReady").ToException();
        }

        if (IsResetting || Interlocked.CompareExchange(ref _staffReset, 1, 0) != 0)
        {
            throw IpcError.Conflict("A profile reset is already running", "resetRunning").ToException();
        }

        Guid? sessionId = _sessions.State.IsOpen() ? _sessions.Current?.Id : null;
        _logger.LogWarning("Player profile reset requested by {By}; session {SessionId} goes on", by, sessionId);
        _ = Task.Run(() => ResetForStaffCoreAsync(by));
        return Task.FromResult(new ResetProfileResult(true, sessionId));
    }

    private async Task ResetForStaffCoreAsync(string by)
    {
        try
        {
            User? player = _users?.User;
            // The games first, then the saves out of the profile (SHELL_CHANGES 23): the detach closes the games itself.
            if (_saves?.Attached == true)
            {
                _saves.BeginRelease(SessionEndReason.Admin, closeGames: true);
            }
            else if (_games is not null)
            {
                await _games.KillAllAsync(SessionEndReason.Admin, graceful: true, CancellationToken.None).ConfigureAwait(false);
            }

            if (_saves is not null)
            {
                TimeSpan max = TimeSpan.FromSeconds(40 + _settings.CurrentValue.Games.PlayerSaves.DetachTimeoutSec);
                await _saves.WaitReleasedAsync(max, CancellationToken.None).ConfigureAwait(false);
            }

            await RequestResetAsync("requested by " + by, force: true, CancellationToken.None, duringSession: true).ConfigureAwait(false);

            if (_sessions.State.IsOpen())
            {
                // The player goes on in the fresh profile: it owes a reset again at the session's end.
                try
                {
                    _reset.MarkDirty(_provisioner.UserName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Profile could not be marked dirty after the reset");
                }
            }

            // Back in: the saves of the same player laid out again into the new profile.
            if (player is not null && _users?.User?.Id == player.Id && WtsSessions.FindByUser(_provisioner.UserName) is not null)
            {
                _saves?.SignedIn(player);
            }

            _logger.LogInformation("Player profile reset requested by {By} finished", by);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Player profile reset requested by {By} failed", by);
        }
        finally
        {
            Volatile.Write(ref _staffReset, 0);
        }
    }

    /// <summary>
    /// Shared body of the triggers; <paramref name="force"/> skips the cooldown for a profile a player has used;
    /// <paramref name="duringSession"/> (a staff reset) runs under an open session.
    /// </summary>
    private async Task RequestResetAsync(string reason, bool force, CancellationToken cancellationToken, bool duringSession = false)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _lock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Profile reset ({Reason}) skipped: a reset is already running", reason);
            return;
        }

        try
        {
            var now = _clock.UtcNow;
            if (!force && _lastResetAt is { } last && now - last < Cooldown)
            {
                _logger.LogInformation("Profile reset ({Reason}) skipped: last reset {Last}, cooldown {Cooldown}", reason, last, Cooldown);
                return;
            }

            if (!duringSession && _sessions.State.IsOpen())
            {
                _logger.LogWarning("Profile reset ({Reason}) skipped: a session is open", reason);
                return;
            }

            // Without a provisioned kiosk the configured name may be someone else's account (a refused administrator,
            // SHELL_CHANGES 24): its profile is not ours to delete.
            if (!_provisioner.IsProvisioned)
            {
                _logger.LogWarning("Profile reset ({Reason}) skipped: the kiosk account is not provisioned", reason);
                return;
            }

            var user = _provisioner.UserName;
            _logger.LogInformation("Resetting profile of {User}: {Reason}", user, reason);
            try
            {
                await _relauncher.StopAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<string> preserve = _settings.CurrentValue.Shell.KioskUser.PreserveOnReset ?? ProfileReset.PreservedDirectories;
                // The sessions of the player who just left, logged off once each: Winlogon logs the kiosk on again at
                // once (ForceAutoLogon), and that session is the next player's, not one to chase.
                IReadOnlyCollection<uint> sessions = ProfileReset.SessionsOf(user);
                if (sessions.Count > 0)
                {
                    // The Agent signs the kiosk out: if Winlogon does not bring it back, that is the Agent's to repair.
                    _logonGuard?.NoteSignedOut();
                }

                var result = await Task.Run(() => _reset.ResetProfile(user, preserve, sessions), cancellationToken).ConfigureAwait(false);
                // The redirected Desktop, Documents, ... live outside the profile: empty them too, while the kiosk is logged off.
                await Task.Run(_provisioner.ClearRedirectedFolders, cancellationToken).ConfigureAwait(false);
                _lastResetAt = _clock.UtcNow;
                if (result.Deleted)
                {
                    _logger.LogInformation("Profile of {User} deleted, {Bytes} bytes freed", user, result.FreedBytes);
                }
                else
                {
                    _logger.LogWarning("Profile of {User} not fully deleted: {Errors}", user, string.Join("; ", result.Errors));
                }

                // The debt is paid when the profile is gone, and equally when there was none to delete. It survives a
                // failed deletion on purpose: the next Agent start is the only thing that will try again.
                if (result.Deleted || result.Errors.Count == 0)
                {
                    _reset.ClearDirty();
                    _failedResets = 0;
                    _rebootForDebt = false;
                    _resetFailed = false;
                    ForgetDebtReboot();
                }
                else
                {
                    NoteFailedReset();
                }
            }
            finally
            {
                await _relauncher.RelaunchAsync(cancellationToken).ConfigureAwait(false);
                // The profile is recreated at logon; redirection applies once the hive exists (best effort here, retried by the provisioner).
                _ = _provisioner.ApplyFolderRedirect();
                await RestorePreservedAsync(user, cancellationToken).ConfigureAwait(false);
                await SealFreshProfileAsync(user, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessions.Changed -= OnSessionChanged;
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Waits for the relaunch to recreate the profile and moves the preserved anti-cheat directories back in. Gives
    /// up after <see cref="RestoreTimeout"/>; the stash survives and the next reset restores it instead.
    /// </summary>
    private async Task RestorePreservedAsync(string user, CancellationToken cancellationToken)
    {
        var deadline = _clock.UtcNow + RestoreTimeout;
        while (true)
        {
            try
            {
                if (_reset.RestorePreserved(user) is not null)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Restoring preserved directories of {User} failed", user);
                return;
            }

            if (_clock.UtcNow >= deadline)
            {
                _logger.LogWarning("Profile of {User} did not reappear within {Timeout}; preserved directories stay in the stash", user, RestoreTimeout);
                return;
            }

            await _clock.Delay(RestorePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The profile Windows recreates at the next logon comes from Default: no per-user shell replacement, no Explorer
    /// lockdown, so userinit starts explorer.exe and the player would get a desktop (the reason the pilot ran with the
    /// reset off). Once the kiosk user is logged on again, every policy section is written into the new hive, and the
    /// explorer.exe of that session is closed for <see cref="ExplorerWatch"/>; the watchdog starts the Shell. The next
    /// logon already reads the shell from the hive.
    /// </summary>
    private async Task SealFreshProfileAsync(string user, CancellationToken cancellationToken)
    {
        if (_enforcer is null)
        {
            return;
        }

        var deadline = _clock.UtcNow + RestoreTimeout;
        WtsSession? session;
        while ((session = WtsSessions.FindByUser(user)) is null)
        {
            if (_clock.UtcNow >= deadline)
            {
                _logger.LogWarning("Kiosk user {User} did not log on again within {Timeout}; policies are re-applied at the next logon", user, RestoreTimeout);
                _ = _logonGuard?.Recover("profileReset");
                return;
            }

            await _clock.Delay(RestorePollInterval, cancellationToken).ConfigureAwait(false);
        }

        _logonGuard?.NoteBack();
        try
        {
            await _enforcer.ReapplyAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Re-applying the kiosk policies to the recreated profile of {User} failed", user);
        }

        if (_enforcer.Current?.ShellReplacement.Enabled != true)
        {
            return;
        }

        var watchEnd = _clock.UtcNow + ExplorerWatch;
        while (_clock.UtcNow < watchEnd)
        {
            foreach (Process explorer in Process.GetProcessesByName("explorer"))
            {
                using (explorer)
                {
                    try
                    {
                        if (explorer.SessionId == (int)session.Id)
                        {
                            explorer.Kill(entireProcessTree: false);
                            _logger.LogInformation("Closed explorer.exe (pid {Pid}) started by the recreated profile of {User}", explorer.Id, user);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        _logger.LogDebug(ex, "explorer.exe {Pid} could not be closed", explorer.Id);
                    }
                }
            }

            await _clock.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnSessionChanged(object? sender, SessionEvent e)
    {
        if (!_settings.CurrentValue.Shell.KioskUser.ResetProfileOnLogout)
        {
            return;
        }

        // Marked at the start, not at the end: a power cut mid-session never produces an Ended event, and the profile
        // is dirty from the moment the player logs in.
        if (e.Type == SessionEventType.Started)
        {
            try
            {
                _reset.MarkDirty(_provisioner.UserName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Profile could not be marked dirty; a crash during this session would leave it behind");
            }

            return;
        }

        if (e.Type != SessionEventType.Ended)
        {
            return;
        }

        // Claim the debt for the ordinary path before it settles, so a maintenance tick landing in that window does
        // not race it and report a perfectly clean session end as a profile left behind. If this reset fails, the
        // claim expires with the cooldown and ResetIfDirtyAsync takes over — by then it really was left behind.
        _lastDirtyAttemptAt = _clock.UtcNow;
        _ = ResetAfterSessionAsync();
    }

    private async Task ResetAfterSessionAsync()
    {
        try
        {
            await _clock.Delay(SessionSettleDelay, CancellationToken.None).ConfigureAwait(false);
            if (_saves is not null)
            {
                // The cleanup closes the games (a step of up to 20 s) and only then starts the detach.
                TimeSpan max = TimeSpan.FromSeconds(40 + _settings.CurrentValue.Games.PlayerSaves.DetachTimeoutSec);
                await _saves.WaitReleasedAsync(max, CancellationToken.None).ConfigureAwait(false);
            }

            if (await ResetModeAsync().ConfigureAwait(false) == "reboot")
            {
                if (await OutboxEmptyAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (RebootAfterSession())
                    {
                        return;
                    }
                }
                else
                {
                    // The outbox (the end of this session with it) is on C:, which the reboot resets: the profile is
                    // reset as on any PC, and the reboot waits until the server has it.
                    _rebootOwed = true;
                    _logger.LogWarning("Session ended on a PC whose system disk is reset at boot, but its end is not on the server yet; resetting the profile, rebooting once the outbox is sent");
                }
            }

            // Forced: a session that ended within the cooldown of the last reset (a short one, or one right after the
            // previous player's) would otherwise hand its profile to the next player.
            await RequestResetAsync("sessionEnded", force: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Profile reset after session end failed");
        }
    }

    /// <summary>
    /// <c>shell.kioskUser.resetMode</c> as it applies now: <c>reboot</c> or <c>logoff</c> as set; <c>auto</c> follows the
    /// server's <c>pcs.stateless</c> once a heartbeat has brought it, else reboots on an iSCSI system disk.
    /// </summary>
    private async Task<string> ResetModeAsync()
    {
        string mode = await DecideResetModeAsync().ConfigureAwait(false);
        if (!string.Equals(_resetMode, mode, StringComparison.Ordinal))
        {
            _logger.LogInformation("Profile reset after a session: {Mode}", mode);
            _resetMode = mode;
        }

        return mode;
    }

    private async Task<string> DecideResetModeAsync()
    {
        string mode = _settings.CurrentValue.Shell.KioskUser.ResetMode;
        if (_power is null || mode == "logoff")
        {
            return "logoff";
        }

        if (mode == "reboot")
        {
            return "reboot";
        }

        if (_flags?.Stateless is { } stateless)
        {
            return stateless ? "reboot" : "logoff";
        }

        if (_systemDiskIscsi is null && _wmi is not null && !_busTypeAsked)
        {
            // Asked once: this runs on every maintenance tick, and a bus type WMI cannot tell stays unknown.
            _busTypeAsked = true;
            try
            {
                if (await _wmi.GetSystemDiskBusTypeAsync(CancellationToken.None).ConfigureAwait(false) is { } bus)
                {
                    _systemDiskIscsi = bus == IscsiBusType;
                    _logger.LogInformation("System disk bus type {BusType}: {Mode} after a session", bus, _systemDiskIscsi == true ? "reboot" : "profile reset");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "System disk bus type unknown");
            }
        }

        return _systemDiskIscsi == true ? "reboot" : "logoff";
    }

    /// <summary>
    /// A PC reset by rebooting (SHELL_CHANGES 78): a debt is not paid by deleting the profile. One written before this
    /// boot went with C:; one written after it (the Agent restarted without a reboot), or a reboot still owed by a
    /// session whose end was not on the server, is paid by a reboot at the next idle moment.
    /// </summary>
    private async Task PayByRebootAsync(CancellationToken cancellationToken)
    {
        if (!_rebootOwed && _reset.DirtyUser is { } dirtyUser)
        {
            DateTimeOffset bootedAt = _clock.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (_reset.DirtySince is { } since && since > bootedAt)
            {
                _rebootOwed = true;
                _logger.LogWarning("Profile of {User} was left behind since {Since}, after the last boot; the PC reboots at the next idle moment", dirtyUser, since);
            }
            else
            {
                _reset.ClearDirty();
                _logger.LogInformation("Profile debt of {User} from before the boot dropped: the system disk came back clean", dirtyUser);
            }
        }

        if (_rebootOwed && await IdleForRebootAsync(cancellationToken).ConfigureAwait(false))
        {
            // One try: after a refusal a debt is paid by a reset (ResetIfDirtyAsync).
            _rebootOwed = false;
            if (Reboot("a reboot owed after a session"))
            {
                _reset.ClearDirty();
            }
            else
            {
                _rebootRefused = true;
            }
        }
    }

    /// <summary>
    /// Nobody would lose anything to a reboot now: no session, nobody signed in or working on the PC (console or Remote
    /// Desktop), no maintenance window, the saves collected and the outbox on the server.
    /// </summary>
    private async Task<bool> IdleForRebootAsync(CancellationToken cancellationToken)
    {
        if (_sessions.State.IsOpen() || _users?.User is not null || _maintenance?.IsActive == true || _saves?.Busy == true)
        {
            return false;
        }

        string kiosk = _provisioner.UserName;
        if (WtsSessions.Enumerate().Any(s => s.HasUser && s.IsActive && !string.Equals(s.UserName, kiosk, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return await OutboxEmptyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Nothing waits in the outbox (the end of the last session included); a store that cannot tell counts as not empty.</summary>
    private async Task<bool> OutboxEmptyAsync(CancellationToken cancellationToken)
    {
        if (_outbox is null)
        {
            return true;
        }

        try
        {
            return (await _outbox.GetQueueStatsAsync(cancellationToken).ConfigureAwait(false)).Pending == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Outbox state unknown; no reboot for now");
            return false;
        }
    }

    /// <summary>
    /// A reset left the profile behind. The second in a row reboots the PC at the next idle moment, once: the reboot
    /// releases whatever held the files, and the next Agent start pays the debt. A failure after that reboot is
    /// reported (<see cref="ResetFailed"/>) instead of another reboot.
    /// </summary>
    private void NoteFailedReset()
    {
        _failedResets++;
        if (DebtRebootSpent())
        {
            if (!_resetFailed)
            {
                _logger.LogError("Profile of the kiosk still cannot be deleted after the reboot for that; reported, no more reboots");
            }

            _resetFailed = true;
        }
        else if (_failedResets >= 2 && _power is not null)
        {
            _rebootForDebt = true;
            _logger.LogWarning("Profile of the kiosk could not be deleted twice in a row; the PC reboots at the next idle moment");
        }
    }

    private bool Reboot(string why)
    {
        try
        {
            _power!.Reboot(RebootDelaySec, message: null, force: true);
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "Reboot ({Why}) refused", why);
            return false;
        }

        _logger.LogWarning("Rebooting the PC in {Delay} s: {Why}", RebootDelaySec, why);
        return true;
    }

    private string DebtRebootMarker()
    {
        AgentSettings settings = _settings.CurrentValue;
        return Path.Combine(settings.ResolvePersistentPath(settings.Paths.Cache), RebootMarkerFileName);
    }

    private bool DebtRebootSpent()
    {
        try
        {
            return File.Exists(DebtRebootMarker());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RememberDebtReboot()
    {
        try
        {
            string path = DebtRebootMarker();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remember the reboot for the profile debt");
        }
    }

    private void ForgetDebtReboot()
    {
        try
        {
            File.Delete(DebtRebootMarker());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Reboot marker of the profile debt not deleted");
        }
    }

    /// <summary>
    /// The reboot is the reset: the system disk of such a PC comes back clean, the kiosk's profile with it, and auto-logon
    /// runs at boot. Returns <see langword="false"/> when Windows refuses the reboot, and the profile is reset instead.
    /// </summary>
    private bool RebootAfterSession()
    {
        if (_sessions.State.IsOpen())
        {
            // The same rule as a reset: an open session is never pulled from under a player.
            _logger.LogInformation("Reboot after the session skipped: the next session already started");
            return true;
        }

        try
        {
            _power!.Reboot(RebootDelaySec, message: null, force: true);
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "Reboot after the session failed; resetting the profile instead");
            return false;
        }

        _reset.ClearDirty();
        _logger.LogInformation("Session ended on a PC whose system disk is reset at boot: rebooting in {Delay} s instead of resetting the profile", RebootDelaySec);
        return true;
    }
}
