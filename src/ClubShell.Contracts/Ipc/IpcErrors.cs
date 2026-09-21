using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Ipc;

/// <summary>
/// Error carried by an IPC response envelope (IPC_PROTOCOL.md §2) and by <c>game.stateChanged</c> /
/// <c>update.progress</c> events. <paramref name="Details"/> is always present on the wire (<see langword="null"/> when empty).
/// </summary>
/// <param name="Code">Error code.</param>
/// <param name="Message">Human-readable English message, safe to display in dev; UI localizes by <paramref name="Code"/>.</param>
/// <param name="Details">Structured extra data, e.g. <see cref="ValidationDetails"/>.</param>
public sealed record IpcError(
    ErrorCode Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Details)
{
    /// <summary>Creates an error with the default description of <paramref name="code"/> and no details.</summary>
    public static IpcError Of(ErrorCode code) => new(code, code.Describe(), null);

    /// <summary>Creates an error with a custom message and no details.</summary>
    public static IpcError Of(ErrorCode code, string message) => new(code, message, null);

    /// <summary>Creates an error with typed details (serialized through <see cref="ContractsJsonContext"/>).</summary>
    public static IpcError Of<TDetails>(ErrorCode code, string message, TDetails details) =>
        new(code, message, JsonDefaults.ToElement(details));

    /// <summary><see cref="ErrorCode.Unauthorized"/>.</summary>
    public static IpcError Unauthorized(string? message = null) => Of(ErrorCode.Unauthorized, message ?? ErrorCode.Unauthorized.Describe());

    /// <summary><see cref="ErrorCode.Unauthorized"/> with <c>details.reason</c> (e.g. <c>expired</c>, <c>userToken</c>, <c>clockSkew</c>).</summary>
    public static IpcError Unauthorized(string message, string reason) => Of(ErrorCode.Unauthorized, message, new ReasonDetails(reason));

    /// <summary><see cref="ErrorCode.Forbidden"/> with optional <c>details.reason</c>.</summary>
    public static IpcError Forbidden(string? message = null, string? reason = null) =>
        reason is null
            ? Of(ErrorCode.Forbidden, message ?? ErrorCode.Forbidden.Describe())
            : Of(ErrorCode.Forbidden, message ?? ErrorCode.Forbidden.Describe(), new ReasonDetails(reason));

    /// <summary><see cref="ErrorCode.NotFound"/> for an unknown IPC message name (<c>details.name</c>).</summary>
    public static IpcError UnknownMessage(string name) => Of(ErrorCode.NotFound, $"Unknown message '{name}'", new NameDetails(name));

    /// <summary><see cref="ErrorCode.NotFound"/> for a missing entity.</summary>
    public static IpcError NotFound(string what) => Of(ErrorCode.NotFound, $"{what} not found");

    /// <summary><see cref="ErrorCode.Validation"/> with <c>details: { field, reason }</c>.</summary>
    public static IpcError Validation(string field, string reason, string? message = null) =>
        Of(ErrorCode.Validation, message ?? $"Invalid '{field}': {reason}", new ValidationDetails(field, reason));

    /// <summary><see cref="ErrorCode.Conflict"/> with optional <c>details.reason</c>.</summary>
    public static IpcError Conflict(string message, string? reason = null) =>
        reason is null ? Of(ErrorCode.Conflict, message) : Of(ErrorCode.Conflict, message, new ReasonDetails(reason));

    /// <summary><see cref="ErrorCode.InsufficientFunds"/> with <c>details: { required, available }</c>.</summary>
    public static IpcError InsufficientFunds(Money required, Money available) =>
        Of(ErrorCode.InsufficientFunds, ErrorCode.InsufficientFunds.Describe(), new InsufficientFundsDetails(required, available));

    /// <summary><see cref="ErrorCode.SessionNotActive"/>.</summary>
    public static IpcError SessionNotActive() => Of(ErrorCode.SessionNotActive);

    /// <summary><see cref="ErrorCode.SessionAlreadyActive"/>.</summary>
    public static IpcError SessionAlreadyActive() => Of(ErrorCode.SessionAlreadyActive);

    /// <summary><see cref="ErrorCode.GameNotInstalled"/>.</summary>
    public static IpcError GameNotInstalled(Guid gameId) => Of(ErrorCode.GameNotInstalled, $"Game {gameId} is not installed");

    /// <summary><see cref="ErrorCode.GameLaunchFailed"/> with <c>details: { stage, exitCode?, stderr? }</c>.</summary>
    public static IpcError GameLaunchFailed(string stage, string message, int? exitCode = null, string? stderr = null) =>
        Of(ErrorCode.GameLaunchFailed, message, new LaunchFailedDetails(stage, exitCode, stderr));

    /// <summary><see cref="ErrorCode.AccountPoolExhausted"/>.</summary>
    public static IpcError AccountPoolExhausted() => Of(ErrorCode.AccountPoolExhausted);

    /// <summary><see cref="ErrorCode.AntiCheatBlocked"/> with <c>details: { kind, reason }</c>.</summary>
    public static IpcError AntiCheatBlocked(AntiCheatKind kind, string reason) =>
        Of(ErrorCode.AntiCheatBlocked, $"Anti-cheat check failed: {reason}", new AntiCheatBlockedDetails(kind, reason));

    /// <summary><see cref="ErrorCode.PolicyDenied"/> with <c>details.rule</c> (and optional <c>details.field</c>).</summary>
    public static IpcError PolicyDenied(string rule, string? field = null) =>
        Of(ErrorCode.PolicyDenied, $"Denied by policy rule '{rule}'", new PolicyDeniedDetails(rule, field));

    /// <summary><see cref="ErrorCode.AgentOffline"/>.</summary>
    public static IpcError AgentOffline() => Of(ErrorCode.AgentOffline);

    /// <summary><see cref="ErrorCode.ServerUnavailable"/>.</summary>
    public static IpcError ServerUnavailable(string? message = null) => Of(ErrorCode.ServerUnavailable, message ?? ErrorCode.ServerUnavailable.Describe());

    /// <summary><see cref="ErrorCode.Timeout"/>.</summary>
    public static IpcError Timeout(string? message = null) => Of(ErrorCode.Timeout, message ?? ErrorCode.Timeout.Describe());

    /// <summary><see cref="ErrorCode.RateLimited"/> with <c>details.retryAfterSec</c>.</summary>
    public static IpcError RateLimited(int retryAfterSec) =>
        Of(ErrorCode.RateLimited, ErrorCode.RateLimited.Describe(), new RateLimitDetails(retryAfterSec));

    /// <summary><see cref="ErrorCode.Internal"/> with <c>details.traceId</c>. Never leaks exception text to the caller.</summary>
    public static IpcError Internal(string traceId) => Of(ErrorCode.Internal, ErrorCode.Internal.Describe(), new TraceDetails(traceId));

    /// <summary><see cref="ErrorCode.ProtocolError"/>.</summary>
    public static IpcError ProtocolError(string message) => Of(ErrorCode.ProtocolError, message);

    /// <summary><see cref="ErrorCode.VersionMismatch"/> with <c>details: { supported, got }</c>.</summary>
    public static IpcError VersionMismatch(IReadOnlyList<int> supported, int got) =>
        Of(ErrorCode.VersionMismatch, $"Protocol version {got} unsupported", new VersionMismatchDetails(supported, got));

    /// <summary><see langword="true"/> when <see cref="Code"/> is transient (<see cref="ErrorCodes.IsRetryable"/>).</summary>
    [JsonIgnore]
    public bool IsRetryable => Code.IsRetryable();

    /// <summary>Wraps this error in an <see cref="IpcException"/>.</summary>
    public IpcException ToException() => new(this);

    /// <summary>Deserializes <see cref="Details"/> as <typeparamref name="TDetails"/>, or <see langword="null"/> when absent.</summary>
    public TDetails? DetailsAs<TDetails>() where TDetails : class =>
        Details is { ValueKind: JsonValueKind.Object } d ? JsonDefaults.FromElement<TDetails>(d) : null;

    /// <summary>
    /// Returns a copy of <paramref name="details"/> with <c>traceId</c> added (or <c>{ traceId }</c> when
    /// <paramref name="details"/> is not an object). Used when mapping server errors onto IPC errors.
    /// </summary>
    public static JsonElement WithTraceId(JsonElement? details, string traceId)
    {
        ArgumentNullException.ThrowIfNull(traceId);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (details is { ValueKind: JsonValueKind.Object } obj)
            {
                foreach (var property in obj.EnumerateObject())
                {
                    if (!property.NameEquals("traceId"))
                    {
                        property.WriteTo(writer);
                    }
                }
            }

            writer.WriteString("traceId", traceId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}

/// <summary><c>details</c> for <see cref="ErrorCode.Validation"/>.</summary>
/// <param name="Field">JSON path of the offending field, e.g. <c>minutes</c> or <c>items[2].qty</c>.</param>
/// <param name="Reason">Machine-readable reason code, e.g. <c>required</c>, <c>min</c>, <c>max</c>, <c>format</c>.</param>
public sealed record ValidationDetails(string Field, string Reason);

/// <summary><c>details</c> carrying a single <c>reason</c> code (<see cref="ErrorCode.Unauthorized"/>, <see cref="ErrorCode.Forbidden"/>, <see cref="ErrorCode.Conflict"/>).</summary>
/// <param name="Reason">Reason code, e.g. <c>expired</c>, <c>banned</c>, <c>outOfStock</c>, <c>pendingApproval</c>.</param>
public sealed record ReasonDetails(string Reason);

/// <summary><c>details</c> for <see cref="ErrorCode.NotFound"/> when an IPC message name is unknown.</summary>
/// <param name="Name">The unknown message name.</param>
public sealed record NameDetails(string Name);

/// <summary><c>details</c> for <see cref="ErrorCode.RateLimited"/>.</summary>
/// <param name="RetryAfterSec">Seconds the caller should wait before retrying.</param>
public sealed record RateLimitDetails(int RetryAfterSec);

/// <summary><c>details</c> for <see cref="ErrorCode.InsufficientFunds"/>.</summary>
/// <param name="Required">Amount required by the operation.</param>
/// <param name="Available">Amount currently available.</param>
public sealed record InsufficientFundsDetails(Money Required, Money Available);

/// <summary><c>details</c> for <see cref="ErrorCode.PolicyDenied"/>.</summary>
/// <param name="Rule">Policy rule identifier, e.g. <c>ageRating</c>, <c>alreadyRunning</c>, <c>postpaidNotAllowed</c>.</param>
/// <param name="Field">Optional settings field locked by policy (<c>settings.set</c>).</param>
public sealed record PolicyDeniedDetails(string Rule, string? Field = null);

/// <summary><c>details</c> for <see cref="ErrorCode.AntiCheatBlocked"/>.</summary>
/// <param name="Kind">Anti-cheat subsystem.</param>
/// <param name="Reason">Check that failed, e.g. <c>driverMissing</c>, <c>secureBootOff</c>.</param>
public sealed record AntiCheatBlockedDetails(AntiCheatKind Kind, string Reason);

/// <summary><c>details</c> for <see cref="ErrorCode.GameLaunchFailed"/>.</summary>
/// <param name="Stage">Launch stage, e.g. <c>lease</c>, <c>inject</c>, <c>createProcess</c>, <c>waitForWindow</c>.</param>
/// <param name="ExitCode">Launcher exit code when it exited.</param>
/// <param name="Stderr">Captured stderr tail, when any.</param>
public sealed record LaunchFailedDetails(string Stage, int? ExitCode = null, string? Stderr = null);

/// <summary><c>details</c> for <see cref="ErrorCode.VersionMismatch"/>.</summary>
/// <param name="Supported">Supported protocol majors.</param>
/// <param name="Got">Version received.</param>
public sealed record VersionMismatchDetails(IReadOnlyList<int> Supported, int Got);

/// <summary><c>details</c> for <see cref="ErrorCode.Internal"/> (and any server error mapped to IPC).</summary>
/// <param name="TraceId">Trace id to correlate logs.</param>
public sealed record TraceDetails(string TraceId);

/// <summary>Exception form of <see cref="IpcError"/> for code paths that prefer throwing to returning.</summary>
public sealed class IpcException : Exception
{
    /// <summary>Creates an exception wrapping <paramref name="error"/>.</summary>
    public IpcException(IpcError error)
        : base(error?.Message)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>Creates an exception wrapping <paramref name="error"/> with an inner cause.</summary>
    public IpcException(IpcError error, Exception innerException)
        : base(error?.Message, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>Creates an exception from a code and message without details.</summary>
    public IpcException(ErrorCode code, string message)
        : this(IpcError.Of(code, message))
    {
    }

    /// <summary>The wire error.</summary>
    public IpcError Error { get; }

    /// <summary>Shortcut for <c>Error.Code</c>.</summary>
    public ErrorCode Code => Error.Code;
}
