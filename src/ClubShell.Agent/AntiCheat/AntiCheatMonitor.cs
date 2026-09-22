using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Registry;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace ClubShell.Agent.AntiCheat;

/// <summary>Pre-launch anti-cheat gate consulted by the Games module.</summary>
public interface IAntiCheatGate
{
    /// <summary>
    /// Runs the checks required for <paramref name="game"/> (its own anti-cheat plus <c>policy.anticheat.required</c>
    /// and the platform prerequisites). Returns every result; throws <see cref="IpcException"/>
    /// (<c>antiCheatBlocked</c>) when a check failed and <c>policy.anticheat.blockOnViolation</c> is on.
    /// </summary>
    Task<IReadOnlyList<AntiCheatCheckResult>> CheckForLaunchAsync(Game game, CancellationToken cancellationToken);
}

/// <summary>One anti-cheat subsystem probe.</summary>
public interface IAntiCheatChecker
{
    /// <summary>Subsystem covered (<see cref="AntiCheatKind.None"/> = platform prerequisites).</summary>
    AntiCheatKind Kind { get; }

    /// <summary>Pre-launch check (installed, driver present, prerequisites).</summary>
    Task<AntiCheatCheckResult> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Check while the game with <paramref name="gamePid"/> runs (service / driver alive alongside it).</summary>
    Task<AntiCheatCheckResult> CheckRuntimeAsync(int gamePid, CancellationToken cancellationToken);
}

/// <summary>Publishes Agent → server events (WS, queued offline); implemented by the realtime module.</summary>
public interface IAgentEventSink
{
    /// <summary>Publishes <paramref name="agentEvent"/>.</summary>
    ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken cancellationToken);
}

/// <summary>Running-game access for runtime checks (implemented by the Games module; optional).</summary>
public interface IAntiCheatGameControl
{
    /// <summary>Games currently tracked by the Games module.</summary>
    IReadOnlyList<RunningGame> Running { get; }

    /// <summary>Terminates the game with <paramref name="pid"/>.</summary>
    Task KillAsync(int pid, bool force, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the registered <see cref="IAntiCheatChecker"/>s before a launch (<see cref="IAntiCheatGate"/>) and every
/// <c>anticheat.checkIntervalSec</c> while games with an anti-cheat run. Violations are reported to the server
/// (<c>POST /anticheat/report</c>, when <c>anticheat.reportViolations</c>) and as the <c>anticheatViolation</c> agent
/// event; with <c>policy.anticheat.blockOnViolation</c> the launch is refused, a running game is killed and, for
/// critical findings, the session is locked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AntiCheatMonitor : BackgroundService, IAntiCheatGate
{
    private static readonly JsonSerializerOptions DetailsOptions = CreateDetailsOptions();

    private readonly IAntiCheatChecker[] _checkers;
    private readonly IServerClient _server;
    private readonly IAgentEventSink _events;
    private readonly ISessionService _sessions;
    private readonly IPolicyEnforcer _policies;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<AntiCheatMonitor> _logger;
    private readonly IAntiCheatGameControl? _games;
    private readonly ConcurrentDictionary<Guid, AntiCheatKind> _kinds = new();
    private readonly ConcurrentDictionary<(int Pid, string Check), byte> _reported = new();

    /// <summary>Creates the monitor; <paramref name="games"/> enables runtime checks when registered.</summary>
    public AntiCheatMonitor(IEnumerable<IAntiCheatChecker> checkers, IServerClient server, IAgentEventSink events, ISessionService sessions, IPolicyEnforcer policies, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<AntiCheatMonitor> logger, IAntiCheatGameControl? games = null)
    {
        ArgumentNullException.ThrowIfNull(checkers);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _checkers = checkers.ToArray();
        _server = server;
        _events = events;
        _sessions = sessions;
        _policies = policies;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _games = games;
    }

    /// <summary>Kinds with a registered checker.</summary>
    public IReadOnlyList<AntiCheatKind> SupportedKinds => _checkers.Select(c => c.Kind).Distinct().ToArray();

    /// <inheritdoc />
    public async Task<IReadOnlyList<AntiCheatCheckResult>> CheckForLaunchAsync(Game game, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        var policy = _policies.Current?.Anticheat;
        var anticheat = _settings.CurrentValue.Anticheat;
        _kinds[game.Id] = game.AntiCheat;

        var kinds = new List<AntiCheatKind>();
        if (policy is not null)
        {
            kinds.AddRange(policy.Required.Where(k => k != AntiCheatKind.None));
        }

        if (game.AntiCheat != AntiCheatKind.None && !kinds.Contains(game.AntiCheat))
        {
            kinds.Add(game.AntiCheat);
        }

        if (kinds.Count > 0 || anticheat.RequireSecureBoot || anticheat.RequireTpm || anticheat.RequireHvci)
        {
            kinds.Insert(0, AntiCheatKind.None);
        }

        var results = new List<AntiCheatCheckResult>(kinds.Count);
        var failures = new List<AntiCheatCheckResult>();
        foreach (var kind in kinds)
        {
            var checker = Find(kind);
            if (checker is null)
            {
                _logger.LogDebug("No checker registered for {Kind}; treated as satisfied", kind);
                continue;
            }

            AntiCheatCheckResult result;
            try
            {
                result = await checker.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Anti-cheat checker {Kind} failed; treated as satisfied", kind);
                result = new AntiCheatCheckResult(kind, true);
            }

            results.Add(result);
            if (!result.Ok)
            {
                failures.Add(result);
            }
        }

        if (failures.Count == 0)
        {
            return results;
        }

        var block = policy?.BlockOnViolation ?? true;
        foreach (var failure in failures)
        {
            var check = failure.Reason ?? "unknown";
            await ReportAsync(failure.Kind, check, SeverityOf(check), Details(("gameId", game.Id.ToString("D")), ("title", game.Title), ("phase", "launch")), game.Id, block ? AntiCheatAction.BlockedLaunch : AntiCheatAction.None, cancellationToken).ConfigureAwait(false);
        }

        if (block)
        {
            var first = failures[0];
            throw IpcError.AntiCheatBlocked(first.Kind, first.Reason ?? "unknown").ToException();
        }

        return results;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_games is null)
        {
            _logger.LogInformation("Anti-cheat runtime checks disabled: no game control registered");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(5, _settings.CurrentValue.Anticheat.CheckIntervalSec));
            try
            {
                await _clock.Delay(interval, stoppingToken).ConfigureAwait(false);
                await CheckRunningAsync(_games, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anti-cheat runtime check failed");
            }
        }
    }

    private static JsonSerializerOptions CreateDetailsOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static JsonElement Details(params (string Key, string? Value)[] pairs)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            map[key] = value;
        }

