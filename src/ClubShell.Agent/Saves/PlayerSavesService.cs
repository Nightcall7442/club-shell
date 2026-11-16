using System.Text.Json;
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
    /// <see langword="true"/> from the attach until its detach has finished: a reboot must wait (the helper collects the
    /// saves from the profile).
    /// </summary>
    bool Busy { get; }

    /// <summary>
    /// A successful <c>auth.login</c>: starts laying the player's saves out and returns at once. Never for a guest
    /// (<paramref name="guest"/>, or the user's role). The same player signing in again changes nothing.
    /// <paramref name="handoff"/> (SHELL_CHANGES 92): the sign-in ended the player's session on another PC, which is
    /// collecting their saves; the attach waits for it (state <see cref="PlayerSavesState.Transferring"/>), at most the
    /// detach timeout + 20 s, then goes out anyway.
    /// </summary>
    void SignedIn(User user, bool guest = false, Func<CancellationToken, Task>? handoff = null);

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

    /// <summary>
    /// At Agent start, after the sign-in restore (SHELL_CHANGES 91): the attachment kept on disk is taken back for
    /// <paramref name="restored"/> — the player whose session goes on — without a new <c>profile.attach</c> (the helper
    /// still holds it, and a new attach would lay the server's copy over the progress made before the restart). One of
    /// another player, or with nobody restored, is collected now (<c>profile.detach</c>).
    /// </summary>
    void RestoreAfterStart(User? restored);
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
/// <item>what was laid out is also kept on disk (<c>cache\saves-attachment.json</c>: the player, the kiosk SID, when):
/// an Agent restarted mid-session takes it back with the player's sign-in (<see cref="RestoreAfterStart"/>, no second
/// attach) and detaches at the session end as usual (SHELL_CHANGES 91).</item>
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

    /// <summary>File of the attachment the helper holds, under the persistent cache.</summary>
    public const string StateFileName = "saves-attachment.json";
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

    /// <summary>Pause before repeating a command the helper answered with <c>updating</c> (it is replacing its executable).</summary>
    public TimeSpan UpdatingRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

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

    /// <inheritdoc />
    public bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    private PlayerSavesSettings Config => _settings.CurrentValue.Games.PlayerSaves;

    private TimeSpan AttachTimeout => TimeSpan.FromSeconds(Config.AttachTimeoutSec);

    private TimeSpan DetachTimeout => TimeSpan.FromSeconds(Config.DetachTimeoutSec);

    /// <summary>How long the attach waits for the previous PC to hand the saves back (SHELL_CHANGES 92).</summary>
    private TimeSpan HandoffTimeout => DetachTimeout + TimeSpan.FromSeconds(20);

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
    public void SignedIn(User user, bool guest = false, Func<CancellationToken, Task>? handoff = null)
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
            attachment = new Attachment(playerId) { Handoff = handoff };
            _current = attachment;
            SetStateLocked(handoff is null ? PlayerSavesState.Loading : PlayerSavesState.Transferring);
        }

        Persist(attachment);

        // Off the lock and off the IPC thread; a launch waits on attachment.AttachDone, set before this runs.
        _ = Task.Run(() => AttachAsync(attachment, previous));
    }

    /// <inheritdoc />
    public async Task WaitForAttachAsync(CancellationToken cancellationToken)
    {
        Task? attach;
        TimeSpan budget;
        lock (_gate)
        {
            attach = _current is { Release: null } current ? current.AttachDone.Task : null;
            budget = _current is { Handoff: not null } ? AttachTimeout + HandoffTimeout : AttachTimeout;
        }

        if (attach is null || attach.IsCompleted)
        {
            return;
        }

        try
        {
            await attach.WaitAsync(budget + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Saves: the launch stopped waiting for the attach after {Timeout}", budget);
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
    public void RestoreAfterStart(User? restored)
    {
        Persisted? saved = LoadPersisted();
        if (saved is null)
        {
            return;
        }

        var attachment = new Attachment(saved.PlayerId);
        attachment.AttachDone.TrySetResult();
        bool same = restored is not null && restored.Role != UserRole.Guest
            && string.Equals(HelperProtocol.PlayerId(restored.Id), saved.PlayerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(saved.Sid, _kiosk.Sid, StringComparison.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_current is not null)
            {
                return;
            }

            _current = attachment;
            if (same)
            {
                SetStateLocked(PlayerSavesState.Ready);
            }
            else
            {
                StartReleaseLocked(attachment, SessionEndReason.AgentRestart, closeGames: false);
            }
        }

        if (same)
        {
            _logger.LogWarning("Saves: attachment of player {PlayerId} (attached {At:O}) restored from disk after an Agent restart; no new attach", saved.PlayerId, saved.AttachedAt);
        }
        else
        {
            _logger.LogWarning("Saves: attachment of player {PlayerId} found on disk without that player's session; detaching it now", saved.PlayerId);
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

        if (attachment.Handoff is { } handoff)
        {
            await WaitHandoffAsync(attachment, handoff).ConfigureAwait(false);
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
            // A missing channel is retried by the channel itself; "updating" (helper 1.8.0+ replacing its exe) here,
            // every 5 s, both within the attach timeout.
            while (true)
            {
                TimeSpan remaining = AttachTimeout - _clock.GetElapsedTime(started);
                try
                {
                    result = await _channel.SendAsync(HelperProtocol.Attach(attachment.PlayerId, sid), remaining, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    result = new HelperResult(HelperResultKind.Failed, Message: ex.Message);
                }

                if (result is not { Kind: HelperResultKind.Error, Code: HelperErrorCode.Updating })
                {
                    break;
                }

                remaining = AttachTimeout - _clock.GetElapsedTime(started);
                if (remaining <= UpdatingRetryDelay)
                {
                    result = new HelperResult(HelperResultKind.Timeout, Message: "the helper was still updating when the attach timeout ran out");
                    break;
                }

                _logger.LogInformation("Saves attach of player {PlayerId}: the helper is updating; retrying in {Delay}", attachment.PlayerId, UpdatingRetryDelay);
                await _clock.Delay(UpdatingRetryDelay, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>The previous PC collects the player's saves first; then the attach (state back to loading).</summary>
    private async Task WaitHandoffAsync(Attachment attachment, Func<CancellationToken, Task> handoff)
    {
        long started = _clock.GetTimestamp();
        using var budget = new CancellationTokenSource(HandoffTimeout);
        try
        {
            await handoff(budget.Token).WaitAsync(HandoffTimeout).ConfigureAwait(false);
            _logger.LogInformation("Saves of player {PlayerId}: the previous PC handed them back after {Elapsed}; attaching", attachment.PlayerId, _clock.GetElapsedTime(started));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Saves of player {PlayerId}: the previous PC did not hand them back within {Timeout}; attaching anyway, its last progress may be missing", attachment.PlayerId, HandoffTimeout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Saves of player {PlayerId}: waiting for the previous PC failed; attaching anyway", attachment.PlayerId);
        }

        lock (_gate)
        {
            if (ReferenceEquals(_current, attachment) && attachment.Release is null)
            {
                SetStateLocked(PlayerSavesState.Loading);
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
                TimeSpan wait = AttachTimeout + TimeSpan.FromSeconds(5) + (attachment.Handoff is null ? TimeSpan.Zero : HandoffTimeout);
                await attachment.AttachDone.Task.WaitAsync(wait).ConfigureAwait(false);
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

                    Forget(attachment);
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

            TimeSpan pause = result is { Kind: HelperResultKind.Error, Code: HelperErrorCode.Updating } ? UpdatingRetryDelay : RetryDelay;
            remaining = budget - _clock.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                await _clock.Delay(remaining < pause ? remaining : pause, CancellationToken.None).ConfigureAwait(false);
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
                    case HelperErrorCode.Updating:
                        _logger.LogInformation("Saves detach of player {PlayerId}: the helper is updating, attempt {Attempt}; retrying in {Delay}", playerId, attempt, UpdatingRetryDelay);
                        return true;
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
                _logger.LogInformation(
                    "Saves: diskless helper {Version} (build {Build}{Updating}) answers on {Pipe}",
                    result.Version ?? "?",
                    result.Build ?? "?",
                    result.Updating ? ", updating" : string.Empty,
                    HelperProtocol.PipeName);
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
    private string StatePath
    {
        get
        {
            AgentSettings settings = _settings.CurrentValue;
            return Path.Combine(settings.ResolvePersistentPath(settings.Paths.Cache), StateFileName);
        }
    }

    private void Persist(Attachment attachment)
    {
        try
        {
            string path = StatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Persisted(attachment.PlayerId, _kiosk.Sid, _clock.UtcNow)));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Saves: attachment of player {PlayerId} not kept on disk; an Agent restart would forget it", attachment.PlayerId);
        }
    }

    private void Forget(Attachment attachment)
    {
        try
        {
            string path = StatePath;
            if (LoadPersisted() is { } saved && string.Equals(saved.PlayerId, attachment.PlayerId, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Saves: attachment file not deleted");
        }
    }

    private Persisted? LoadPersisted()
    {
        try
        {
            string path = StatePath;
            return File.Exists(path) ? JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path)) is { PlayerId.Length: > 0 } p ? p : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Saves: attachment file unreadable; ignored");
            return null;
        }
    }

    private sealed record Persisted(string PlayerId, string Sid, DateTimeOffset AttachedAt);

    private sealed class Attachment(string playerId)
    {
        public string PlayerId { get; } = playerId;

        /// <summary>Completes when the attach has its answer (any outcome); a game launch waits on it.</summary>
        public TaskCompletionSource AttachDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Release { get; set; }

        /// <summary>The previous PC hands the saves back first (SHELL_CHANGES 92).</summary>
        public Func<CancellationToken, Task>? Handoff { get; init; }

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
