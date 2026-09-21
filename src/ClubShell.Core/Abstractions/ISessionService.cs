using ClubShell.Contracts.Sessions;

namespace ClubShell.Core.Abstractions;

/// <summary>
/// Owner of the single play session of this PC (ARCHITECTURE.md §5.1): all mutations are serialized, the timer runs
/// on monotonic time, and every transition is published as a <see cref="SessionEvent"/> (to the Shell as IPC events,
/// to the server as agent events or offline replay).
/// </summary>
public interface ISessionService
{
    /// <summary>Current session, or <see langword="null"/> when <see cref="State"/> is <see cref="SessionState.Idle"/>.</summary>
    Session? Current { get; }

    /// <summary>Current state (<see cref="SessionState.Idle"/> when no session).</summary>
    SessionState State { get; }

    /// <summary>Remaining time; <see cref="TimeSpan.Zero"/> when idle or expired, <see cref="Timeout.InfiniteTimeSpan"/> for open-ended postpaid.</summary>
    TimeSpan TimeLeft { get; }

    /// <summary>Raised after every transition (started, paused, resumed, extended, warning, locked, unlocked, ended, charged).</summary>
    event EventHandler<SessionEvent>? Changed;

    /// <summary>Starts a session (server-side, or offline when allowed). Throws <c>sessionAlreadyActive</c> when one is open.</summary>
    Task<Session> StartAsync(SessionCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Pauses the timer.</summary>
    Task<Session> PauseAsync(string? reason, CancellationToken cancellationToken);

    /// <summary>Resumes a paused session.</summary>
    Task<Session> ResumeAsync(CancellationToken cancellationToken);

    /// <summary>Ends the session and settles it.</summary>
    Task<SessionEndResult> EndAsync(SessionEndReason reason, CancellationToken cancellationToken);

    /// <summary>Adds <paramref name="minutes"/> (optionally switching tariff).</summary>
    Task<Session> ExtendAsync(int minutes, Guid? tariffId, CancellationToken cancellationToken);

    /// <summary>Locks the screen; the timer keeps running unless the tariff pauses on lock.</summary>
    Task<Session> LockAsync(string? reason, CancellationToken cancellationToken);

    /// <summary>Unlocks a locked session (credentials already verified by the caller).</summary>
    Task<Session> UnlockAsync(CancellationToken cancellationToken);

    /// <summary>Streams session events until cancelled (used by the IPC event pump and tests).</summary>
    IAsyncEnumerable<SessionEvent> WatchAsync(CancellationToken cancellationToken);
}
