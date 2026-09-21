using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Commands;

#region Updates

/// <summary>Update channel.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<UpdateChannel>))]
public enum UpdateChannel
{
    /// <summary>Stable releases.</summary>
    Stable,

    /// <summary>Beta releases.</summary>
    Beta,
}

/// <summary>Updatable component.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<UpdateComponent>))]
public enum UpdateComponent
{
    /// <summary>ClubShellAgent service.</summary>
    Agent,

    /// <summary>Kiosk shell.</summary>
    Shell,
}

/// <summary>Phase reported by <c>update.progress</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<UpdatePhase>))]
public enum UpdatePhase
{
    /// <summary>Downloading the package.</summary>
    Downloading,

    /// <summary>Verifying SHA-256 and signature.</summary>
    Verifying,

    /// <summary>Copying to <c>pending-update\</c>.</summary>
    Staging,

    /// <summary>Installing.</summary>
    Applying,

    /// <summary>Failed; <c>error</c> set.</summary>
    Failed,
}

/// <summary>Update package descriptor (IPC_PROTOCOL.md §6.19; <c>GET /updates/{channel}/manifest</c>).</summary>
/// <param name="Channel">Channel.</param>
/// <param name="Component">Component.</param>
/// <param name="Version">Package semver.</param>
/// <param name="Url">HTTPS download URL (Bearer required, <c>Range</c> supported).</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the package.</param>
/// <param name="Size">Package size in bytes.</param>
/// <param name="Signature">Base64 RSA-PSS-SHA256 signature over the raw package bytes.</param>
/// <param name="ReleaseNotes">Markdown release notes.</param>
/// <param name="Mandatory">Must be applied even during a session (after a 60 s notice).</param>
/// <param name="PublishedAt">Publication time.</param>
/// <param name="MinAgentVersion">Minimum Agent version required (shell packages).</param>
public sealed record UpdateManifest(
    UpdateChannel Channel,
    UpdateComponent Component,
    string Version,
    string Url,
    string Sha256,
    long Size,
    string Signature,
    string ReleaseNotes,
    bool Mandatory,
    DateTimeOffset PublishedAt,
    string? MinAgentVersion = null);

#endregion

#region Server → Agent commands

/// <summary>Command types the server may send over WS / <c>GET /agents/{pcId}/commands</c> (SERVER_API.md §6.1).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ServerCommandType>))]
public enum ServerCommandType
{
    /// <summary>Lock the session / show idle lock; payload <see cref="LockCommand"/>; result <see langword="null"/>.</summary>
    Lock,

    /// <summary>Unlock; no payload; result <see langword="null"/>.</summary>
    Unlock,

    /// <summary>Show an admin message; payload <see cref="MessageCommand"/>; result <see cref="MessageDeliveryResult"/>.</summary>
    Message,

    /// <summary>Reboot; payload <see cref="PowerCommand"/>; result <see cref="ScheduledResult"/>.</summary>
    Reboot,

    /// <summary>Shut down; payload <see cref="PowerCommand"/>; result <see cref="ScheduledResult"/>.</summary>
    Shutdown,

    /// <summary>Send a Wake-on-LAN packet to another PC; payload <see cref="WakeCommand"/>; result <see langword="null"/>.</summary>
    Wake,

    /// <summary>End the session; payload <see cref="EndSessionCommand"/>; result <see cref="SessionResult"/>.</summary>
    EndSession,

    /// <summary>Extend the session; payload <see cref="ExtendSessionCommand"/>; result <see cref="SessionResult"/>.</summary>
    ExtendSession,

    /// <summary>Launch a game; payload <see cref="LaunchRequest"/>; result <see cref="LaunchResult"/>.</summary>
    LaunchGame,

    /// <summary>Kill a game; payload <see cref="KillGameCommand"/>; result <see cref="GamesKillResponse"/>.</summary>
    KillGame,

    /// <summary>Apply a policy; payload <see cref="Policy"/>; result <see cref="SetPolicyResult"/>.</summary>
    SetPolicy,

    /// <summary>Re-fetch and apply the policy; no payload; result <see cref="ReloadPolicyResult"/>.</summary>
    ReloadPolicy,

    /// <summary>Capture the screen; payload <see cref="ScreenshotCommand"/>; result <see cref="ScreenshotResult"/>.</summary>
    Screenshot,

    /// <summary>Start remote control; payload <see cref="RemoteControlStartCommand"/>; result <see cref="RemoteControlStartResult"/>.</summary>
    RemoteControlStart,

