using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
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
}

/// <summary>Control over the interactive Shell process (implemented by the watchdog).</summary>
public interface IShellRelauncher
{
    /// <summary>Stops the Shell and suspends automatic restarts until <see cref="RelaunchAsync"/>.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>Resumes the watchdog and starts the Shell in the interactive session (logging the kiosk user on when needed).</summary>
    Task RelaunchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deletes the kiosk user's profile between sessions (<c>shell.kioskUser.resetProfileOnLogout</c>): stops the Shell,
/// logs the kiosk user off, runs <see cref="ProfileReset.ResetProfile(string, IReadOnlyList{string})"/>, re-applies
/// the shell-folder redirection, relaunches the Shell and moves the preserved anti-cheat directories
/// (<c>shell.kioskUser.preserveOnReset</c>) back into the recreated profile. Resets are serialized, skipped while a
/// session is open and rate-limited by <see cref="Cooldown"/>. Triggered automatically on
/// <see cref="SessionEventType.Ended"/> (subscribed while the hosted service runs) and on demand through
/// <see cref="IProfileResetTrigger"/>.
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
    private const int MaxLogoffRounds = 4;
    private static readonly TimeSpan SessionSettleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RestorePollInterval = TimeSpan.FromSeconds(2);

    private readonly ProfileReset _reset;
    private readonly TempUserProvisioner _provisioner;
    private readonly IShellRelauncher _relauncher;
    private readonly ISessionService _sessions;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<ProfileResetService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset? _lastResetAt;
    private DateTimeOffset? _lastDirtyAttemptAt;
    private bool _disposed;

    /// <summary>Creates the service; <see cref="StartAsync"/> subscribes it to session changes.</summary>
    public ProfileResetService(ProfileReset reset, TempUserProvisioner provisioner, IShellRelauncher relauncher, ISessionService sessions, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<ProfileResetService> logger)
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
        _lastResetAt = reset.LastReset;
    }

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

    /// <summary>Minimum time between two resets.</summary>
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
        // maintenance tick, so the common case must not touch the disk at all.
        if (_sessions.State.IsOpen())
        {
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

    /// <summary>Shared body of both triggers; <paramref name="force"/> skips the cooldown for a debt that must be paid.</summary>
    private async Task RequestResetAsync(string reason, bool force, CancellationToken cancellationToken)
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

            if (_sessions.State.IsOpen())
            {
                _logger.LogWarning("Profile reset ({Reason}) skipped: a session is open", reason);
                return;
            }

            var user = _provisioner.UserName;
            _logger.LogInformation("Resetting profile of {User}: {Reason}", user, reason);
            try
            {
                await _relauncher.StopAsync(cancellationToken).ConfigureAwait(false);
                LogoffKioskSessions(user);
                IReadOnlyList<string> preserve = _settings.CurrentValue.Shell.KioskUser.PreserveOnReset ?? ProfileReset.PreservedDirectories;
                var result = await Task.Run(() => _reset.ResetProfile(user, preserve), cancellationToken).ConfigureAwait(false);
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
                }
            }
            finally
            {
                await _relauncher.RelaunchAsync(cancellationToken).ConfigureAwait(false);
                // The profile is recreated at logon; redirection applies once the hive exists (best effort here, retried by the provisioner).
                _ = _provisioner.ApplyFolderRedirect();
                await RestorePreservedAsync(user, cancellationToken).ConfigureAwait(false);
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

    private void LogoffKioskSessions(string user)
    {
        for (var round = 0; round < MaxLogoffRounds; round++)
        {
            var session = WtsSessions.FindByUser(user);
            if (session is null)
            {
                return;
            }

            try
            {
                _logger.LogInformation("Logging off session {SessionId} of {User}", session.Id, user);
                WtsSessions.Logoff(session.Id, wait: true);
            }
            catch (Win32Exception ex)
            {
                _logger.LogWarning(ex, "Logoff of session {SessionId} failed", session.Id);
                return;
            }
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
            await RequestResetAsync("sessionEnded", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Profile reset after session end failed");
        }
    }
}
