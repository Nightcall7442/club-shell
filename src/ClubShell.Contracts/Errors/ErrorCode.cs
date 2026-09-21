using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Serialization;

namespace ClubShell.Contracts.Errors;

/// <summary>
/// Error codes shared verbatim by the IPC protocol (<c>IpcError.code</c>), the central server REST error
/// envelope (<c>error.code</c>) and the Tauri <c>ShellError.code</c>. Wire literal is camelCase
/// (<see cref="Serialization.CamelCaseEnumConverter{TEnum}"/>). See IPC_PROTOCOL.md §5.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ErrorCode>))]
public enum ErrorCode
{
    /// <summary>Not authenticated / bad token / bad credentials (HTTP 401).</summary>
    Unauthorized,

    /// <summary>Authenticated but not allowed: role, SID, capability (HTTP 403).</summary>
    Forbidden,

    /// <summary>Entity or message name unknown (HTTP 404).</summary>
    NotFound,

    /// <summary>Payload invalid; <c>details.field</c> / <c>details.reason</c> (HTTP 400).</summary>
    Validation,

    /// <summary>State conflict: duplicate, out of stock, already joined (HTTP 409).</summary>
    Conflict,

    /// <summary>Balance too low; <c>details: { required: Money, available: Money }</c> (HTTP 402).</summary>
    InsufficientFunds,

    /// <summary>Operation needs an active/paused session (HTTP 409).</summary>
    SessionNotActive,

    /// <summary><c>session.start</c> while a session already exists (HTTP 409).</summary>
    SessionAlreadyActive,

    /// <summary>Game not installed or install path missing (HTTP 409).</summary>
    GameNotInstalled,

    /// <summary>Launcher error; <c>details: { stage, exitCode?, stderr? }</c> (HTTP 500).</summary>
    GameLaunchFailed,

    /// <summary>No free pooled account (HTTP 409).</summary>
    AccountPoolExhausted,

    /// <summary>Anti-cheat prerequisite failed; <c>details: { kind, reason }</c> (HTTP 403).</summary>
    AntiCheatBlocked,

    /// <summary>Blocked by policy; <c>details: { rule }</c> (HTTP 403).</summary>
    PolicyDenied,

    /// <summary>Agent has no server connection and the operation cannot be served from cache (HTTP 503).</summary>
    AgentOffline,

    /// <summary>Server returned 5xx or the circuit breaker is open (HTTP 503).</summary>
    ServerUnavailable,

    /// <summary>Upstream or launch timeout (HTTP 504).</summary>
    Timeout,

    /// <summary>Too many requests; <c>details.retryAfterSec</c> (HTTP 429).</summary>
    RateLimited,

    /// <summary>Unexpected exception; <c>details.traceId</c> (HTTP 500).</summary>
    Internal,

    /// <summary>Envelope / framing violation (HTTP 400).</summary>
    ProtocolError,

    /// <summary>Protocol or application version unsupported; <c>details: { supported: [..], got }</c> (HTTP 426).</summary>
    VersionMismatch,
}

/// <summary>Static metadata about <see cref="ErrorCode"/> values: descriptions, HTTP mapping, retry semantics.</summary>
public static class ErrorCodes
{
    /// <summary>Human-readable English description of <paramref name="code"/> (safe for logs; UI localizes by code).</summary>
    public static string Describe(this ErrorCode code) => code switch
    {
        ErrorCode.Unauthorized => "Not authenticated, bad token or bad credentials",
        ErrorCode.Forbidden => "Authenticated but not allowed",
        ErrorCode.NotFound => "Entity or message name unknown",
        ErrorCode.Validation => "Payload invalid",
        ErrorCode.Conflict => "State conflict",
        ErrorCode.InsufficientFunds => "Balance too low",
        ErrorCode.SessionNotActive => "Operation requires an active or paused session",
        ErrorCode.SessionAlreadyActive => "A session is already active",
        ErrorCode.GameNotInstalled => "Game is not installed",
        ErrorCode.GameLaunchFailed => "Game launcher failed",
        ErrorCode.AccountPoolExhausted => "No free pooled account",
        ErrorCode.AntiCheatBlocked => "Anti-cheat prerequisite failed",
        ErrorCode.PolicyDenied => "Blocked by policy",
        ErrorCode.AgentOffline => "Agent is offline and cannot serve the request from cache",
        ErrorCode.ServerUnavailable => "Server unavailable",
        ErrorCode.Timeout => "Operation timed out",
        ErrorCode.RateLimited => "Rate limit exceeded",
        ErrorCode.Internal => "Internal error",
        ErrorCode.ProtocolError => "Protocol violation",
        ErrorCode.VersionMismatch => "Version unsupported",
        _ => "Unknown error",
    };

