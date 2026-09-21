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
/// logs the kiosk user off, runs <see cref="ProfileReset.ResetProfile"/>, re-applies the shell-folder redirection
/// and relaunches the Shell. Resets are serialized, skipped while a session is open and rate-limited by
/// <see cref="Cooldown"/>. Triggered automatically on <see cref="SessionEventType.Ended"/> (subscribed while the
/// hosted service runs) and on demand through <see cref="IProfileResetTrigger"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProfileResetService : IProfileResetTrigger, IHostedService, IDisposable
{
    private const int MaxLogoffRounds = 4;
    private static readonly TimeSpan SessionSettleDelay = TimeSpan.FromSeconds(2);

    private readonly ProfileReset _reset;
    private readonly TempUserProvisioner _provisioner;
    private readonly IShellRelauncher _relauncher;
    private readonly ISessionService _sessions;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<ProfileResetService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset? _lastResetAt;
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
    public async Task RequestResetAsync(string reason, CancellationToken cancellationToken)
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
            if (_lastResetAt is { } last && now - last < Cooldown)
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
                var result = await Task.Run(() => _reset.ResetProfile(user), cancellationToken).ConfigureAwait(false);
                _lastResetAt = _clock.UtcNow;
                if (result.Deleted)
                {
                    _logger.LogInformation("Profile of {User} deleted, {Bytes} bytes freed", user, result.FreedBytes);
                }
                else
                {
                    _logger.LogWarning("Profile of {User} not fully deleted: {Errors}", user, string.Join("; ", result.Errors));
                }
            }
            finally
            {
                await _relauncher.RelaunchAsync(cancellationToken).ConfigureAwait(false);
                // The profile is recreated at logon; redirection applies once the hive exists (best effort here, retried by the provisioner).
                _ = _provisioner.ApplyFolderRedirect();
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
        if (e.Type != SessionEventType.Ended || !_settings.CurrentValue.Shell.KioskUser.ResetProfileOnLogout)
        {
            return;
        }

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
