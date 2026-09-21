using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Sessions;

/// <summary>Session state machine (IPC_PROTOCOL.md §6.1, §9.2).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SessionState>))]
public enum SessionState
{
    /// <summary>No session.</summary>
    Idle,

    /// <summary>Being created on the server / offline store.</summary>
    Starting,

    /// <summary>Timer running.</summary>
    Active,

    /// <summary>Paused by the user; timer stopped.</summary>
    Paused,

    /// <summary>Locked (user away / admin); timer keeps running unless the tariff pauses on lock.</summary>
    Locked,

    /// <summary>Time is up; grace period before forced end.</summary>
    Ending,

    /// <summary>Finished and settled.</summary>
    Ended,
}

/// <summary>Helpers over <see cref="SessionState"/>.</summary>
public static class SessionStateExtensions
{
    /// <summary><see langword="true"/> for states in which a session exists and is not finished (<see cref="SessionState.Starting"/> .. <see cref="SessionState.Ending"/>).</summary>
    public static bool IsOpen(this SessionState state) => state is not (SessionState.Idle or SessionState.Ended);

    /// <summary><see langword="true"/> when session-scoped IPC requests are allowed (<see cref="SessionState.Active"/> or <see cref="SessionState.Paused"/>).</summary>
    public static bool AllowsSessionRequests(this SessionState state) => state is SessionState.Active or SessionState.Paused;

    /// <summary><see langword="true"/> when the timer is counting down.</summary>
    public static bool IsTimerRunning(this SessionState state) => state is SessionState.Active or SessionState.Locked or SessionState.Ending;
}

/// <summary>Play session (IPC_PROTOCOL.md §6.4).</summary>
/// <param name="Id">Session id (client-generated when created offline).</param>
/// <param name="UserId">Player.</param>
/// <param name="PcId">PC.</param>
/// <param name="State">State.</param>
/// <param name="StartedAt">Start time.</param>
/// <param name="EndsAt">Scheduled end; <see langword="null"/> while <see cref="SessionState.Idle"/>/<see cref="SessionState.Starting"/> or for open-ended postpaid.</param>
/// <param name="PausedAt">Set while paused/locked.</param>
/// <param name="TariffId">Tariff in effect.</param>
/// <param name="SecondsLeft">Remaining seconds; −1 for open-ended postpaid.</param>
/// <param name="SecondsUsed">Consumed seconds.</param>
/// <param name="Cost">Cost accrued so far.</param>
/// <param name="IsPrepaid">Prepaid (charged up front) vs postpaid.</param>
/// <param name="WarningsSent">Minute marks already emitted as <c>session.warning</c>, e.g. <c>[10, 5]</c>.</param>
public sealed record Session(
    Guid Id,
    Guid UserId,
    Guid PcId,
    SessionState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? PausedAt,
    Guid TariffId,
    int SecondsLeft,
    int SecondsUsed,
    Money Cost,
    bool IsPrepaid,
    IReadOnlyList<int> WarningsSent)
{
    /// <summary>Value of <see cref="SecondsLeft"/> for open-ended postpaid sessions.</summary>
    public const int OpenEnded = -1;

    /// <summary><see langword="true"/> when the session has no fixed end (<see cref="SecondsLeft"/> == <see cref="OpenEnded"/>).</summary>
    [JsonIgnore]
    public bool IsOpenEnded => SecondsLeft == OpenEnded;
}

/// <summary>Payload of the <c>session.warning</c> event, emitted at each configured minute mark and once at 0.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="MinutesLeft">Whole minutes remaining (0 at expiry).</param>
/// <param name="SecondsLeft">Seconds remaining.</param>
/// <param name="EndsAt">Scheduled end.</param>
public sealed record SessionWarning(
    Guid SessionId,
    int MinutesLeft,
    int SecondsLeft,
    DateTimeOffset EndsAt);
