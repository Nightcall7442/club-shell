using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using ClubShell.Agent.Games;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Power;
using ClubShell.Agent.Remote;
using ClubShell.Agent.Session;
using ClubShell.Agent.Updates;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcPolicy = ClubShell.Contracts.Pcs.Policy;
using ServerConfig = ClubShell.Contracts.Pcs.AgentServerConfig;
using ShellSettingsStore = ClubShell.Agent.Ipc.Handlers.ShellSettingsStore;

namespace ClubShell.Agent.Server;

/// <summary>
/// Sends a <see cref="ShellCommand"/> to the Shell over IPC (<c>shell.command</c> event). Declared here because the
/// IPC layer is written in parallel; the DI phase binds this to the pipe bridge. Used for <c>lock</c>/<c>unlock</c>
/// mirror events and <c>showAds</c>.
/// </summary>
public interface IShellCommandSender
{
    /// <summary>Delivers <paramref name="command"/> to the connected Shell (no-op / best-effort when the Shell is offline).</summary>
    ValueTask SendAsync(ShellCommand command, CancellationToken cancellationToken);
}

/// <summary>Sets the endpoint audio volume for the interactive session.</summary>
public interface IVolumeController
{
    /// <summary>Applies <paramref name="request"/> and returns the resulting state.</summary>
    ValueTask<VolumeState> SetAsync(SetVolumeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Central dispatcher for <see cref="ServerCommand"/>s arriving over the WebSocket (<see cref="ServerConnection"/>) or
/// the REST heartbeat poll (<see cref="HeartbeatService"/>). Implements <see cref="IServerCommandSink"/> plus
/// <see cref="IPolicyRefresh"/> / <see cref="IConfigRefresh"/> (both declared in <c>Heartbeat.cs</c>). Every command is
/// deduplicated by id for <see cref="DedupeWindow"/> (at-least-once delivery), checked for expiry, honours
/// <c>supersedes</c> (cancels the still-running earlier command), runs under a per-command timeout and is audit-logged
/// with its <c>issuedBy</c>. Handlers translate results into <see cref="CommandAck.Success{TResult}"/> /
/// <see cref="CommandAck.Failure"/> (<see cref="IpcException"/> maps to its <see cref="IpcError"/>). The ack itself is
/// sent by the transport: <see cref="ServerConnection"/> over the WS, <see cref="HeartbeatService"/> via
/// <c>POST /commands/{id}/ack</c> for the REST path.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CommandReceiver : IServerCommandSink, IPolicyRefresh, IConfigRefresh
{
    /// <summary>How long a command id stays deduplicated (SERVER_API.md §6: 24 h).</summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(24);

    private const int MaxDedupeEntries = 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly ITokenStore _tokens;
    private readonly IServerClient _server;
    private readonly SettingsLoader _settingsLoader;
    private readonly SessionManager _sessions;
    private readonly SessionLock _sessionLock;
    private readonly AdminMessageService _adminMessages;
    private readonly PowerCommands _power;
    private readonly PowerScheduler _powerScheduler;
    private readonly GameLaunchService _games;
    private readonly GameLibrary _library;
    private readonly PolicyStore _policyStore;
    private readonly IPolicyEnforcer _policyEnforcer;
    private readonly ScreenCapture _screen;
    private readonly RemoteInputService _remoteInput;
    private readonly AgentUpdater _updater;
    private readonly IShellCommandSender _shell;
    private readonly IVolumeController? _volume;
    private readonly ShellSettingsStore? _shellSettings;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<CommandReceiver> _logger;

    private readonly ConcurrentDictionary<Guid, SeenEntry> _seen = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inflight = new();

    /// <summary>Creates the receiver. All dependencies are singletons.</summary>
    public CommandReceiver(
        ITokenStore tokens,
        IServerClient server,
        SettingsLoader settingsLoader,
        SessionManager sessions,
        SessionLock sessionLock,
        AdminMessageService adminMessages,
        PowerCommands power,
        PowerScheduler powerScheduler,
        GameLaunchService games,
        GameLibrary library,
        PolicyStore policyStore,
        IPolicyEnforcer policyEnforcer,
        ScreenCapture screen,
        RemoteInputService remoteInput,
        AgentUpdater updater,
        IShellCommandSender shell,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<CommandReceiver> logger,
        IVolumeController? volume = null,
        ShellSettingsStore? shellSettings = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settingsLoader);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sessionLock);
        ArgumentNullException.ThrowIfNull(adminMessages);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(powerScheduler);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(policyStore);
        ArgumentNullException.ThrowIfNull(policyEnforcer);
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(remoteInput);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _tokens = tokens;
        _server = server;
        _settingsLoader = settingsLoader;
        _sessions = sessions;
        _sessionLock = sessionLock;
        _adminMessages = adminMessages;
        _power = power;
        _powerScheduler = powerScheduler;
        _games = games;
        _library = library;
        _policyStore = policyStore;
        _policyEnforcer = policyEnforcer;
        _screen = screen;
        _remoteInput = remoteInput;
        _updater = updater;
        _shell = shell;
        _volume = volume;
        _shellSettings = shellSettings;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CommandAck> HandleAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        PruneSeen();

        if (_seen.TryGetValue(command.Id, out var existing))
        {
            _logger.LogDebug("Duplicate command {Id} ({Type}); returning cached ack", command.Id, command.Type);
            return await existing.Result.ConfigureAwait(false);
        }

        if (command.Supersedes is { } superseded && _inflight.TryGetValue(superseded, out var supersededCts))
        {
            _logger.LogInformation("Command {Id} supersedes {Superseded}; cancelling the earlier command", command.Id, superseded);
            try
            {
                supersededCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The superseded command already completed.
            }
        }

        if (command.IsExpired(_clock.UtcNow))
        {
            _logger.LogInformation("Command {Id} ({Type}) expired before delivery", command.Id, command.Type);
            var expiredAck = CommandAck.Failure(IpcError.Timeout("Command expired before delivery"));
            Remember(command.Id, Task.FromResult(expiredAck));
            return expiredAck;
        }

        var completion = new TaskCompletionSource<CommandAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_seen.TryAdd(command.Id, new SeenEntry(completion.Task, _clock.UtcNow)))
        {
            return await _seen[command.Id].Result.ConfigureAwait(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CommandTimeout);
        _inflight[command.Id] = cts;

        _logger.LogInformation("Server command {Id} {Type} issuedBy={IssuedBy}", command.Id, command.Type, command.IssuedBy ?? "-");
        CommandAck ack;
        try
        {
            ack = await DispatchAsync(command, cts.Token).ConfigureAwait(false);
        }
        catch (IpcException ex)
        {
            ack = CommandAck.Failure(ex.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _inflight.TryRemove(command.Id, out _);
            _seen.TryRemove(command.Id, out _);
            completion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Command {Id} ({Type}) timed out or was superseded", command.Id, command.Type);
            ack = CommandAck.Failure(IpcError.Timeout("Command timed out or was superseded"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {Id} ({Type}) handler threw", command.Id, command.Type);
            ack = CommandAck.Failure(IpcError.Internal(command.Id.ToString("D")));
        }
        finally
        {
            _inflight.TryRemove(command.Id, out _);
        }

        completion.TrySetResult(ack);
        return ack;
    }

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken) => RefreshPolicyAsync(cancellationToken);

    /// <inheritdoc />
    public Task RefreshAsync(RefreshConfigCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RefreshConfigInternalAsync(command, cancellationToken);
    }

    private Task<CommandAck> DispatchAsync(ServerCommand command, CancellationToken cancellationToken) => command.Type switch
    {
        ServerCommandType.Lock => LockAsync(command, cancellationToken),
        ServerCommandType.Unlock => UnlockAsync(cancellationToken),
        ServerCommandType.Message => MessageAsync(command, cancellationToken),
        ServerCommandType.Reboot => PowerAsync(command, reboot: true, cancellationToken),
        ServerCommandType.Shutdown => PowerAsync(command, reboot: false, cancellationToken),
        ServerCommandType.Wake => WakeAsync(command, cancellationToken),
        ServerCommandType.EndSession => EndSessionAsync(command, cancellationToken),
        ServerCommandType.ExtendSession => ExtendSessionAsync(command, cancellationToken),
        ServerCommandType.LaunchGame => LaunchGameAsync(command, cancellationToken),
        ServerCommandType.KillGame => KillGameAsync(command, cancellationToken),
        ServerCommandType.SetPolicy => SetPolicyAsync(command, cancellationToken),
        ServerCommandType.ReloadPolicy => ReloadPolicyAsync(cancellationToken),
        ServerCommandType.Screenshot => ScreenshotAsync(command, cancellationToken),
        ServerCommandType.RemoteControlStart => RemoteControlStartAsync(command, cancellationToken),
        ServerCommandType.RemoteControlStop => RemoteControlStopAsync(command, cancellationToken),
        ServerCommandType.Update => UpdateAsync(command, cancellationToken),
        ServerCommandType.ShowAds => ShowAdsAsync(command, cancellationToken),
        ServerCommandType.SetVolume => SetVolumeAsync(command, cancellationToken),
        ServerCommandType.RefreshConfig => RefreshConfigAsync(command, cancellationToken),
        _ => throw new IpcException(IpcError.UnknownMessage(command.Type.ToWireName())),
    };

    private async Task<CommandAck> LockAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = command.PayloadAs<LockCommand>() ?? new LockCommand();
        if (_sessions.State.IsOpen())
        {
            _ = await _sessionLock.LockAsync(payload.Reason, cancellationToken).ConfigureAwait(false);
        }

        await _shell.SendAsync(ShellCommand.Of(ShellCommandKind.Lock, payload, Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
        return CommandAck.Success();
    }

    private async Task<CommandAck> UnlockAsync(CancellationToken cancellationToken)
    {
        if (_sessions.State == SessionState.Locked)
        {
            _ = await _sessions.UnlockAsync(cancellationToken).ConfigureAwait(false);
        }

        await _shell.SendAsync(new ShellCommand(ShellCommandKind.Unlock, null, Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
        return CommandAck.Success();
    }

    private async Task<CommandAck> MessageAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var message = Require<MessageCommand>(command);
        var result = await _adminMessages.DeliverAsync(message, cancellationToken).ConfigureAwait(false);
        if (message.RequiresAck)
        {
            // The first ack reports delivery; a second, out-of-band ack is sent when the user acknowledges.
            _ = Task.Run(() => SendUserAckAsync(command.Id, message.Id), CancellationToken.None);
        }

        return CommandAck.Success(result);
    }

    private async Task SendUserAckAsync(Guid commandId, Guid messageId)
    {
        try
        {
            var result = await _adminMessages.WaitForAckAsync(messageId, CancellationToken.None).ConfigureAwait(false);
            if (result.AckedAt is null || _tokens.Agent?.PcId is not { } pcId)
            {
                return;
            }

            await _server.AckCommandAsync(pcId, commandId, CommandAck.Success(result), CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Sent user acknowledgement for admin message command {CommandId}", commandId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sending the user acknowledgement for command {CommandId} failed", commandId);
        }
    }

    private async Task<CommandAck> PowerAsync(ServerCommand command, bool reboot, CancellationToken cancellationToken)
    {
        var payload = Require<PowerCommand>(command);
        var result = reboot
            ? await _power.RebootAsync(payload.DelaySec, payload.Message, payload.Force, cancellationToken).ConfigureAwait(false)
            : await _power.ShutdownAsync(payload.DelaySec, payload.Message, payload.Force, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(result);
    }

    private async Task<CommandAck> WakeAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<WakeCommand>(command);
        if (!_settings.CurrentValue.Power.WolEnabled)
        {
            return CommandAck.Failure(IpcError.PolicyDenied("power.wolEnabled"));
        }

        var sent = await _powerScheduler.WakeAsync(payload.TargetMac, cancellationToken).ConfigureAwait(false);
        return sent
            ? CommandAck.Success()
            : CommandAck.Failure(IpcError.Of(ErrorCode.ServerUnavailable, $"Wake-on-LAN packet to {payload.TargetMac} could not be sent"));
    }

    private async Task<CommandAck> EndSessionAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<EndSessionCommand>(command);
        if (_sessions.Current is { } current && current.Id != payload.SessionId)
        {
            return CommandAck.Failure(IpcError.NotFound($"Session {payload.SessionId}"));
        }

        var result = await _sessions.EndAsync(payload.Reason, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(new SessionResult(result.Session));
    }

    private async Task<CommandAck> ExtendSessionAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<ExtendSessionCommand>(command);
        if (_sessions.Current is { } current && current.Id != payload.SessionId)
        {
            return CommandAck.Failure(IpcError.NotFound($"Session {payload.SessionId}"));
        }

        if (!payload.Charge)
        {
            // SERVER_API.md §6.1: already billed server-side, so adopt the server's view (GET /sessions/current)
            // instead of POST /extend, which would charge the wallet a second time.
            await _sessions.SyncWithServerAsync(cancellationToken).ConfigureAwait(false);
            var adopted = _sessions.Current ?? throw new IpcException(IpcError.SessionNotActive());
            return CommandAck.Success(new SessionResult(adopted));
        }

        var session = await _sessions.ExtendAsync(payload.Minutes, null, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(new SessionResult(session));
    }

    private async Task<CommandAck> LaunchGameAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var request = Require<LaunchRequest>(command);
        var result = await _games.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(result);
    }

    private async Task<CommandAck> KillGameAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<KillGameCommand>(command);
        var response = await _games.KillAsync(new GamesKillRequest(payload.GameId, payload.Pid, payload.Force), cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(response);
    }

    private async Task<CommandAck> SetPolicyAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var policy = Require<PcPolicy>(command);
        var problems = PolicyStore.Validate(policy);
        if (problems.Count > 0)
        {
            return CommandAck.Failure(IpcError.Validation("policy", string.Join("; ", problems)));
        }

        await _policyStore.SaveAsync(policy, cancellationToken).ConfigureAwait(false);
        var applied = await _policyEnforcer.ApplyAsync(policy, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(new SetPolicyResult(policy.Version, applied.Applied));
    }

    private async Task<CommandAck> ReloadPolicyAsync(CancellationToken cancellationToken)
    {
        var version = await RefreshPolicyAsync(cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(new ReloadPolicyResult(version));
    }

    private async Task<int> RefreshPolicyAsync(CancellationToken cancellationToken)
    {
        var (policy, source) = await _policyStore.LoadAsync(force: true, cancellationToken).ConfigureAwait(false);
        var applied = await _policyEnforcer.ApplyAsync(policy, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Policy v{Version} reloaded from {Source}; applied={Applied}", policy.Version, source, applied.Applied);
        return policy.Version;
    }

    private async Task<CommandAck> ScreenshotAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<ScreenshotCommand>(command);
        var result = await _screen.CaptureAndUploadAsync(payload, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(result);
    }

    private async Task<CommandAck> RemoteControlStartAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<RemoteControlStartCommand>(command);
        var result = await _remoteInput.StartAsync(payload, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(result);
    }

    private async Task<CommandAck> RemoteControlStopAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<RemoteControlStopCommand>(command);
        var result = await _remoteInput.StopAsync(payload, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(result);
    }

    private async Task<CommandAck> UpdateAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<UpdateCommand>(command);
        var result = await _updater.HandleCommandAsync(payload, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(result);
    }

    private async Task<CommandAck> ShowAdsAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<ShowAdsArgs>(command);
        await _shell.SendAsync(ShellCommand.Of(ShellCommandKind.ShowAds, payload, Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
        return CommandAck.Success();
    }

    private async Task<CommandAck> SetVolumeAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = Require<SetVolumeRequest>(command);
        var level = Math.Clamp(payload.Level, 0, 100);
        // ponytail: audio lives in the interactive session, not session 0. When a session-side controller is bound we
        // apply through it; otherwise the level is echoed and the Shell applies it via its own sys.setVolume handler.
        if (_volume is { } volume)
        {
            var state = await volume.SetAsync(payload with { Level = level }, cancellationToken).ConfigureAwait(false);
            return CommandAck.Success(state);
        }

        _logger.LogInformation("SetVolume {Level} (muted={Muted}) recorded; applied by the Shell mixer", level, payload.Muted ?? false);
        return CommandAck.Success(new VolumeState(level, payload.Muted ?? false));
    }

    private async Task<CommandAck> RefreshConfigAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        var payload = command.PayloadAs<RefreshConfigCommand>() ?? new RefreshConfigCommand();
        var refreshed = await RefreshConfigInternalAsync(payload, cancellationToken).ConfigureAwait(false);
        return CommandAck.Success(new RefreshConfigResult(refreshed));
    }

    private async Task<IReadOnlyList<string>> RefreshConfigInternalAsync(RefreshConfigCommand command, CancellationToken cancellationToken)
    {
        var refreshed = new List<string>();
        var all = command.IsAll;

        if ((all || command.Config == true) && await RefreshServerConfigAsync(cancellationToken).ConfigureAwait(false))
        {
            refreshed.Add("config");
        }

        if (all || command.Games == true || command.Apps == true)
        {
            try
            {
                if (await _library.HandleRefreshConfigAsync(command, cancellationToken).ConfigureAwait(false) || command.Games == true || all)
                {
                    refreshed.Add("games");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Refreshing the game catalogue failed");
            }
        }

        // tariffs, products and themes have no Agent-side cache; the Shell re-fetches them on demand.
        foreach (var (requested, name) in new[] { (command.Tariffs == true, "tariffs"), (command.Products == true, "products"), (command.Themes == true, "themes") })
        {
            if (requested)
            {
                refreshed.Add(name);
            }
        }

        _logger.LogInformation("RefreshConfig applied: {Refreshed}", refreshed.Count == 0 ? "-" : string.Join(", ", refreshed));
        return refreshed;
    }

    private async Task<bool> RefreshServerConfigAsync(CancellationToken cancellationToken)
    {
        if (_tokens.Agent?.PcId is not { } pcId)
        {
            return false;
        }

        try
        {
            var response = await _server.GetConfigAsync(pcId, null, cancellationToken).ConfigureAwait(false);
            if (response.Value is { } config)
            {
                _settingsLoader.ApplyServerOverrides(config);
                ApplyShellOverride(config);
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Refreshing server config failed");
        }

        return false;
    }

    /// <summary>
    /// Writes the server's <c>shell</c> block (feature flags, theme, locale, ads, idle) into <c>shell.json</c>.
    /// <see cref="SettingsLoader.ApplyServerOverrides"/> only covers the Agent's own sections, so without this the
    /// UI keeps every feature on and calls endpoints the server never implemented.
    /// </summary>
    private void ApplyShellOverride(ServerConfig config)
    {
        if (config.Shell is not { } shell || _shellSettings is null)
        {
            return;
        }

        try
        {
            var applied = _shellSettings.ApplyServerOverride(shell);
            _logger.LogInformation("Shell config from the server applied: theme {Theme}, locale {Locale}, features {Features}", applied.Theme, applied.Locale, applied.Features);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Shell config from the server could not be written to shell.json");
        }
    }

    private static TPayload Require<TPayload>(ServerCommand command)
        where TPayload : class
        => command.PayloadAs<TPayload>() ?? throw new IpcException(IpcError.Validation("payload", $"{command.Type.ToWireName()} requires a payload"));

    private void Remember(Guid id, Task<CommandAck> result) => _seen[id] = new SeenEntry(result, _clock.UtcNow);

    private void PruneSeen()
    {
        if (_seen.IsEmpty)
        {
            return;
        }

        var cutoff = _clock.UtcNow - DedupeWindow;
        foreach (var pair in _seen)
        {
            if (pair.Value.At < cutoff)
            {
                _seen.TryRemove(pair.Key, out _);
            }
        }

        if (_seen.Count <= MaxDedupeEntries)
        {
            return;
        }

        foreach (var pair in _seen.OrderBy(static e => e.Value.At).Take(_seen.Count - MaxDedupeEntries))
        {
            _seen.TryRemove(pair.Key, out _);
        }
    }

    private sealed record SeenEntry(Task<CommandAck> Result, DateTimeOffset At);
}
