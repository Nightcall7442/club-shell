using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Agent.AntiCheat;
using ClubShell.Agent.Storage;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Hardware;
using ClubShell.Windows.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Server;

/// <summary>Snapshot of the games currently running, used to populate the heartbeat.</summary>
public interface IRunningGamesSource
{
    /// <summary>The games running right now (empty when none).</summary>
    IReadOnlyList<HeartbeatRunningGame> Snapshot();
}

/// <summary>Current size of the offline outbox.</summary>
public interface IOfflineQueueDepth
{
    /// <summary>Number of events/requests waiting to be replayed.</summary>
    int Count { get; }
}

/// <summary>Whether the Shell's named-pipe connection is alive.</summary>
public interface IShellConnectionState
{
    /// <summary><see langword="true"/> while the Shell is connected over the pipe.</summary>
    bool IsConnected { get; }
}

/// <summary>Re-fetches and applies the server policy (heartbeat <c>policyVersion</c> mismatch).</summary>
public interface IPolicyRefresh
{
    /// <summary>Fetches <c>GET /agents/{pcId}/policies</c> and applies it.</summary>
    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>Re-fetches server config / catalogue caches (heartbeat version mismatch).</summary>
public interface IConfigRefresh
{
    /// <summary>Refreshes the caches selected by <paramref name="command"/> (all when empty).</summary>
    Task RefreshAsync(RefreshConfigCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Sends <c>POST /agents/{pcId}/heartbeat</c> every <c>session.heartbeatSec</c> (SERVER_API.md §4.1) and reacts to the
/// response: a newer <c>policyVersion</c> triggers <see cref="IPolicyRefresh"/>; a changed <c>configVersion</c> /
/// <c>catalogVersion</c> triggers <see cref="IConfigRefresh"/>; <c>pendingCommands &gt; 0</c> pulls
/// <c>GET /agents/{pcId}/commands</c> and dispatches each to <see cref="IServerCommandSink"/>; the returned
/// <c>serverTime</c> is used to log clock skew. Each beat also carries the games library state
/// (<see cref="GamesShareMounter.VolumeState"/>) and the Vanguard / Secure Boot / TPM state.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HeartbeatService : BackgroundService
{
    private readonly ServerConnection _connection;
    private readonly IServerClient _server;
    private readonly ISessionService _sessions;
    private readonly IPolicyEnforcer _policy;
    private readonly IRunningGamesSource _runningGames;
    private readonly IOfflineQueueDepth _queueDepth;
    private readonly IShellConnectionState _shell;
    private readonly IServerCommandSink _commandSink;
    private readonly IPolicyRefresh _policyRefresh;
    private readonly IConfigRefresh _configRefresh;
    private readonly NetworkProbe _network;
    private readonly GamesShareMounter _gamesVolume;
    private readonly WmiQueries _wmi;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<HeartbeatService> _logger;

    private int _lastConfigVersion = -1;
    private string? _lastCatalogVersion;
    private bool? _tpmPresent;

    /// <summary>Creates the heartbeat service.</summary>
    public HeartbeatService(
        ServerConnection connection,
        IServerClient server,
        ISessionService sessions,
        IPolicyEnforcer policy,
        IRunningGamesSource runningGames,
        IOfflineQueueDepth queueDepth,
        IShellConnectionState shell,
        IServerCommandSink commandSink,
        IPolicyRefresh policyRefresh,
        IConfigRefresh configRefresh,
        NetworkProbe network,
        GamesShareMounter gamesVolume,
        WmiQueries wmi,
        IClock clock,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<HeartbeatService> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(runningGames);
        ArgumentNullException.ThrowIfNull(queueDepth);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(commandSink);
        ArgumentNullException.ThrowIfNull(policyRefresh);
        ArgumentNullException.ThrowIfNull(configRefresh);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(gamesVolume);
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _server = server;
        _sessions = sessions;
        _policy = policy;
        _runningGames = runningGames;
        _queueDepth = queueDepth;
        _shell = shell;
        _commandSink = commandSink;
        _policyRefresh = policyRefresh;
        _configRefresh = configRefresh;
        _network = network;
        _gamesVolume = gamesVolume;
        _wmi = wmi;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(5, _settings.CurrentValue.Session.HeartbeatSec));
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_connection.PcId is not { } pcId)
            {
                continue;
            }

            try
            {
                await BeatOnceAsync(pcId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ServerApiException ex)
            {
                _logger.LogWarning("Heartbeat failed: {Code} {Message}", ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat failed unexpectedly");
            }
        }
    }

    private async Task BeatOnceAsync(Guid pcId, CancellationToken cancellationToken)
    {
        var appliedPolicy = _policy.Current?.Version ?? 0;
        var request = new HeartbeatRequest(
            MapStatus(),
            _sessions.Current?.Id,
            ClubShell.Core.Http.ClubShellVersion.Current,
            _server.ShellVersion,
            Environment.TickCount64 / 1000,
            _network.GetInfo().Ip,
            appliedPolicy,
            _runningGames.Snapshot(),
            _queueDepth.Count,
            _shell.IsConnected,
            _gamesVolume.VolumeState(),
            await AntiCheatStateAsync(cancellationToken).ConfigureAwait(false));

        var sentAt = _clock.UtcNow;
        var response = await _server.HeartbeatAsync(pcId, request, cancellationToken).ConfigureAwait(false);
        var receivedAt = _clock.UtcNow;

        var skew = response.ServerTime - receivedAt;
        var latencyMs = (int)Math.Clamp((receivedAt - sentAt).TotalMilliseconds, 0, int.MaxValue);
        if (Math.Abs(skew.TotalSeconds) > _settings.CurrentValue.Server.ClockSkewToleranceSec)
        {
            _logger.LogWarning("Clock skew {Skew} exceeds tolerance (heartbeat latency {Latency} ms)", skew, latencyMs);
        }

        if (response.PolicyVersion > appliedPolicy)
        {
            _logger.LogInformation("Server policy {Server} newer than applied {Applied}; refreshing", response.PolicyVersion, appliedPolicy);
            await SafeRefreshPolicyAsync(cancellationToken).ConfigureAwait(false);
        }

        await ApplyCacheVersionsAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.PendingCommands > 0)
        {
            await DrainPendingCommandsAsync(pcId, response.PendingCommands, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Vanguard driver, Secure Boot and TPM; a probe that fails leaves its field unknown instead of failing the beat.</summary>
    private async Task<HeartbeatAntiCheat> AntiCheatStateAsync(CancellationToken cancellationToken)
    {
        // TPM presence only changes across a reboot, which restarts the Agent, so one answered WMI query is kept.
        _tpmPresent ??= await _wmi.GetTpmPresentAsync(cancellationToken).ConfigureAwait(false);
        bool? installed = null;
        bool? loaded = null;
        try
        {
            var vgk = AntiCheatProbe.QueryService(VanguardChecker.DriverName);
            installed = vgk.Installed && vgk.ImageExists;
            loaded = vgk.IsRunning;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception)
        {
            _logger.LogDebug(ex, "Vanguard driver state unknown");
        }

        return new HeartbeatAntiCheat(installed, loaded, WmiQueries.GetSecureBootEnabled(), _tpmPresent);
    }

    private PcStatus MapStatus()
    {
        if (_sessions.Current is null)
        {
            return PcStatus.Free;
        }

        return _sessions.State switch
        {
            SessionState.Locked => PcStatus.Locked,
            SessionState.Starting or SessionState.Active or SessionState.Paused or SessionState.Ending => PcStatus.Busy,
            _ => PcStatus.Free,
        };
    }

    private async Task ApplyCacheVersionsAsync(HeartbeatResponse response, CancellationToken cancellationToken)
    {
        // The game catalogue is loaded from cache at boot, so its first heartbeat only establishes the baseline. The
        // server config is not: nothing fetches it before the first heartbeat, so skipping it here would leave the PC
        // on its local agent.json (and every feature flag on) until the operator happened to bump configVersion.
        var configChanged = response.ConfigVersion != _lastConfigVersion;
        var catalogChanged = _lastCatalogVersion is not null && !string.Equals(response.CatalogVersion, _lastCatalogVersion, StringComparison.Ordinal);
        _lastConfigVersion = response.ConfigVersion;
        _lastCatalogVersion = response.CatalogVersion;

        if (!configChanged && !catalogChanged)
        {
            return;
        }

        var command = new RefreshConfigCommand(
            Config: configChanged ? true : null,
            Games: catalogChanged ? true : null,
            Apps: catalogChanged ? true : null);

        _logger.LogInformation("Cache versions changed (config={Config}, catalog={Catalog}); refreshing", configChanged, catalogChanged);
        try
        {
            await _configRefresh.RefreshAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Config refresh failed");
        }
    }

    private async Task SafeRefreshPolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _policyRefresh.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Policy refresh failed");
        }
    }

    private Task DrainPendingCommandsAsync(Guid pcId, int pending, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching {Pending} pending command(s) over REST", pending);
        return DrainCommandsAsync(_server, _commandSink, _clock, _logger, pcId, cancellationToken);
    }

    /// <summary>
    /// Pulls <c>GET /agents/{pcId}/commands</c> and acks every item through <c>POST /agents/{pcId}/commands/{id}/ack</c>:
    /// known commands go to <paramref name="sink"/>, expired ones are acked <c>timeout</c> and names this agent does not
    /// know <c>notFound</c> (like the WebSocket path), so one item never blocks the rest of the batch.
    /// </summary>
    /// <param name="server">Server client.</param>
    /// <param name="sink">Command dispatcher.</param>
    /// <param name="clock">Clock for expiry checks.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="pcId">This PC.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes once every fetched command has been acked (or its ack failed).</returns>
    public static async Task DrainCommandsAsync(IServerClient server, IServerCommandSink sink, IClock clock, ILogger logger, Guid pcId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        ServerCommandsResponse commands;
        try
        {
            commands = await server.GetCommandsAsync(pcId, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex)
        {
            logger.LogWarning("Fetching pending commands failed: {Code}", ex.Code);
            return;
        }

        foreach (var envelope in commands.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ack = await ExecuteAsync(envelope, sink, clock, logger, cancellationToken).ConfigureAwait(false);
            try
            {
                await server.AckCommandAsync(pcId, envelope.Id, ack, cancellationToken).ConfigureAwait(false);
            }
            catch (ServerApiException ex)
            {
                logger.LogWarning("Acking command {Id} failed: {Code}", envelope.Id, ex.Code);
            }
        }
    }

    private static async Task<CommandAck> ExecuteAsync(ServerCommandEnvelope envelope, IServerCommandSink sink, IClock clock, ILogger logger, CancellationToken cancellationToken)
    {
        ServerCommand command;
        try
        {
            command = ServerCommand.FromEnvelope(envelope);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Unknown pending command {Name} ({Id}): {Message}", envelope.Name, envelope.Id, ex.Message);
            return CommandAck.Failure(IpcError.Of(ErrorCode.NotFound, $"Unknown command '{envelope.Name}'"));
        }

        if (command.IsExpired(clock.UtcNow))
        {
            return CommandAck.Failure(IpcError.Timeout("Command expired before delivery"));
        }

        try
        {
            return await sink.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (IpcException ex)
        {
            return CommandAck.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Pending command {Id} ({Type}) handler threw", command.Id, command.Type);
            return CommandAck.Failure(IpcError.Internal(command.Id.ToString("D")));
        }
    }
}
