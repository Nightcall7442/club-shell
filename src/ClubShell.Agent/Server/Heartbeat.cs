using System.Runtime.Versioning;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
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
/// <c>serverTime</c> is used to log clock skew.
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
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<HeartbeatService> _logger;

    private int _lastConfigVersion = -1;
    private string? _lastCatalogVersion;

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
            _shell.IsConnected);

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
        // First heartbeat establishes the baseline (caches were loaded at boot); later changes trigger a refresh.
        var configChanged = _lastConfigVersion >= 0 && response.ConfigVersion != _lastConfigVersion;
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

    private async Task DrainPendingCommandsAsync(Guid pcId, int pending, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching {Pending} pending command(s) over REST", pending);
        ServerCommandsResponse commands;
        try
        {
            commands = await _server.GetCommandsAsync(pcId, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex)
        {
            _logger.LogWarning("Fetching pending commands failed: {Code}", ex.Code);
            return;
        }

        foreach (var envelope in commands.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = ServerCommand.FromEnvelope(envelope);
            CommandAck ack;
            if (command.IsExpired(_clock.UtcNow))
            {
                ack = CommandAck.Failure(ClubShell.Contracts.Ipc.IpcError.Timeout("Command expired before delivery"));
            }
            else
            {
                try
                {
                    ack = await _commandSink.HandleAsync(command, cancellationToken).ConfigureAwait(false);
                }
                catch (ClubShell.Contracts.Ipc.IpcException ex)
                {
                    ack = CommandAck.Failure(ex.Error);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Pending command {Id} ({Type}) handler threw", command.Id, command.Type);
                    ack = CommandAck.Failure(ClubShell.Contracts.Ipc.IpcError.Internal(command.Id.ToString("D")));
                }
            }

            try
            {
                await _server.AckCommandAsync(pcId, command.Id, ack, cancellationToken).ConfigureAwait(false);
            }
            catch (ServerApiException ex)
            {
                _logger.LogWarning("Acking command {Id} failed: {Code}", command.Id, ex.Code);
            }
        }
    }
}
