using ClubShell.Agent.Ipc;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Saves;

/// <summary>A player's game saves on any PC of the club (SHELL_CHANGES 23), as the rest of the Agent sees them.</summary>
public interface IPlayerSaves
{
    /// <summary>Where the signed-in player's saves are (also sent to the Shell as <c>saves.status</c>).</summary>
    PlayerSavesState State { get; }

    /// <summary>
    /// <see langword="true"/> from the moment an attach is sent until its detach starts: games must then close gently,
    /// and the profile must wait for the detach.
    /// </summary>
    bool Attached { get; }

    /// <summary>
    /// A successful <c>auth.login</c>: starts laying the player's saves out and returns at once. Never for a guest
    /// (<paramref name="guest"/>, or the user's role). The same player signing in again changes nothing.
    /// </summary>
    void SignedIn(User user, bool guest = false);

    /// <summary>A game launch waits here for the saves to be laid out (up to the attach timeout); never throws but on cancel.</summary>
    Task WaitForAttachAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The end of the player's session or sign-out: collects the saves in the background (<c>profile.detach</c>, retried
    /// within the detach timeout) for every attach that was sent, failed and timed-out ones included. With
    /// <paramref name="closeGames"/> the running games are closed first and their exit awaited — the session cleanup has
    /// already done that. Returns at once; <see cref="WaitReleasedAsync"/> waits for the end.
    /// </summary>
    void BeginRelease(SessionEndReason reason, bool closeGames);

    /// <summary>
    /// The profile reset waits here, up to <paramref name="max"/>, for the detach of the last player — also for one that
    /// has not started yet because the games are still closing. <see langword="false"/> when the wait ran out.
    /// </summary>
    Task<bool> WaitReleasedAsync(TimeSpan max, CancellationToken cancellationToken);
}

/// <summary>
/// Tells the diskless helper when to lay a player's saves out and when to collect them (SHELL_CHANGES 23). The club
/// server keeps the saves and the helper moves the files; the Agent only orders it, through
/// <see cref="IDisklessHelperChannel"/>:
/// <list type="bullet">
/// <item><c>profile.attach</c> after every successful sign-in of a member (online or from the offline cache), before the
/// first game: a launch waits for it up to <see cref="PlayerSavesSettings.AttachTimeoutSec"/>; any failure lets the player
/// play without saves and the Shell says so;</item>
/// <item><c>profile.detach</c> at the end of the session, after the games have closed and before the kiosk is signed out
/// and its profile reset (<see cref="WaitReleasedAsync"/>), retried within <see cref="PlayerSavesSettings.DetachTimeoutSec"/>
/// while the helper may still succeed;</item>
/// <item>never after the profile was reset: what was laid out is held only in memory, so an Agent restart forgets it and
/// a missed detach is not caught up — the helper's copy on the server stays the previous one.</item>
/// </list>
/// </summary>
public sealed class PlayerSavesService : IPlayerSaves, IHostedService
{
    private readonly IDisklessHelperChannel _channel;
    private readonly IKioskCredentials _kiosk;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<PlayerSavesService> _logger;
    private readonly IIpcEventPublisher? _events;
    private readonly IGameSessionCleanup? _games;
    private readonly Func<bool> _helperInstalled;
    private readonly object _gate = new();
    private Attachment? _current;
    private PlayerSavesState _state = PlayerSavesState.None;
    private Task _publishing = Task.CompletedTask;

    /// <summary>Creates the service.</summary>
    /// <param name="channel">The helper's saves channel.</param>
    /// <param name="kiosk">The kiosk account whose profile receives the saves.</param>
    /// <param name="settings">Agent settings (<c>games.playerSaves</c>).</param>
    /// <param name="clock">Time source.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="events">Shell events (<c>saves.status</c>).</param>
    /// <param name="games">Closes the games before a sign-out's detach and kills what still holds files.</param>
    /// <param name="helperInstalled">Probe for the helper service (<c>auto</c> mode); the registry by default.</param>
    public PlayerSavesService(
        IDisklessHelperChannel channel,
        IKioskCredentials kiosk,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<PlayerSavesService> logger,
        IIpcEventPublisher? events = null,
        IGameSessionCleanup? games = null,
        Func<bool>? helperInstalled = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _channel = channel;
        _kiosk = kiosk;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _events = events;
        _games = games;
        _helperInstalled = helperInstalled ?? HelperServiceInstalled;
    }

