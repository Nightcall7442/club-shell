using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games.Launchers;

/// <summary>What a launcher backend runs to start a game.</summary>
/// <param name="Exe">Executable (launcher client or the game itself).</param>
/// <param name="Args">Arguments after any injected launcher arguments.</param>
/// <param name="WorkingDir">Working directory (defaults to the executable's directory).</param>
/// <param name="WaitForGameProcess"><see langword="true"/> when <paramref name="Exe"/> is a launcher client and the real game process must be discovered.</param>
public sealed record LaunchCommand(string Exe, string? Args, string? WorkingDir, bool WaitForGameProcess = true);

/// <summary>
/// Shared launcher plumbing: install check, kiosk-session process creation through <see cref="ProcessLauncher"/>,
/// discovery of the real game process (by expected image names or install directory, via
/// <see cref="ProcessWatcher"/> events plus periodic snapshots), and tree kill.
/// </summary>
[SupportedOSPlatform("windows")]
public abstract class LauncherBase : IGameLauncher
{
    /// <summary>
    /// <see cref="LaunchContext.Env"/> key whose value is placed on the launcher command line before the launch
    /// command (e.g. Steam <c>-login</c>). It is removed from the environment block and never reaches the process.
    /// </summary>
    public const string LauncherArgsEnvKey = "CLUBSHELL_LAUNCHER_ARGS";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // ponytail: substring denylist for helper executables living next to the game; make it per-game catalogue data if it misfires.
    private static readonly string[] HelperNameFragments =
    {
        "crashhandler", "crashreport", "crashsender", "crashpad", "unitycrash", "vcredist", "dxsetup", "dotnetfx",
        "easyanticheat_setup", "beservice", "redist", "installer", "uninstall", "_setup", "launcher_helper",
    };

    private readonly ProcessLauncher _processes;
    private readonly ProcessKiller _killer;
    private readonly ProcessWatcher _watcher;

    /// <summary>Initializes the shared plumbing.</summary>
    protected LauncherBase(
        ProcessLauncher processes,
        ProcessKiller killer,
        ProcessWatcher watcher,
        GameDetector detector,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger logger)
    {
        _processes = processes;
        _killer = killer;
        _watcher = watcher;
        Detector = detector;
        Settings = settings;
        Clock = clock;
        Logger = logger;
    }

    /// <inheritdoc />
    public abstract LauncherType Launcher { get; }

    /// <summary>Install detector.</summary>
    protected GameDetector Detector { get; }

    /// <summary>Agent settings.</summary>
    protected IOptionsMonitor<AgentSettings> Settings { get; }

    /// <summary>Clock.</summary>
    protected IClock Clock { get; }

    /// <summary>Logger of the concrete launcher.</summary>
    protected ILogger Logger { get; }

