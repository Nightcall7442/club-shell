using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Sessions;

/// <summary>Kind of <see cref="SessionEvent"/>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SessionEventType>))]
public enum SessionEventType
{
    /// <summary>Session started.</summary>
    Started,

    /// <summary>Paused.</summary>
    Paused,

    /// <summary>Resumed.</summary>
    Resumed,

    /// <summary>Extended; data = <see cref="SessionExtendedData"/>.</summary>
    Extended,

    /// <summary>Time warning emitted; data = <see cref="SessionWarningData"/>.</summary>
    Warning,

    /// <summary>Locked.</summary>
    Locked,

    /// <summary>Unlocked.</summary>
    Unlocked,

    /// <summary>Ended; data = <see cref="SessionEndedData"/>.</summary>
    Ended,

    /// <summary>Charged; data = <see cref="SessionChargedData"/>.</summary>
    Charged,
}

/// <summary>Why a session ended.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SessionEndReason>))]
public enum SessionEndReason
{
    /// <summary>User ended it.</summary>
    User,

    /// <summary>Prepaid time ran out.</summary>
    TimeUp,

    /// <summary>Ended by an admin.</summary>
    Admin,

    /// <summary>Idle timeout.</summary>
    Idle,

    /// <summary>Agent restarted and could not resume.</summary>
    AgentRestart,

    /// <summary>Internal error.</summary>
    Error,
}

/// <summary>Audit/replay event of a session (IPC_PROTOCOL.md §6.21). <paramref name="Data"/> shape depends on <paramref name="Type"/>.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="Type">Kind.</param>
/// <param name="At">Event time.</param>
/// <param name="Data">Typed extra data (<see cref="SessionExtendedData"/>, <see cref="SessionWarningData"/>, <see cref="SessionChargedData"/>, <see cref="SessionEndedData"/>) or <see langword="null"/>.</param>
public sealed record SessionEvent(
    Guid SessionId,
    SessionEventType Type,
    DateTimeOffset At,
    JsonElement? Data = null)
{
    /// <summary>Creates an event without data.</summary>
    public static SessionEvent Of(Guid sessionId, SessionEventType type, DateTimeOffset at) => new(sessionId, type, at);

    /// <summary>Creates an <see cref="SessionEventType.Extended"/> event.</summary>
    public static SessionEvent Extended(Guid sessionId, DateTimeOffset at, int minutes, Money cost) =>
        new(sessionId, SessionEventType.Extended, at, JsonDefaults.ToElement(new SessionExtendedData(minutes, cost)));

    /// <summary>Creates a <see cref="SessionEventType.Warning"/> event.</summary>
    public static SessionEvent Warning(Guid sessionId, DateTimeOffset at, int minutesLeft) =>
        new(sessionId, SessionEventType.Warning, at, JsonDefaults.ToElement(new SessionWarningData(minutesLeft)));

    /// <summary>Creates a <see cref="SessionEventType.Charged"/> event.</summary>
    public static SessionEvent Charged(Guid sessionId, DateTimeOffset at, Money amount) =>
        new(sessionId, SessionEventType.Charged, at, JsonDefaults.ToElement(new SessionChargedData(amount)));

    /// <summary>Creates an <see cref="SessionEventType.Ended"/> event.</summary>
    public static SessionEvent Ended(Guid sessionId, DateTimeOffset at, SessionEndReason reason) =>
        new(sessionId, SessionEventType.Ended, at, JsonDefaults.ToElement(new SessionEndedData(reason)));

    /// <summary>Deserializes <see cref="Data"/> as <typeparamref name="TData"/>; <see langword="null"/> when absent.</summary>
    public TData? DataAs<TData>() where TData : class =>
        Data is { ValueKind: JsonValueKind.Object } d ? JsonDefaults.FromElement<TData>(d) : null;
}

/// <summary><c>data</c> of <see cref="SessionEventType.Extended"/>.</summary>
/// <param name="Minutes">Minutes added.</param>
/// <param name="Cost">Amount charged for the extension.</param>
public sealed record SessionExtendedData(int Minutes, Money Cost);

/// <summary><c>data</c> of <see cref="SessionEventType.Warning"/>.</summary>
/// <param name="MinutesLeft">Minutes remaining at the time of the warning.</param>
public sealed record SessionWarningData(int MinutesLeft);

/// <summary><c>data</c> of <see cref="SessionEventType.Charged"/>.</summary>
/// <param name="Amount">Amount charged.</param>
public sealed record SessionChargedData(Money Amount);

/// <summary><c>data</c> of <see cref="SessionEventType.Ended"/>.</summary>
/// <param name="Reason">End reason.</param>
public sealed record SessionEndedData(SessionEndReason Reason);

/// <summary>Settlement returned by <c>session.end</c> and <c>POST /sessions/{id}/end</c>.</summary>
/// <param name="Session">Final session (state <see cref="SessionState.Ended"/>).</param>
/// <param name="Charged">Amount charged at settlement (postpaid).</param>
/// <param name="Refunded">Amount refunded for unused prepaid time.</param>
public sealed record SessionEndResult(
    Session Session,
    Money Charged,
    Money Refunded);

/// <summary>Payload of the <c>session.ended</c> IPC event and the <c>sessionEnded</c> agent event.</summary>
/// <param name="Session">Final session.</param>
/// <param name="Reason">End reason.</param>
/// <param name="Charged">Amount charged at settlement.</param>
public sealed record SessionEndedEvent(
    Session Session,
    SessionEndReason Reason,
    Money Charged);

/// <summary>Payload of the <c>sessionStarted</c> agent event.</summary>
/// <param name="Session">New session.</param>
public sealed record SessionStartedEvent(Session Session);

/// <summary>Body of <c>POST /sessions</c> (SERVER_API.md §4.5). Sent with an <c>Idempotency-Key</c>.</summary>
/// <param name="PcId">PC.</param>
/// <param name="UserId">Player.</param>
/// <param name="TariffId">Tariff.</param>
/// <param name="Minutes">Minutes to buy; required unless the tariff is a package.</param>
/// <param name="Prepaid">Charge now vs open-ended postpaid.</param>
/// <param name="StartedAt">Actual start for offline-created sessions (≤ <c>maxOfflineMinutes</c> in the past).</param>
/// <param name="ClientSessionId">Agent-generated id used offline; adopted by the server when free.</param>
public sealed record SessionCreateRequest(
    Guid PcId,
    Guid UserId,
    Guid TariffId,
    int? Minutes,
    bool Prepaid,
    DateTimeOffset? StartedAt = null,
    Guid? ClientSessionId = null);

/// <summary>Body of <c>POST /sessions/{id}/end</c>.</summary>
/// <param name="Reason">End reason.</param>
/// <param name="SecondsUsed">Seconds consumed as measured by the Agent.</param>
/// <param name="EndedAt">Actual end time (offline replay).</param>
public sealed record SessionEndReport(
    SessionEndReason Reason,
    int SecondsUsed,
    DateTimeOffset? EndedAt = null);

/// <summary>Body of <c>POST /sessions/{id}/events</c> (≤ 100 events; offline replay and lock/unlock/warning audit).</summary>
/// <param name="Events">Events in chronological order.</param>
public sealed record SessionEventsBatch(IReadOnlyList<SessionEvent> Events)
{
    /// <summary>Maximum events per batch.</summary>
    public const int MaxEvents = 100;
}
