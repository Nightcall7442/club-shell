using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Serialization;

namespace ClubShell.Contracts.Games;

/// <summary>Lifecycle state of a launched game process.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<GameState>))]
public enum GameState
{
    /// <summary>Launcher invoked; waiting for the game window/process.</summary>
    Launching,

    /// <summary>Game process running.</summary>
    Running,

    /// <summary>Exited normally.</summary>
    Exited,

    /// <summary>Launch failed.</summary>
    Failed,

    /// <summary>Killed by the Agent (session end, admin, policy).</summary>
    Killed,
}

/// <summary>Helpers over <see cref="GameState"/>.</summary>
public static class GameStateExtensions
{
    /// <summary><see langword="true"/> for terminal states (<see cref="GameState.Exited"/>, <see cref="GameState.Failed"/>, <see cref="GameState.Killed"/>).</summary>
    public static bool IsTerminal(this GameState state) => state is GameState.Exited or GameState.Failed or GameState.Killed;
}

/// <summary>Screen resolution requested for a launch.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public sealed record Resolution(
    int Width,
    int Height);

/// <summary>Full launch request as executed by the Agent (IPC_PROTOCOL.md §6.9); also the payload of the <c>launchGame</c> server command.</summary>
/// <param name="GameId">Game to launch.</param>
/// <param name="SessionId">Active session.</param>
/// <param name="UserId">Player.</param>
/// <param name="UseAccountPool">Lease a pooled account and inject credentials.</param>
/// <param name="AccountLeaseId">Reuse an existing lease.</param>
/// <param name="ExtraArgs">Extra command line (policy-sanitized).</param>
/// <param name="Resolution">Requested resolution.</param>
/// <param name="LaunchTimeoutSec">Seconds to wait for the game process/window before failing.</param>
public sealed record LaunchRequest(
    Guid GameId,
    Guid SessionId,
    Guid UserId,
    bool UseAccountPool,
    Guid? AccountLeaseId,
    string? ExtraArgs,
    Resolution? Resolution,
    int LaunchTimeoutSec);

/// <summary>Outcome of a launch (IPC_PROTOCOL.md §6.9). Over IPC failures are returned as errors, so <paramref name="Ok"/> is always <see langword="true"/> there.</summary>
/// <param name="Ok">Success flag.</param>
/// <param name="Pid">Game process id when started.</param>
/// <param name="StartedAt">Launch time.</param>
/// <param name="AccountLeaseId">Account-pool lease used, when any.</param>
/// <param name="Error">Failure details when <paramref name="Ok"/> is <see langword="false"/>.</param>
public sealed record LaunchResult(
    bool Ok,
    int? Pid,
    DateTimeOffset StartedAt,
    Guid? AccountLeaseId,
    IpcError? Error = null)
{
    /// <summary>Successful result.</summary>
    public static LaunchResult Success(int pid, DateTimeOffset startedAt, Guid? accountLeaseId = null) =>
        new(true, pid, startedAt, accountLeaseId, null);

    /// <summary>Failed result.</summary>
    public static LaunchResult Failure(IpcError error, DateTimeOffset startedAt, Guid? accountLeaseId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(false, null, startedAt, accountLeaseId, error);
    }
}

/// <summary>Game process tracked by the Agent (IPC_PROTOCOL.md §6.9).</summary>
/// <param name="GameId">Game id.</param>
/// <param name="Title">Game title.</param>
/// <param name="Pid">Process id.</param>
/// <param name="StartedAt">Launch time.</param>
/// <param name="AccountLeaseId">Account-pool lease in use, when any.</param>
/// <param name="State">Current state.</param>
public sealed record RunningGame(
    Guid GameId,
    string Title,
    int Pid,
    DateTimeOffset StartedAt,
    Guid? AccountLeaseId,
    GameState State);

/// <summary>Pre-signed download of a user's cloud-save bundle.</summary>
/// <param name="Url">Pre-signed HTTPS URL.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the bundle.</param>
/// <param name="SizeBytes">Bundle size.</param>
public sealed record CloudSaveDownload(
    string Url,
    string Sha256,
    long SizeBytes);