    /// <summary>Stop remote control; payload <see cref="RemoteControlStopCommand"/>; result <see cref="RemoteControlStopResult"/>.</summary>
    RemoteControlStop,

    /// <summary>Check/apply an update; payload <see cref="UpdateCommand"/>; result <see cref="UpdateApplyResponse"/>.</summary>
    Update,

    /// <summary>Show ads; payload <see cref="ShowAdsArgs"/>; result <see langword="null"/>.</summary>
    ShowAds,

    /// <summary>Set volume; payload <see cref="SetVolumeRequest"/>; result <see cref="VolumeState"/>.</summary>
    SetVolume,

    /// <summary>Re-fetch caches; payload <see cref="RefreshConfigCommand"/>; result <see cref="RefreshConfigResult"/>.</summary>
    RefreshConfig,
}

/// <summary>Normalized server command as handled by the Agent's command dispatcher.</summary>
/// <param name="Id">Command id (dedupe key, 24 h).</param>
/// <param name="Type">Command type.</param>
/// <param name="IssuedAt">Server timestamp.</param>
/// <param name="Payload">Typed payload (see <see cref="ServerCommandType"/>) or <see langword="null"/>.</param>
/// <param name="IssuedBy">Admin login or <c>system</c>, when known.</param>
/// <param name="Supersedes">Earlier pending command this one cancels.</param>
/// <param name="ExpiresAt">Discard (ack with <see cref="Errors.ErrorCode.Timeout"/>) after this time.</param>
public sealed record ServerCommand(
    Guid Id,
    ServerCommandType Type,
    DateTimeOffset IssuedAt,
    JsonElement? Payload,
    string? IssuedBy = null,
    Guid? Supersedes = null,
    DateTimeOffset? ExpiresAt = null)
{
    /// <summary><see langword="true"/> when <see cref="ExpiresAt"/> has passed at <paramref name="now"/>.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;

    /// <summary>Deserializes <see cref="Payload"/> as <typeparamref name="TPayload"/>; <see langword="null"/> when absent.</summary>
    public TPayload? PayloadAs<TPayload>() where TPayload : class =>
        Payload is { ValueKind: JsonValueKind.Object } p ? JsonDefaults.FromElement<TPayload>(p) : null;

    /// <summary>Builds from a REST polling envelope.</summary>
    public static ServerCommand FromEnvelope(ServerCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new(envelope.Id, envelope.Name, envelope.Ts, envelope.Payload, envelope.IssuedBy, envelope.Supersedes, envelope.ExpiresAt);
    }

    /// <summary>Builds from a WS <c>command</c> frame; throws <see cref="ArgumentException"/> for other frame types.</summary>
    public static ServerCommand FromFrame(WsFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Type != WsFrameType.Command)
        {
            throw new ArgumentException($"Frame type is {frame.Type}, expected command", nameof(frame));
        }

        if (frame.Name is null || !ServerCommandTypes.TryParse(frame.Name, out var type))
        {
            throw new ArgumentException($"Unknown server command '{frame.Name}'", nameof(frame));
        }

        return new(frame.Id, type, frame.Ts, frame.Payload, null, frame.Supersedes, frame.ExpiresAt);
    }
}