        return JsonSerializer.SerializeToElement(map, DetailsOptions);
    }

    private static AntiCheatSeverity SeverityOf(string check) => check switch
    {
        AntiCheatChecks.TestSigningOn or AntiCheatChecks.InjectedModule or AntiCheatChecks.DebuggerAttached
            or AntiCheatChecks.BlockedProcess or AntiCheatChecks.VmDetected
            or AntiCheatChecks.CodeIntegrityOff or AntiCheatChecks.KernelDebugOn => AntiCheatSeverity.Critical,
        _ => AntiCheatSeverity.Warning,
    };

    private IAntiCheatChecker? Find(AntiCheatKind kind) => Array.Find(_checkers, c => c.Kind == kind);

    private async Task CheckRunningAsync(IAntiCheatGameControl games, CancellationToken cancellationToken)
    {
        var running = games.Running;
        var livePids = new HashSet<int>(running.Count);
        foreach (var game in running)
        {
            livePids.Add(game.Pid);
            if (game.State != GameState.Running || !_kinds.TryGetValue(game.GameId, out var kind) || kind == AntiCheatKind.None)
            {
                continue;
            }

            var checker = Find(kind);
            if (checker is null)
            {
                continue;
            }

            AntiCheatCheckResult result;
            try
            {
                result = await checker.CheckRuntimeAsync(game.Pid, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Runtime anti-cheat check {Kind} for pid {Pid} failed", kind, game.Pid);
                continue;
            }

            if (!result.Ok)
            {
                await HandleRuntimeViolationAsync(games, game, result, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var key in _reported.Keys)
        {
            if (!livePids.Contains(key.Pid))
            {
                _ = _reported.TryRemove(key, out _);
            }
        }
    }

    private async Task HandleRuntimeViolationAsync(IAntiCheatGameControl games, RunningGame game, AntiCheatCheckResult result, CancellationToken cancellationToken)
    {
        var check = result.Reason ?? "unknown";
        if (!_reported.TryAdd((game.Pid, check), 0))
        {
            return;
        }

        var block = _policies.Current?.Anticheat.BlockOnViolation ?? true;
        var severity = SeverityOf(check);
        var action = AntiCheatAction.None;
        if (block)
        {
            try
            {
                await games.KillAsync(game.Pid, force: true, cancellationToken).ConfigureAwait(false);
                action = AntiCheatAction.KilledGame;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Game {Title} (pid {Pid}) could not be killed after an anti-cheat violation", game.Title, game.Pid);
            }

            if (severity == AntiCheatSeverity.Critical && _sessions.State.IsOpen())
            {
                try
                {
                    _ = await _sessions.LockAsync("anticheat:" + check, cancellationToken).ConfigureAwait(false);
                    action = AntiCheatAction.LockedSession;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Session could not be locked after an anti-cheat violation");
                }
            }
        }

        await ReportAsync(result.Kind, check, severity, Details(("gameId", game.GameId.ToString("D")), ("title", game.Title), ("pid", game.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("phase", "runtime")), game.GameId, action, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReportAsync(AntiCheatKind kind, string check, AntiCheatSeverity severity, JsonElement details, Guid? gameId, AntiCheatAction action, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var session = _sessions.Current;
        var report = new AntiCheatReport(_settings.CurrentValue.PcId ?? Guid.Empty, session?.Id, session?.UserId, gameId, kind, check, severity, details, now, action);
        _logger.LogWarning("Anti-cheat violation {Kind}/{Check} ({Severity}), action {Action}", kind, check, severity, action);

        try
        {
            await _events.PublishAsync(AgentEvent.Of(AgentEventType.AnticheatViolation, now, report), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "anticheatViolation event could not be published");
        }

        if (!_settings.CurrentValue.Anticheat.ReportViolations)
        {
            return;
        }

        try
        {
            await _server.ReportAntiCheatAsync(report, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServerApiException or HttpRequestException or System.TimeoutException)
        {
            _logger.LogWarning(ex, "Anti-cheat report could not be sent to the server");
        }
    }
}

/// <summary>Installed / running state of a Windows service or kernel driver.</summary>
/// <param name="Name">Service key name.</param>
/// <param name="Installed">Service key exists.</param>
/// <param name="Status">SCM status, when queryable.</param>
/// <param name="ImagePath">Normalised binary path from <c>ImagePath</c>, when set.</param>
/// <param name="ImageExists">Whether the binary exists on disk.</param>
/// <param name="FileVersion">File version of the binary, when readable.</param>
internal sealed record ServiceInfo(string Name, bool Installed, ServiceControllerStatus? Status, string? ImagePath, bool ImageExists, string? FileVersion)
{
    /// <summary><see langword="true"/> when the SCM reports the service or driver as running.</summary>
    public bool IsRunning => Status == ServiceControllerStatus.Running;
}

/// <summary>Registry / SCM / process probes shared by the checkers (no cheat signatures: only the integrity of the anti-cheat components themselves).</summary>
[SupportedOSPlatform("windows")]
internal static class AntiCheatProbe
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Queries service <paramref name="name"/> (Win32 service or kernel driver).</summary>
    public static ServiceInfo QueryService(string name)
    {
        var key = ServicesKey + "\\" + name;
        var installed = RegistryHelper.Exists(RegistryHive.LocalMachine, key);
        if (!installed)
        {
            return new ServiceInfo(name, false, null, null, false, null);
        }

        var imagePath = NormalizeImagePath(RegistryHelper.Get<string>(RegistryHive.LocalMachine, key, "ImagePath"));
        var exists = imagePath is not null && File.Exists(imagePath);
        ServiceControllerStatus? status = null;
        try
        {
            using var controller = new ServiceController(name);
            status = controller.Status;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Not registered with the SCM (or access denied); registry state is all we have.
        }

        return new ServiceInfo(name, true, status, imagePath, exists, exists ? ReadFileVersion(imagePath!) : null);
    }

    /// <summary><see langword="true"/> when a process with <paramref name="processName"/> (without extension) is alive.</summary>
    public static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Start time of <paramref name="pid"/>, or <see langword="null"/> when it is gone or inaccessible.</summary>
    public static DateTimeOffset? ProcessStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary><see langword="true"/> when the process started less than <paramref name="grace"/> ago (anti-cheat still bootstrapping).</summary>
    public static bool IsWithinStartupGrace(int pid, TimeSpan grace, DateTimeOffset now) =>
        ProcessStartTime(pid) is { } started && now - started < grace;

    /// <summary><see langword="true"/> when <paramref name="path"/> carries an Authenticode signature (chain not evaluated).</summary>
    public static bool HasAuthenticodeSignature(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Turns an <c>ImagePath</c> value (<c>\??\C:\...</c>, <c>\SystemRoot\...</c>, <c>system32\drivers\x.sys</c>, quoted paths with arguments) into an absolute file path.</summary>
    public static string? NormalizeImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            text = end > 1 ? text[1..end] : text.Trim('"');
        }
        else
        {
            var exe = text.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
            if (exe > 0)
            {
                text = text[..(exe + 4)];
            }
        }

        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            text = Path.Combine(windows, text[@"\SystemRoot\".Length..]);
        }
        else if (!Path.IsPathRooted(text))
        {
            text = Path.Combine(windows, text);
        }

        return text;
    }

    private static string? ReadFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