/// <summary>
/// Account-pool lease (<c>GET /games/{id}/accounts/lease</c>, SERVER_API.md §4.6). <paramref name="Secret"/> is
/// AES-256-GCM encrypted with a per-agent key derived from the signing secret (HKDF-SHA256, info <c>account-pool</c>),
/// base64 <c>nonce||ciphertext||tag</c>. Never logged.
/// </summary>
/// <param name="LeaseId">Lease id.</param>
/// <param name="Launcher">Launcher the credentials belong to.</param>
/// <param name="Username">Account login.</param>
/// <param name="Secret">Encrypted password or token.</param>
/// <param name="Extra">Launcher-specific extras (<c>steamGuardSecret</c>, <c>authenticatorSeed</c>, <c>region</c>).</param>
/// <param name="ExpiresAt">Lease TTL.</param>
/// <param name="CloudSave">Save bundle to restore before launch, when any.</param>
public sealed record AccountLease(
    Guid LeaseId,
    LauncherType Launcher,
    string Username,
    string Secret,
    JsonElement? Extra,
    DateTimeOffset ExpiresAt,
    CloudSaveDownload? CloudSave = null);

/// <summary>Why an account-pool lease is released.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AccountLeaseReleaseReason>))]
public enum AccountLeaseReleaseReason
{
    /// <summary>Game exited.</summary>
    Exit,

    /// <summary>Session ended.</summary>
    SessionEnd,

    /// <summary>Launch failed.</summary>
    LaunchFailed,

    /// <summary>Manual / admin release.</summary>
    Manual,
}

/// <summary>Cloud-save bundle the Agent uploaded before releasing a lease.</summary>
/// <param name="UploadUrl">Pre-signed URL the bundle was <c>PUT</c> to (from <see cref="SaveUploadTarget"/>).</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the bundle.</param>
/// <param name="SizeBytes">Bundle size.</param>
public sealed record CloudSaveUpload(
    string? UploadUrl,
    string Sha256,
    long SizeBytes);

/// <summary>Body of <c>POST /games/{id}/accounts/{leaseId}/release</c>.</summary>
/// <param name="Reason">Release reason.</param>
/// <param name="CloudSave">Uploaded save bundle, when cloud save is enabled.</param>
public sealed record AccountLeaseRelease(
    AccountLeaseReleaseReason Reason,
    CloudSaveUpload? CloudSave = null);

/// <summary>Response of <c>GET /games/{id}/accounts/{leaseId}/save-upload</c>.</summary>
/// <param name="UploadUrl">Pre-signed <c>PUT</c> URL.</param>
/// <param name="ExpiresAt">URL expiry.</param>
/// <param name="MaxBytes">Maximum accepted bundle size.</param>
public sealed record SaveUploadTarget(
    string UploadUrl,
    DateTimeOffset ExpiresAt,
    long MaxBytes);

/// <summary>Which lifecycle point a <see cref="LaunchReport"/> describes.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<LaunchReportPhase>))]
public enum LaunchReportPhase
{
    /// <summary>Reported right after launch (success or failure).</summary>
    Launch,

    /// <summary>Reported when the game exits.</summary>
    Exit,
}

/// <summary>Body of <c>POST /games/{id}/launch-report</c> (SERVER_API.md §4.6).</summary>
/// <param name="SessionId">Session.</param>
/// <param name="UserId">Player.</param>
/// <param name="Result">Launch outcome.</param>
/// <param name="DurationMs">Launch latency in milliseconds.</param>
/// <param name="Launcher">Launcher used.</param>
/// <param name="AntiCheat">Pre-launch anti-cheat check.</param>
/// <param name="Phase">Lifecycle point.</param>
/// <param name="ExitCode">Process exit code (<see cref="LaunchReportPhase.Exit"/>).</param>
/// <param name="PlayedSec">Seconds played (<see cref="LaunchReportPhase.Exit"/>).</param>
public sealed record LaunchReport(
    Guid SessionId,
    Guid UserId,
    LaunchResult Result,
    int DurationMs,
    LauncherType Launcher,
    AntiCheatCheckResult AntiCheat,
    LaunchReportPhase Phase,
    int? ExitCode = null,
    int? PlayedSec = null);