    /// <summary>Pause between two detach attempts.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public PlayerSavesState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public bool Attached
    {
        get
        {
            lock (_gate)
            {
                return _current is { Release: null };
            }
        }
    }

    private PlayerSavesSettings Config => _settings.CurrentValue.Games.PlayerSaves;

    private TimeSpan AttachTimeout => TimeSpan.FromSeconds(Config.AttachTimeoutSec);

    private TimeSpan DetachTimeout => TimeSpan.FromSeconds(Config.DetachTimeoutSec);

    /// <summary><c>games.playerSaves.mode</c>: <c>on</c>, <c>off</c>, or <c>auto</c> — on where the helper service is installed.</summary>
    public bool Enabled => Config.Mode switch
    {
        "on" => true,
        "off" => false,
        _ => SafeHelperInstalled(),
    };

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Enabled)
        {
            if (_settings.CurrentValue.Shell.KioskUser.FolderRedirect.Enabled)
            {
                // The helper looks for %DOCUMENTS% and %SAVEDGAMES% inside the kiosk profile.
                _logger.LogWarning("Saves: shell.kioskUser.folderRedirect is on, so Documents and Saved Games are outside the profile where the diskless helper lays the player's saves; turn it off on this PC (SHELL_CHANGES 23)");
            }

            _ = Task.Run(PingAsync, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void SignedIn(User user, bool guest = false)
    {
        ArgumentNullException.ThrowIfNull(user);
        guest |= user.Role == UserRole.Guest;
        if (!Enabled)
        {
            return;
        }

        string playerId = HelperProtocol.PlayerId(user.Id);
        Attachment attachment;
        Task previous;
        lock (_gate)
        {
            if (_current is { Release: null } same && !guest && string.Equals(same.PlayerId, playerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Another player's saves still out (no session end in between): collected before anything else.
            if (_current is { Release: null } other)
            {
                StartReleaseLocked(other, SessionEndReason.User, closeGames: false);
            }

            if (guest)
            {
                _logger.LogInformation("Saves: guest signed in; no saves are laid out for guests");
                if (_current is null)
                {
                    SetStateLocked(PlayerSavesState.None);
                }

                return;
            }

            previous = _current?.Release ?? Task.CompletedTask;
            attachment = new Attachment(playerId);
            _current = attachment;
            SetStateLocked(PlayerSavesState.Loading);
        }

        // Off the lock and off the IPC thread; a launch waits on attachment.AttachDone, set before this runs.
        _ = Task.Run(() => AttachAsync(attachment, previous));
    }

    /// <inheritdoc />
    public async Task WaitForAttachAsync(CancellationToken cancellationToken)
    {
        Task? attach;
        lock (_gate)
        {
            attach = _current is { Release: null } current ? current.AttachDone.Task : null;
        }

        if (attach is null || attach.IsCompleted)
        {
            return;
        }

        try
        {
            await attach.WaitAsync(AttachTimeout + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Saves: the launch stopped waiting for the attach after {Timeout}", AttachTimeout);
        }
    }

    /// <inheritdoc />
    public void BeginRelease(SessionEndReason reason, bool closeGames)
    {
        lock (_gate)
        {
            if (_current is { Release: null } current)
            {
                StartReleaseLocked(current, reason, closeGames);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitReleasedAsync(TimeSpan max, CancellationToken cancellationToken)
    {
        Attachment? current;
        lock (_gate)
        {
            current = _current;
        }

        if (current is null)
        {
            return true;
        }

        try
        {
            await current.Released.Task.WaitAsync(max, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Saves: detach of player {PlayerId} not finished within {Max}; the profile is reset anyway", current.PlayerId, max);
            return false;
        }
    }

    private void StartReleaseLocked(Attachment attachment, SessionEndReason reason, bool closeGames)
    {
        attachment.Release = Task.Run(() => ReleaseAsync(attachment, reason, closeGames));
    }

    private async Task AttachAsync(Attachment attachment, Task previous)
    {
        try
        {
            await AttachCoreAsync(attachment, previous).ConfigureAwait(false);
        }
        finally
        {
            attachment.AttachDone.TrySetResult();
        }
    }

    private async Task AttachCoreAsync(Attachment attachment, Task previous)
    {
        // The helper runs attach and detach one at a time anyway; waiting here keeps the order in the log, too.
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Saves: previous detach failed");
        }

        string sid = _kiosk.Sid;
        HelperResult result;
        long started = _clock.GetTimestamp();
        if (string.IsNullOrEmpty(sid))
        {
            result = new HelperResult(HelperResultKind.Failed, Message: "the kiosk account has no SID yet");
        }
        else
        {
            try
            {
                result = await _channel.SendAsync(HelperProtocol.Attach(attachment.PlayerId, sid), AttachTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new HelperResult(HelperResultKind.Failed, Message: ex.Message);
            }
        }

        LogAttach(attachment.PlayerId, result, _clock.GetElapsedTime(started));
        lock (_gate)
        {
            if (ReferenceEquals(_current, attachment) && attachment.Release is null)
            {
                SetStateLocked(result.IsOk ? PlayerSavesState.Ready : PlayerSavesState.Unavailable);
            }
        }
    }

    private async Task ReleaseAsync(Attachment attachment, SessionEndReason reason, bool closeGames)
    {
        long started = _clock.GetTimestamp();
        try
        {
            // A detach sent before its attach would find nothing laid out and leave the attach's files behind.
            try
            {
                await attachment.AttachDone.Task.WaitAsync(AttachTimeout + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Saves: attach of player {PlayerId} still unanswered; detaching anyway", attachment.PlayerId);
            }

            if (closeGames && _games is not null)
            {
                await CloseGamesAsync(reason, graceful: true).ConfigureAwait(false);
            }

            await DetachAsync(attachment, reason).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Saves: detach of player {PlayerId} failed", attachment.PlayerId);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, attachment))
                {
                    _current = null;
                    if (_state != PlayerSavesState.TooLarge)
                    {
                        SetStateLocked(PlayerSavesState.None);
                    }
                }
            }

            attachment.Released.TrySetResult();
            _logger.LogDebug("Saves: release of player {PlayerId} took {Duration}", attachment.PlayerId, _clock.GetElapsedTime(started));
        }
    }

    private async Task DetachAsync(Attachment attachment, SessionEndReason reason)
    {
        long started = _clock.GetTimestamp();
        TimeSpan budget = DetachTimeout;
        for (int attempt = 1; ; attempt++)
        {
            TimeSpan remaining = budget - _clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                _logger.LogWarning("Saves: detach of player {PlayerId} not confirmed within {Budget}; the session's progress is lost, the server keeps the previous version", attachment.PlayerId, budget);
                return;
            }

            HelperResult result = await _channel.SendAsync(HelperProtocol.Detach(attachment.PlayerId), remaining, CancellationToken.None).ConfigureAwait(false);
            bool retry = LogDetach(attachment.PlayerId, result, attempt, _clock.GetElapsedTime(started));
            if (result.Kind == HelperResultKind.Error && result.Code == HelperErrorCode.OverLimit)
            {
                lock (_gate)
                {
                    SetStateLocked(PlayerSavesState.TooLarge);
                }
            }

            if (!retry)
            {
                return;
            }

            if (result.Kind == HelperResultKind.Error && result.Code == HelperErrorCode.Internal && _games is not null)
            {
                // Usually a file still held by a game: the helper already retried three times.
                await CloseGamesAsync(reason, graceful: false).ConfigureAwait(false);
            }

            remaining = budget - _clock.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                await _clock.Delay(remaining < RetryDelay ? remaining : RetryDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task CloseGamesAsync(SessionEndReason reason, bool graceful)
    {
        try
        {
            await _games!.KillAllAsync(reason, graceful, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Saves: closing the games before the detach failed");
        }
    }

    private void LogAttach(string playerId, HelperResult result, TimeSpan took)
    {
        if (result.IsOk)
        {
            _logger.LogInformation("Saves attach of player {PlayerId}: ok in {Duration}", playerId, took);
            return;
        }

        // badRequest and profileNotFound (a wrong SID, the kiosk profile not created yet) are the Agent's own mistakes.
        bool ours = result.Kind == HelperResultKind.Error && result.Code is HelperErrorCode.BadRequest or HelperErrorCode.ProfileNotFound;
        _logger.Log(
            ours ? LogLevel.Error : LogLevel.Warning,
            "Saves attach of player {PlayerId}: {Result} in {Duration} ({Message}); the player plays without saves",
            playerId,
            result.Describe(),
            took,
            result.Message);
    }

    /// <summary>Logs a detach answer and says whether another attempt may still help.</summary>
    private bool LogDetach(string playerId, HelperResult result, int attempt, TimeSpan took)
    {
        switch (result.Kind)
        {
            case HelperResultKind.Ok:
                _logger.LogInformation("Saves detach of player {PlayerId}: ok in {Duration} (attempt {Attempt})", playerId, took, attempt);
                return false;
            case HelperResultKind.Timeout:
                _logger.LogWarning("Saves detach of player {PlayerId}: no answer within the detach timeout; the helper still finishes it", playerId);
                return false;
            case HelperResultKind.Error:
                switch (result.Code)
                {
                    case HelperErrorCode.OverLimit:
                        _logger.LogWarning("Saves detach of player {PlayerId}: overLimit ({Message}); nothing sent, the server keeps the previous version", playerId, result.Message);
                        return false;
                    case HelperErrorCode.NotAttached or HelperErrorCode.MasterMode:
                        _logger.LogInformation("Saves detach of player {PlayerId}: {Code} ({Message}); nothing to send", playerId, result.Describe(), result.Message);
                        return false;
                    case HelperErrorCode.BadRequest or HelperErrorCode.ProfileNotFound:
                        _logger.LogError("Saves detach of player {PlayerId}: {Code} ({Message}); nothing sent, the session's progress is lost", playerId, result.Describe(), result.Message);
                        return false;
                    default:
                        // serverUnavailable, disabled, internal: the attach still holds, a retry can succeed.
                        _logger.LogWarning("Saves detach of player {PlayerId}: {Code} ({Message}), attempt {Attempt}; retrying", playerId, result.Describe(), result.Message, attempt);
                        return true;
                }

            default:
                // No channel, not the helper, a broken connection: the helper may be restarting.
                _logger.LogWarning("Saves detach of player {PlayerId}: {Result} ({Message}), attempt {Attempt}; retrying", playerId, result.Describe(), result.Message, attempt);
                return true;
        }
    }

    private async Task PingAsync()
    {
        try
        {
            HelperResult result = await _channel.SendAsync(HelperProtocol.Ping(), TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            if (result.IsOk)
            {
                _logger.LogInformation("Saves: diskless helper {Version} answers on {Pipe}", result.Version ?? "?", HelperProtocol.PipeName);
            }
            else
            {
                _logger.LogWarning("Saves: diskless helper does not answer on {Pipe}: {Result} ({Message}); players' saves will be unavailable until it does (helper 1.7.0 or newer)", HelperProtocol.PipeName, result.Describe(), result.Message);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Saves: helper ping failed");
        }
    }

    private void SetStateLocked(PlayerSavesState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        if (_events is not null)
        {
            // Chained under the lock: the Shell hears the states in the order they happened (never "ready" before
            // "loading"), and never from the caller's thread.
            _publishing = _publishing.ContinueWith(_ => PublishAsync(state), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task PublishAsync(PlayerSavesState state)
    {
        try
        {
            await _events!.PublishAsync(IpcMessages.Events.SavesStatus, new PlayerSavesStatus(state), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "saves.status not delivered to the Shell");
        }
    }

    private bool SafeHelperInstalled()
    {
        try
        {
            return _helperInstalled();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Saves: helper service probe failed");
            return false;
        }
    }

    private static bool HelperServiceInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + HelperProtocol.ServiceName);
        return key is not null;
    }

    /// <summary>One player's saves on this PC: attach sent, detach not finished.</summary>
    private sealed class Attachment(string playerId)
    {
        public string PlayerId { get; } = playerId;

        /// <summary>Completes when the attach has its answer (any outcome); a game launch waits on it.</summary>
        public TaskCompletionSource AttachDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Release { get; set; }

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