    /// <inheritdoc />
    public virtual Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Detector.ResolveLauncherExe(Launcher) is not null);

    /// <inheritdoc />
    public Task<GameInstallStatus> GetInstallStatusAsync(Game game, CancellationToken cancellationToken) => Detector.DetectAsync(game, cancellationToken);

    /// <inheritdoc />
    public async Task<LaunchResult> LaunchAsync(Game game, LaunchRequest request, AccountLease? lease, LaunchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        DateTimeOffset startedAt = Clock.UtcNow;
        Guid? leaseId = lease?.LeaseId;

        GameInstallStatus status = await Detector.DetectAsync(game, cancellationToken).ConfigureAwait(false);
        if (!status.Installed)
        {
            return LaunchResult.Failure(IpcError.GameNotInstalled(game.Id), startedAt, leaseId);
        }

        Game resolved = game with { Installed = true, InstallPath = status.InstallPath ?? game.InstallPath };
        LaunchCommand? command;
        try
        {
            command = BuildCommand(resolved, request, context);
        }
        catch (IpcException ex)
        {
            return LaunchResult.Failure(ex.Error, startedAt, leaseId);
        }

        if (command is null)
        {
            return LaunchResult.Failure(IpcError.GameLaunchFailed("launcher", $"{Launcher} client is not installed"), startedAt, leaseId);
        }

        (IReadOnlyDictionary<string, string> env, string? launcherArgs) = SplitEnv(context.Env);
        uint sessionId = (uint)context.WtsSessionId;
        IReadOnlyList<string> expectedNames = ExpectedProcessNames(resolved);
        HashSet<int> before = command.WaitForGameProcess ? SnapshotMatchingPids(resolved, expectedNames, sessionId) : new HashSet<int>();

        var spec = new ProcessStartSpec
        {
            Exe = command.Exe,
            Args = JoinArgs(launcherArgs, command.Args),
            WorkingDir = command.WorkingDir ?? Path.GetDirectoryName(command.Exe),
            Env = env,
            SessionId = sessionId,
        };

        LaunchedProcess launched;
        try
        {
            launched = await _processes.LaunchAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Logger.LogWarning(ex, "{Launcher}: cannot start {Exe} in session {SessionId}", Launcher, command.Exe, sessionId);
            return LaunchResult.Failure(IpcError.GameLaunchFailed("start", ex.Message), startedAt, leaseId);
        }

        using (launched)
        {
            if (!command.WaitForGameProcess)
            {
                return LaunchResult.Success(launched.Pid, startedAt, leaseId);
            }

            int timeoutSec = request.LaunchTimeoutSec > 0 ? request.LaunchTimeoutSec : Settings.CurrentValue.Games.LaunchTimeoutSec;
            int? pid = await WaitForGameProcessAsync(resolved, expectedNames, sessionId, before, TimeSpan.FromSeconds(timeoutSec), cancellationToken).ConfigureAwait(false);
            if (pid is null)
            {
                int? exit = launched.HasExited ? launched.ExitCode : null;
                Logger.LogWarning("{Launcher}: game process for {Title} did not appear within {Timeout}s (launcher exit code {ExitCode})", Launcher, game.Title, timeoutSec, exit);
                return LaunchResult.Failure(
                    IpcError.GameLaunchFailed("wait", $"Game process did not start within {timeoutSec}s", exit is not null and not 0 ? exit : null),
                    startedAt,
                    leaseId);
            }

            return LaunchResult.Success(pid.Value, startedAt, leaseId);
        }
    }

    /// <inheritdoc />
    public virtual Task KillAsync(int pid, bool force, CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            TimeSpan grace = force ? TimeSpan.Zero : TimeSpan.FromSeconds(Settings.CurrentValue.Games.KillGraceSec);
            IReadOnlyList<KilledProcess> killed = _killer.KillTree(pid, grace);
            Logger.LogInformation("{Launcher}: killed {Count} processes of tree {Pid} (force={Force})", Launcher, killed.Count, pid, force);
        },
        cancellationToken);

    /// <summary>Builds the command for <paramref name="game"/>; <see langword="null"/> when the launcher client is missing. May throw <see cref="IpcException"/>.</summary>
    protected abstract LaunchCommand? BuildCommand(Game game, LaunchRequest request, LaunchContext context);

    /// <summary>Image names (with or without <c>.exe</c>) that identify the game process; defaults to <see cref="Game.ExePath"/>'s file name.</summary>
    protected virtual IReadOnlyList<string> ExpectedProcessNames(Game game)
    {
        string? name = string.IsNullOrWhiteSpace(game.ExePath) ? null : Path.GetFileNameWithoutExtension(game.ExePath);
        return name is null ? Array.Empty<string>() : new[] { name };
    }

    /// <summary>Joins non-empty argument fragments with single spaces.</summary>
    protected static string? JoinArgs(params string?[] parts)
    {
        string joined = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return joined.Length == 0 ? null : joined;
    }

    private static (IReadOnlyDictionary<string, string> Env, string? LauncherArgs) SplitEnv(IReadOnlyDictionary<string, string> env)
    {
        if (!env.TryGetValue(LauncherArgsEnvKey, out string? launcherArgs))
        {
            return (env, null);
        }

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in env)
        {
            if (!key.Equals(LauncherArgsEnvKey, StringComparison.OrdinalIgnoreCase))
            {
                copy[key] = value;
            }
        }

        return (copy, launcherArgs);
    }

    private static bool NameMatches(string imageName, IReadOnlyList<string> expectedNames)
    {
        string bare = Path.GetFileNameWithoutExtension(imageName);
        foreach (string expected in expectedNames)
        {
            if (bare.Equals(Path.GetFileNameWithoutExtension(expected), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHelper(string imageName)
    {
        string lower = imageName.ToLowerInvariant();
        return HelperNameFragments.Any(f => lower.Contains(f, StringComparison.Ordinal));
    }

    private static bool PathUnderInstall(string? imagePath, string? installPath)
    {
        if (string.IsNullOrEmpty(imagePath) || string.IsNullOrEmpty(installPath))
        {
            return false;
        }

        string root = Path.GetFullPath(installPath).TrimEnd('\\') + "\\";
        return imagePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGameProcess(Game game, IReadOnlyList<string> expectedNames, string imageName, Func<string?> imagePath)
    {
        if (NameMatches(imageName, expectedNames))
        {
            return true;
        }

        // Path-based fallback only when the catalogue names no executable: with names known, a bootstrapper in the
        // install directory must not be mistaken for the game.
        return expectedNames.Count == 0 && !IsHelper(imageName) && PathUnderInstall(imagePath(), game.InstallPath);
    }

    private static string? TryMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static uint TrySessionId(Process process)
    {
        try
        {
            return (uint)process.SessionId;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return uint.MaxValue;
        }
    }

    private static HashSet<int> SnapshotMatchingPids(Game game, IReadOnlyList<string> expectedNames, uint sessionId)
    {
        var pids = new HashSet<int>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (TrySessionId(process) == sessionId && IsGameProcess(game, expectedNames, process.ProcessName, () => TryMainModulePath(process)))
                {
                    pids.Add(process.Id);
                }
            }
        }

        return pids;
    }

    private async Task<int?> WaitForGameProcessAsync(Game game, IReadOnlyList<string> expectedNames, uint sessionId, HashSet<int> before, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStarted(object? sender, ProcessStartedEventArgs e)
        {
            if (e.SessionId == sessionId && !before.Contains(e.Pid) && IsGameProcess(game, expectedNames, e.Name, () => e.Path))
            {
                found.TrySetResult(e.Pid);
            }
        }

        _watcher.ProcessStarted += OnStarted;
        try
        {
            _watcher.Start();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "Process watcher unavailable; relying on snapshots only");
        }

        long started = Clock.GetTimestamp();
        try
        {
            while (true)
            {
                foreach (Process process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (before.Contains(process.Id) || TrySessionId(process) != sessionId)
                        {
                            continue;
                        }

                        if (IsGameProcess(game, expectedNames, process.ProcessName, () => TryMainModulePath(process)))
                        {
                            Logger.LogInformation("{Launcher}: game process {Name} pid {Pid} detected for {Title}", Launcher, process.ProcessName, process.Id, game.Title);
                            return process.Id;
                        }
                    }
                }

                if (found.Task.IsCompletedSuccessfully)
                {
                    int pid = found.Task.Result;
                    Logger.LogInformation("{Launcher}: game process pid {Pid} detected for {Title} (watcher)", Launcher, pid, game.Title);
                    return pid;
                }

                if (Clock.GetElapsedTime(started) >= timeout)
                {
                    return null;
                }

                await Task.WhenAny(found.Task, Task.Delay(PollInterval, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            _watcher.ProcessStarted -= OnStarted;
        }
    }
}

/// <summary>Plain executable: the launched process is the game itself (no launcher client, no process discovery).</summary>
[SupportedOSPlatform("windows")]
public sealed class ExeLauncher : LauncherBase
{
    /// <summary>Creates the launcher.</summary>
    public ExeLauncher(ProcessLauncher processes, ProcessKiller killer, ProcessWatcher watcher, GameDetector detector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<ExeLauncher> logger)
        : base(processes, killer, watcher, detector, settings, clock, logger)
    {
    }

    /// <inheritdoc />
    public override LauncherType Launcher => LauncherType.Exe;

    /// <inheritdoc />
    public override Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <inheritdoc />
    protected override LaunchCommand? BuildCommand(Game game, LaunchRequest request, LaunchContext context)
    {
        string exe = GameDetector.ResolveExe(game, game.InstallPath) ?? throw IpcError.GameNotInstalled(game.Id).ToException();
        return new LaunchCommand(exe, JoinArgs(game.Args, request.ExtraArgs), Path.GetDirectoryName(exe), WaitForGameProcess: false);
    }
}