    /// <summary>HTTP status equivalent of <paramref name="code"/> (IPC_PROTOCOL.md §5, "HTTP equiv").</summary>
    public static int ToHttpStatus(this ErrorCode code) => code switch
    {
        ErrorCode.Unauthorized => 401,
        ErrorCode.Forbidden => 403,
        ErrorCode.NotFound => 404,
        ErrorCode.Validation => 400,
        ErrorCode.Conflict => 409,
        ErrorCode.InsufficientFunds => 402,
        ErrorCode.SessionNotActive => 409,
        ErrorCode.SessionAlreadyActive => 409,
        ErrorCode.GameNotInstalled => 409,
        ErrorCode.GameLaunchFailed => 500,
        ErrorCode.AccountPoolExhausted => 409,
        ErrorCode.AntiCheatBlocked => 403,
        ErrorCode.PolicyDenied => 403,
        ErrorCode.AgentOffline => 503,
        ErrorCode.ServerUnavailable => 503,
        ErrorCode.Timeout => 504,
        ErrorCode.RateLimited => 429,
        ErrorCode.Internal => 500,
        ErrorCode.ProtocolError => 400,
        ErrorCode.VersionMismatch => 426,
        _ => 500,
    };

    /// <summary>
    /// Best-effort reverse mapping used when a server response has no parseable error envelope.
    /// Ambiguous statuses map to the generic member (409 → <see cref="ErrorCode.Conflict"/>, 5xx → <see cref="ErrorCode.ServerUnavailable"/>).
    /// </summary>
    public static ErrorCode FromHttpStatus(int status) => status switch
    {
        400 => ErrorCode.Validation,
        401 => ErrorCode.Unauthorized,
        402 => ErrorCode.InsufficientFunds,
        403 => ErrorCode.Forbidden,
        404 => ErrorCode.NotFound,
        408 => ErrorCode.Timeout,
        409 => ErrorCode.Conflict,
        426 => ErrorCode.VersionMismatch,
        429 => ErrorCode.RateLimited,
        504 => ErrorCode.Timeout,
        >= 500 => ErrorCode.ServerUnavailable,
        _ => ErrorCode.Internal,
    };

    /// <summary>
    /// <see langword="true"/> when the same request may succeed if repeated later without changes
    /// (transient transport/availability conditions). Callers still honour <c>details.retryAfterSec</c>.
    /// </summary>
    public static bool IsRetryable(this ErrorCode code) => code switch
    {
        ErrorCode.Timeout => true,
        ErrorCode.RateLimited => true,
        ErrorCode.ServerUnavailable => true,
        ErrorCode.AgentOffline => true,
        _ => false,
    };

    /// <summary><see langword="true"/> for codes that mean the caller must (re)authenticate.</summary>
    public static bool IsAuthFailure(this ErrorCode code) => code is ErrorCode.Unauthorized or ErrorCode.Forbidden;
}

/// <summary>Body of every non-2xx central-server response: <c>{ "error": ServerError }</c> (SERVER_API.md §3).</summary>
/// <param name="Error">The error.</param>
public sealed record ServerErrorEnvelope(ServerError Error);

/// <summary>Central-server error object. Maps 1:1 onto <see cref="IpcError"/> via <see cref="ToIpcError"/>.</summary>
/// <param name="Code">Error code.</param>
/// <param name="Message">Human-readable English message.</param>
/// <param name="Details">Structured extra data; key always present (<see langword="null"/> when none).</param>
/// <param name="TraceId">Server trace id (echo of <c>X-Trace-Id</c>).</param>
public sealed record ServerError(
    ErrorCode Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Details,
    string TraceId)
{
    /// <summary>Converts to an <see cref="IpcError"/>; <see cref="TraceId"/> is moved into <c>details.traceId</c>.</summary>
    public IpcError ToIpcError() => new(Code, Message, IpcError.WithTraceId(Details, TraceId));
}