/// <summary>Wire form of a queued command returned by <c>GET /agents/{pcId}/commands</c>.</summary>
/// <param name="Id">Command id.</param>
/// <param name="Ts">Server timestamp.</param>
/// <param name="Name">Command type.</param>
/// <param name="Payload">Payload or <see langword="null"/>.</param>
/// <param name="IssuedBy">Admin login, when known.</param>
/// <param name="Supersedes">Earlier pending command this one cancels.</param>
/// <param name="ExpiresAt">Expiry.</param>
public sealed record ServerCommandEnvelope(
    Guid Id,
    DateTimeOffset Ts,
    ServerCommandType Name,
    JsonElement? Payload,
    string? IssuedBy = null,
    Guid? Supersedes = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Response of <c>GET /agents/{pcId}/commands</c>.</summary>
/// <param name="Items">Pending commands, oldest first.</param>
public sealed record ServerCommandsResponse(IReadOnlyList<ServerCommandEnvelope> Items);

/// <summary>Body of <c>POST /agents/{pcId}/commands/{commandId}/ack</c> and payload of a WS <c>ack</c> frame.</summary>
/// <param name="Ok">Whether the command succeeded.</param>
/// <param name="Error">Failure, when <paramref name="Ok"/> is <see langword="false"/>.</param>
/// <param name="Result">Typed result (see <see cref="ServerCommandType"/>) or <see langword="null"/>.</param>
public sealed record CommandAck(
    bool Ok,
    IpcError? Error = null,
    JsonElement? Result = null)
{
    /// <summary>Successful ack with an optional typed result.</summary>
    public static CommandAck Success<TResult>(TResult? result) where TResult : class =>
        new(true, null, result is null ? null : JsonDefaults.ToElement(result));

    /// <summary>Successful ack without a result.</summary>
    public static CommandAck Success() => new(true, null, null);

    /// <summary>Failed ack.</summary>
    public static CommandAck Failure(IpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(false, error, null);
    }
}

/// <summary>Helpers over <see cref="ServerCommandType"/>.</summary>
public static class ServerCommandTypes
{
    /// <summary>Parses a camelCase wire name (<c>remoteControlStart</c>).</summary>
    public static bool TryParse(string? name, out ServerCommandType type)
    {
        if (name is { Length: > 0 } && char.IsAsciiLetter(name[0]) && Enum.TryParse(name, ignoreCase: true, out type) && Enum.IsDefined(type))
        {
            return true;
        }

        type = default;
        return false;
    }

    /// <summary>camelCase wire name of <paramref name="type"/>.</summary>
    public static string ToWireName(this ServerCommandType type) => JsonNamingPolicy.CamelCase.ConvertName(type.ToString());
}

/// <summary>Payload of <see cref="ServerCommandType.Lock"/>; also <c>shell.command{lock}</c> args.</summary>
/// <param name="Reason">Machine-readable reason.</param>
/// <param name="Message">Message to display.</param>
public sealed record LockCommand(
    string? Reason = null,
    string? Message = null);

/// <summary>Payload of <see cref="ServerCommandType.Message"/>.</summary>
/// <param name="Id">Message id (acked via <c>sys.ackAdminMessage</c>).</param>
/// <param name="From">Sender name.</param>
/// <param name="Text">Text.</param>
/// <param name="Level">Severity.</param>
/// <param name="RequiresAck">User must acknowledge.</param>
public sealed record MessageCommand(
    Guid Id,
    string From,
    string Text,
    NotificationLevel Level,
    bool RequiresAck);

/// <summary>Result of <see cref="ServerCommandType.Message"/>; a second ack is sent when the user acknowledges.</summary>
/// <param name="DeliveredAt">When the Shell displayed it.</param>
/// <param name="AckedAt">When the user acknowledged it.</param>
public sealed record MessageDeliveryResult(
    DateTimeOffset DeliveredAt,
    DateTimeOffset? AckedAt = null);

/// <summary>Payload of <see cref="ServerCommandType.Reboot"/> and <see cref="ServerCommandType.Shutdown"/>.</summary>
/// <param name="DelaySec">Delay before the action.</param>
/// <param name="Force">End an active session first.</param>
/// <param name="Message">Message to display.</param>
public sealed record PowerCommand(
    int DelaySec,
    bool Force,
    string? Message = null);

/// <summary>Result of power commands and <c>sys.reboot</c>/<c>sys.shutdown</c>.</summary>
/// <param name="ScheduledAt">When the action will run.</param>
public sealed record ScheduledResult(DateTimeOffset ScheduledAt);

/// <summary>Payload of <see cref="ServerCommandType.Wake"/>.</summary>
/// <param name="TargetMac">MAC of the PC to wake, <c>AA:BB:CC:DD:EE:FF</c>.</param>
public sealed record WakeCommand(string TargetMac);

/// <summary>Payload of <see cref="ServerCommandType.EndSession"/>.</summary>
/// <param name="SessionId">Session to end.</param>
/// <param name="Reason">End reason.</param>
public sealed record EndSessionCommand(
    Guid SessionId,
    SessionEndReason Reason);

/// <summary>Payload of <see cref="ServerCommandType.ExtendSession"/>.</summary>
/// <param name="SessionId">Session to extend.</param>
/// <param name="Minutes">Minutes to add.</param>
/// <param name="Charge">Charge the wallet (<see langword="false"/> when already billed server-side).</param>
public sealed record ExtendSessionCommand(
    Guid SessionId,
    int Minutes,
    bool Charge);

/// <summary>Result carrying the resulting session.</summary>
/// <param name="Session">Session after the command.</param>
public sealed record SessionResult(Session Session);

/// <summary>Payload of <see cref="ServerCommandType.KillGame"/>.</summary>
/// <param name="GameId">Game to kill.</param>
/// <param name="Pid">Process to kill.</param>
/// <param name="Force">Terminate immediately instead of a graceful close.</param>
public sealed record KillGameCommand(
    Guid? GameId,
    int? Pid,
    bool Force);

/// <summary>Result of <see cref="ServerCommandType.SetPolicy"/>.</summary>
/// <param name="Version">Policy version.</param>
/// <param name="Applied">Whether it was applied without errors.</param>
public sealed record SetPolicyResult(
    int Version,
    bool Applied);

/// <summary>Result of <see cref="ServerCommandType.ReloadPolicy"/>.</summary>
/// <param name="Version">Policy version now in effect.</param>
public sealed record ReloadPolicyResult(int Version);

/// <summary>Payload of <see cref="ServerCommandType.Screenshot"/>.</summary>
/// <param name="Quality">JPEG quality 1–100.</param>
/// <param name="UploadUrl">Pre-signed <c>PUT</c> URL for the JPEG.</param>
/// <param name="Monitor">Monitor index; <see langword="null"/> = primary.</param>
/// <param name="MaxWidth">Downscale to this width.</param>
public sealed record ScreenshotCommand(
    int Quality,
    string UploadUrl,
    int? Monitor = null,
    int? MaxWidth = null);

/// <summary>Result of <see cref="ServerCommandType.Screenshot"/>.</summary>
/// <param name="Width">Image width.</param>
/// <param name="Height">Image height.</param>
/// <param name="Bytes">Uploaded size.</param>
/// <param name="UploadedAt">Upload time.</param>
public sealed record ScreenshotResult(
    int Width,
    int Height,
    long Bytes,
    DateTimeOffset UploadedAt);

/// <summary>Payload of <see cref="ServerCommandType.RemoteControlStart"/>.</summary>
/// <param name="SessionToken">Relay session token.</param>
/// <param name="RelayUrl">Relay <c>wss://</c> URL.</param>
/// <param name="Fps">Capture frame rate.</param>
/// <param name="AllowInput">Allow remote keyboard/mouse input.</param>
/// <param name="AdminName">Admin shown in the on-screen indicator.</param>
public sealed record RemoteControlStartCommand(
    string SessionToken,
    string RelayUrl,
    int Fps,
    bool AllowInput,
    string AdminName);

/// <summary>Result of <see cref="ServerCommandType.RemoteControlStart"/>.</summary>
/// <param name="StartedAt">Start time.</param>
public sealed record RemoteControlStartResult(DateTimeOffset StartedAt);

/// <summary>Payload of <see cref="ServerCommandType.RemoteControlStop"/>.</summary>
/// <param name="SessionToken">Relay session token.</param>
public sealed record RemoteControlStopCommand(string SessionToken);

/// <summary>Result of <see cref="ServerCommandType.RemoteControlStop"/>.</summary>
/// <param name="StoppedAt">Stop time.</param>
/// <param name="DurationSec">Session length.</param>
public sealed record RemoteControlStopResult(
    DateTimeOffset StoppedAt,
    int DurationSec);

/// <summary>Payload of <see cref="ServerCommandType.Update"/>.</summary>
/// <param name="Component">Component to update.</param>
/// <param name="ApplyNow">Apply immediately (subject to session/mandatory rules).</param>
/// <param name="Manifest">Manifest to use; <see langword="null"/> = fetch.</param>
public sealed record UpdateCommand(
    UpdateComponent Component,
    bool ApplyNow,
    UpdateManifest? Manifest = null);

/// <summary>Payload of <see cref="ServerCommandType.RefreshConfig"/>; all flags <see langword="false"/>/absent = refresh everything.</summary>
/// <param name="Config">Re-fetch <c>/agents/{pcId}/config</c>.</param>
/// <param name="Games">Re-fetch games.</param>
/// <param name="Apps">Re-fetch apps.</param>
/// <param name="Tariffs">Re-fetch tariffs.</param>
/// <param name="Products">Re-fetch shop products.</param>
/// <param name="Themes">Re-download themes.</param>
public sealed record RefreshConfigCommand(
    bool? Config = null,
    bool? Games = null,
    bool? Apps = null,
    bool? Tariffs = null,
    bool? Products = null,
    bool? Themes = null)
{
    /// <summary><see langword="true"/> when no specific cache was selected.</summary>
    [JsonIgnore]
    public bool IsAll => Config != true && Games != true && Apps != true && Tariffs != true && Products != true && Themes != true;
}

/// <summary>Result of <see cref="ServerCommandType.RefreshConfig"/>.</summary>
/// <param name="Refreshed">Caches refreshed (<c>config</c>, <c>games</c>, <c>apps</c>, <c>tariffs</c>, <c>products</c>, <c>themes</c>).</param>
public sealed record RefreshConfigResult(IReadOnlyList<string> Refreshed);

#endregion

#region Agent → Server events

/// <summary>Events the Agent emits over WS (SERVER_API.md §6.2); replayed via REST when offline.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AgentEventType>))]
public enum AgentEventType
{
    /// <summary>Payload <see cref="SessionStartedEvent"/>.</summary>
    SessionStarted,

    /// <summary>Payload <see cref="SessionEndedEvent"/>.</summary>
    SessionEnded,

    /// <summary>Payload <see cref="GameLaunchedEvent"/>.</summary>
    GameLaunched,

    /// <summary>Payload <see cref="GameExitedEvent"/>.</summary>
    GameExited,

    /// <summary>Payload <see cref="AntiCheatReport"/>.</summary>
    AnticheatViolation,

    /// <summary>Payload <see cref="HardwareChangedEvent"/>.</summary>
    HardwareChanged,

    /// <summary>Payload <see cref="OfflineQueueFlushedEvent"/>.</summary>
    OfflineQueueFlushed,
}

/// <summary>Agent → server event.</summary>
/// <param name="Type">Event type.</param>
/// <param name="At">Event time.</param>
/// <param name="Payload">Typed payload (see <see cref="AgentEventType"/>).</param>
public sealed record AgentEvent(
    AgentEventType Type,
    DateTimeOffset At,
    JsonElement? Payload)
{
    /// <summary>Creates an event with a typed payload.</summary>
    public static AgentEvent Of<TPayload>(AgentEventType type, DateTimeOffset at, TPayload payload) =>
        new(type, at, JsonDefaults.ToElement(payload));

    /// <summary>camelCase wire name used in <see cref="WsFrame.Name"/>.</summary>
    [JsonIgnore]
    public string WireName => JsonNamingPolicy.CamelCase.ConvertName(Type.ToString());
}

/// <summary>Payload of <see cref="AgentEventType.GameLaunched"/>.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="GameId">Game.</param>
/// <param name="Pid">Process id.</param>
/// <param name="AccountLeaseId">Lease used, when any.</param>
/// <param name="At">Launch time.</param>
public sealed record GameLaunchedEvent(
    Guid SessionId,
    Guid GameId,
    int Pid,
    Guid? AccountLeaseId,
    DateTimeOffset At);

/// <summary>Payload of <see cref="AgentEventType.GameExited"/>.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="GameId">Game.</param>
/// <param name="Pid">Process id.</param>
/// <param name="ExitCode">Exit code.</param>
/// <param name="PlayedSec">Seconds played.</param>
/// <param name="At">Exit time.</param>
public sealed record GameExitedEvent(
    Guid SessionId,
    Guid GameId,
    int Pid,
    int ExitCode,
    int PlayedSec,
    DateTimeOffset At);

/// <summary>Payload of <see cref="AgentEventType.HardwareChanged"/>.</summary>
/// <param name="Hardware">New inventory.</param>
/// <param name="Diff">Changed top-level keys (<c>gpu</c>, <c>disks</c>, …).</param>
public sealed record HardwareChangedEvent(
    HardwareInfo Hardware,
    IReadOnlyList<string> Diff);

/// <summary>Payload of <see cref="AgentEventType.OfflineQueueFlushed"/>.</summary>
/// <param name="Count">Entries delivered.</param>
/// <param name="Deadlettered">Entries moved to the dead-letter table.</param>
/// <param name="OfflineFrom">Start of the offline period.</param>
/// <param name="OfflineTo">End of the offline period.</param>
public sealed record OfflineQueueFlushedEvent(
    int Count,
    int Deadlettered,
    DateTimeOffset OfflineFrom,
    DateTimeOffset OfflineTo);

#endregion

#region WebSocket

/// <summary>WS frame type (SERVER_API.md §6).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<WsFrameType>))]
public enum WsFrameType
{
    /// <summary>Server → Agent command; must be acked.</summary>
    Command,

    /// <summary>Agent → Server ack of a command.</summary>
    Ack,

    /// <summary>Agent → Server event; not acked.</summary>
    Event,

    /// <summary>Keepalive request (server every 20 s).</summary>
    Ping,

    /// <summary>Keepalive reply (within 10 s).</summary>
    Pong,

    /// <summary>Server → Agent push; not acked.</summary>
    Push,
}

/// <summary>Server → Agent pushes (<see cref="WsFrameType.Push"/>, SERVER_API.md §6.3).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<WsPushKind>))]
public enum WsPushKind
{
    /// <summary>Payload <see cref="Wallet.Balance"/> → IPC <c>wallet.updated</c>.</summary>
    WalletUpdated,

    /// <summary>Payload <see cref="Users.ChatMessage"/> → IPC <c>chat.message</c>.</summary>
    ChatMessage,

    /// <summary>Payload <see cref="Users.Notification"/> → IPC <c>notification.push</c>.</summary>
    Notification,

    /// <summary>Payload <see cref="Shop.Order"/> → IPC <c>notification.push</c> + <c>shop.orderUpdated</c>.</summary>
    OrderUpdated,

    /// <summary>Payload <see cref="Users.Booking"/> → IPC <c>notification.push</c>.</summary>
    BookingUpdated,

    /// <summary>Payload <see cref="Users.Tournament"/> → IPC <c>notification.push</c>.</summary>
    TournamentUpdated,

    /// <summary>Payload <see cref="Sessions.Session"/> → reconcile + IPC <c>session.updated</c>.</summary>
    SessionUpdated,

    /// <summary>Payload <see cref="PcStatusChangedPush"/> → seat-map cache.</summary>
    PcStatusChanged,

    /// <summary>Payload <see cref="UserRevokedPush"/> → IPC <c>auth.expired{revoked}</c>, end session.</summary>
    UserRevoked,
}

/// <summary>Helpers over <see cref="WsPushKind"/>.</summary>
public static class WsPushKinds
{
    /// <summary>Parses a camelCase wire name.</summary>
    public static bool TryParse(string? name, out WsPushKind kind)
    {
        if (name is { Length: > 0 } && char.IsAsciiLetter(name[0]) && Enum.TryParse(name, ignoreCase: true, out kind) && Enum.IsDefined(kind))
        {
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>camelCase wire name of <paramref name="kind"/>.</summary>
    public static string ToWireName(this WsPushKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());
}

/// <summary>Payload of <see cref="WsPushKind.PcStatusChanged"/>.</summary>
/// <param name="PcId">PC.</param>
/// <param name="Status">New status.</param>
public sealed record PcStatusChangedPush(
    Guid PcId,
    PcStatus Status);

/// <summary>Payload of <see cref="WsPushKind.UserRevoked"/>.</summary>
/// <param name="UserId">Revoked user.</param>
/// <param name="Reason">Reason.</param>
public sealed record UserRevokedPush(
    Guid UserId,
    string Reason);

/// <summary>Ack body inside a <see cref="WsFrameType.Ack"/> frame.</summary>
/// <param name="Id">Id of the command being acknowledged.</param>
/// <param name="Ok">Success.</param>
/// <param name="Error">Failure details.</param>
/// <param name="Result">Typed result or <see langword="null"/>.</param>
public sealed record WsAck(
    Guid Id,
    bool Ok,
    IpcError? Error = null,
    JsonElement? Result = null)
{
    /// <summary>Builds from a REST-style <see cref="CommandAck"/>.</summary>
    public static WsAck From(Guid commandId, CommandAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        return new(commandId, ack.Ok, ack.Error, ack.Result);
    }
}

/// <summary>One text frame on <c>wss://&lt;server&gt;/ws/agent</c> (SERVER_API.md §6). Max 1 MiB.</summary>
/// <param name="Type">Frame type.</param>
/// <param name="Id">Frame id (command id for commands).</param>
/// <param name="Ts">Sender timestamp.</param>
/// <param name="Name"><see cref="ServerCommandType"/> / <see cref="AgentEventType"/> / <see cref="WsPushKind"/> wire name; required for command/event/push.</param>
/// <param name="Payload">Body or <see langword="null"/>.</param>
/// <param name="Ack">Ack body; required for <see cref="WsFrameType.Ack"/>.</param>
/// <param name="Supersedes">Command only: earlier pending command this one cancels.</param>
/// <param name="ExpiresAt">Command only: discard after this time.</param>
public sealed record WsFrame(
    WsFrameType Type,
    Guid Id,
    DateTimeOffset Ts,
    string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Payload = null,
    WsAck? Ack = null,
    Guid? Supersedes = null,
    DateTimeOffset? ExpiresAt = null)
{
    /// <summary>Maximum frame size in bytes.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>Subprotocol negotiated on connect.</summary>
    public const string Subprotocol = "clubshell.v1";

    /// <summary>Builds an <see cref="WsFrameType.Ack"/> frame for <paramref name="commandId"/>.</summary>
    public static WsFrame AckOf(Guid commandId, CommandAck ack, DateTimeOffset? ts = null) =>
        new(WsFrameType.Ack, Guid.NewGuid(), ts ?? DateTimeOffset.UtcNow, null, null, WsAck.From(commandId, ack));

    /// <summary>Builds an <see cref="WsFrameType.Event"/> frame.</summary>
    public static WsFrame EventOf(AgentEvent agentEvent, DateTimeOffset? ts = null)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        return new(WsFrameType.Event, Guid.NewGuid(), ts ?? agentEvent.At, agentEvent.WireName, agentEvent.Payload);
    }

    /// <summary>Builds a <see cref="WsFrameType.Pong"/> reply to <paramref name="ping"/>.</summary>
    public static WsFrame PongFor(WsFrame ping, DateTimeOffset? ts = null)
    {
        ArgumentNullException.ThrowIfNull(ping);
        return new(WsFrameType.Pong, ping.Id, ts ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Builds a <see cref="WsFrameType.Ping"/> frame.</summary>
    public static WsFrame Ping(DateTimeOffset? ts = null) => new(WsFrameType.Ping, Guid.NewGuid(), ts ?? DateTimeOffset.UtcNow);

    /// <summary>Parses <see cref="Name"/> as a push kind (frames of type <see cref="WsFrameType.Push"/>).</summary>
    public bool TryGetPushKind(out WsPushKind kind)
    {
        if (Type == WsFrameType.Push)
        {
            return WsPushKinds.TryParse(Name, out kind);
        }

        kind = default;
        return false;
    }

    /// <summary>Deserializes <see cref="Payload"/> as <typeparamref name="TPayload"/>; <see langword="null"/> when absent.</summary>
    public TPayload? PayloadAs<TPayload>() where TPayload : class =>
        Payload is { ValueKind: JsonValueKind.Object } p ? JsonDefaults.FromElement<TPayload>(p) : null;
}

#endregion

#region Agent REST (register / refresh / heartbeat / telemetry / support)

/// <summary>Body of <c>POST /agents/register</c> (auth <c>X-Club-Key</c>).</summary>
/// <param name="Hwid">Hardware id (sha256 hex).</param>
/// <param name="MachineName">Windows machine name.</param>
/// <param name="AgentVersion">Agent semver.</param>
/// <param name="Hardware">Inventory.</param>
/// <param name="IpAddress">IPv4 address.</param>
/// <param name="MacAddress">Primary MAC.</param>
/// <param name="PreviousPcId">Hint for re-registration after reinstall.</param>
public sealed record AgentRegisterRequest(
    string Hwid,
    string MachineName,
    string AgentVersion,
    HardwareInfo Hardware,
    string IpAddress,
    string MacAddress,
    Guid? PreviousPcId = null);

/// <summary>Response of <c>POST /agents/register</c>. Secrets are stored DPAPI-protected in <c>secure\agent.tokens</c>.</summary>
/// <param name="PcId">Assigned PC id.</param>
/// <param name="Pc">PC record.</param>
/// <param name="AccessToken">Agent JWT (≈ 1 h).</param>
/// <param name="RefreshToken">Opaque refresh token (30 d, single-use rotation).</param>
/// <param name="SigningSecret">Base64 32-byte HMAC key for request signing.</param>
/// <param name="ExpiresAt">Access token expiry.</param>
/// <param name="ServerTime">Server clock.</param>
/// <param name="Config">Server configuration overrides.</param>
public sealed record AgentRegisterResponse(
    Guid PcId,
    Pc Pc,
    string AccessToken,
    string RefreshToken,
    string SigningSecret,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ServerTime,
    AgentServerConfig Config);

/// <summary>Body of <c>POST /agents/refresh</c>.</summary>
/// <param name="RefreshToken">Current refresh token.</param>
/// <param name="Hwid">Hardware id.</param>
public sealed record AgentRefreshRequest(
    string RefreshToken,
    string Hwid);

/// <summary>Response of <c>POST /agents/refresh</c>.</summary>
/// <param name="AccessToken">New JWT.</param>
/// <param name="RefreshToken">New refresh token (old one is invalid).</param>
/// <param name="ExpiresAt">Access token expiry.</param>
/// <param name="SigningSecret">Present only when rotated; must be switched atomically.</param>
public sealed record AgentRefreshResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string? SigningSecret = null);

/// <summary>Running game summary in a heartbeat.</summary>
/// <param name="GameId">Game.</param>
/// <param name="Pid">Process id.</param>
/// <param name="StartedAt">Launch time.</param>
public sealed record HeartbeatRunningGame(
    Guid GameId,
    int Pid,
    DateTimeOffset StartedAt);

/// <summary>Body of <c>POST /agents/{pcId}/heartbeat</c> (every <c>session.heartbeatSec</c>).</summary>
/// <param name="Status">Agent's own view of the PC status.</param>
/// <param name="CurrentSessionId">Open session, when any.</param>
/// <param name="AgentVersion">Agent semver.</param>
/// <param name="ShellVersion">Shell semver.</param>
/// <param name="UptimeSec">OS uptime.</param>
/// <param name="IpAddress">IPv4 address.</param>
/// <param name="PolicyVersion">Applied policy version.</param>
/// <param name="RunningGames">Running games.</param>
/// <param name="OfflineQueue">Outbox size.</param>
/// <param name="ShellConnected">Whether the Shell pipe connection is alive.</param>
public sealed record HeartbeatRequest(
    PcStatus Status,
    Guid? CurrentSessionId,
    string AgentVersion,
    string ShellVersion,
    long UptimeSec,
    string IpAddress,
    int PolicyVersion,
    IReadOnlyList<HeartbeatRunningGame> RunningGames,
    int OfflineQueue,
    bool ShellConnected);

/// <summary>Response of <c>POST /agents/{pcId}/heartbeat</c>.</summary>
/// <param name="ServerTime">Server clock (used for offset correction).</param>
/// <param name="PcStatus">Authoritative status.</param>
/// <param name="PolicyVersion">Latest policy version; reload when newer than applied.</param>
/// <param name="ConfigVersion">Latest config version.</param>
/// <param name="CatalogVersion">Games/apps catalogue version; refresh lists when changed.</param>
/// <param name="PendingCommands">Commands queued while WS was down.</param>
/// <param name="Session">Server view of the current session for reconciliation.</param>
public sealed record HeartbeatResponse(
    DateTimeOffset ServerTime,
    PcStatus PcStatus,
    int PolicyVersion,
    int ConfigVersion,
    string CatalogVersion,
    int PendingCommands,
    Session? Session = null);

/// <summary>Well-known values of <see cref="TelemetryEvent.Kind"/>.</summary>
public static class TelemetryEventKinds
{
    /// <summary>Shell process crashed.</summary>
    public const string ShellCrash = "shellCrash";

    /// <summary>Shell restart limit exceeded.</summary>
    public const string ShellCrashLoop = "shellCrashLoop";

    /// <summary>Policy could not be applied.</summary>
    public const string PolicyApplyFailed = "policyApplyFailed";

    /// <summary>Update failed.</summary>
    public const string UpdateFailed = "updateFailed";

    /// <summary>Named-pipe error.</summary>
    public const string PipeError = "pipeError";

    /// <summary>Launcher error.</summary>
    public const string LauncherError = "launcherError";

    /// <summary>Outbox entry dead-lettered.</summary>
    public const string Deadletter = "deadletter";
}

/// <summary>Agent diagnostic event in a telemetry batch.</summary>
/// <param name="Kind">Kind (<see cref="TelemetryEventKinds"/>).</param>
/// <param name="At">Event time.</param>
/// <param name="Data">Structured data.</param>
public sealed record TelemetryEvent(
    string Kind,
    DateTimeOffset At,
    JsonElement Data);

/// <summary>Body of <c>POST /agents/{pcId}/telemetry</c>.</summary>
/// <param name="Samples">Metric samples (≤ 120).</param>
/// <param name="Events">Diagnostic events.</param>
/// <param name="Hardware">Inventory when changed / on rescan.</param>
/// <param name="LogsTail">Last ≤ 50 Warning+ log lines when <paramref name="Events"/> is non-empty.</param>
public sealed record TelemetryBatch(
    IReadOnlyList<PcMetrics> Samples,
    IReadOnlyList<TelemetryEvent> Events,
    HardwareInfo? Hardware = null,
    IReadOnlyList<string>? LogsTail = null)
{
    /// <summary>Maximum samples per batch.</summary>
    public const int MaxSamples = 120;

    /// <summary>Maximum log lines per batch.</summary>
    public const int MaxLogLines = 50;
}

/// <summary>Body of <c>POST /support/call-admin</c>. Sent with an <c>Idempotency-Key</c>.</summary>
/// <param name="PcId">PC.</param>
/// <param name="UserId">Logged-in user, when any.</param>
/// <param name="Category">Category.</param>
/// <param name="Message">Free text.</param>
/// <param name="At">Request time (client clock, for offline replay).</param>
public sealed record CallAdminTicketRequest(
    Guid PcId,
    Guid? UserId,
    CallAdminCategory Category,
    string? Message,
    DateTimeOffset At);

#endregion
