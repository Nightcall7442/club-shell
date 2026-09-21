using System.Runtime.Versioning;
using ClubShell.Agent.Games;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using ClubShell.Windows.Registry;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Agent.Watchdog;

/// <summary>A launched Shell process in the interactive session.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Handle">Process handle (owned by <see cref="ShellLauncher"/>).</param>
/// <param name="SessionId">WTS session the Shell runs in.</param>
/// <param name="StartedAt">Launch time (UTC).</param>
public sealed record ShellProcess(int Pid, SafeProcessHandle Handle, uint SessionId, DateTimeOffset StartedAt);

/// <summary>
/// Launches the kiosk Shell (<c>shell.exePath</c>) into the interactive console session with the kiosk user's primary
/// token (<c>CreateProcessAsUser</c>, desktop <c>winsta0\default</c>, working dir = Shell install dir) and the
/// environment variables the Shell needs (<c>CLUBSHELL_PIPE</c>, <c>CLUBSHELL_SHELL_TOKEN</c>, <c>CLUBSHELL_LOCALE</c>).
/// Owns the single Shell process, stops it gracefully (WM_CLOSE then terminate) and can drop an <c>explorer.exe</c>
/// fallback shell for safe mode. Implements <see cref="IShellRelauncher"/> (stop + suspend / resume + relaunch),
/// <see cref="IKioskSessionLocator"/> and <see cref="IKioskProfilePaths"/> for the Games/Users subsystems.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellLauncher : IShellRelauncher, IKioskSessionLocator, IKioskProfilePaths, IDisposable
{
    private const string PipeEnvVar = "CLUBSHELL_PIPE";
    private const string TokenEnvVar = "CLUBSHELL_SHELL_TOKEN";
    private const string LocaleEnvVar = "CLUBSHELL_LOCALE";

    private readonly ProcessLauncher _processes;
    private readonly IKioskCredentials _kiosk;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<ShellLauncher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LaunchedProcess? _launched;
    private JobObject? _job;
    private ShellProcess? _current;
    private LaunchedProcess? _fallback;
    private volatile bool _suspended;
    private bool _disposed;

    /// <summary>Creates the launcher.</summary>
    public ShellLauncher(
        ProcessLauncher processes,
        IKioskCredentials kiosk,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<ShellLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _processes = processes;
        _kiosk = kiosk;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>UI locale passed to the Shell as <c>CLUBSHELL_LOCALE</c>. The Shell still reads its own <c>shell.json</c>.</summary>
    public string Locale { get; set; } = "en";

    /// <summary>Graceful window given to the Shell before it is terminated.</summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>When <see langword="true"/> the Job keeps the Shell alive after the Agent exits (default). Set <see langword="false"/> to tie the Shell's lifetime to the Agent.</summary>
    public bool KeepShellOnAgentExit { get; set; } = true;

    /// <summary>The Shell process currently tracked, or <see langword="null"/> when none is running.</summary>
    public ShellProcess? Current => Volatile.Read(ref _current);

    /// <summary><see langword="true"/> while a tracked Shell process is alive.</summary>
    public bool IsRunning
    {
        get
        {
            var launched = Volatile.Read(ref _launched);
            return launched is not null && !launched.HasExited;
        }
    }

    /// <summary><see langword="true"/> while automatic restarts are suspended (see <see cref="IShellRelauncher"/>).</summary>
    public bool RestartsSuspended => _suspended;

    /// <inheritdoc />
    public string KioskUser => _kiosk.UserName;

    /// <inheritdoc />
    public int? ActiveSessionId
    {
        get
        {
            try
            {
                return WtsSessions.FindByUser(KioskUser) is { IsActive: true } session ? (int)session.Id : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Enumerating WTS sessions for {User} failed", KioskUser);
                return null;
            }
        }
    }

    /// <inheritdoc />
    public string UserProfile
    {
        get
        {
            var sid = _kiosk.Sid;
            if (!string.IsNullOrEmpty(sid) && RegistryHelper.GetProfileImagePath(sid) is { Length: > 0 } path)
            {
                return path;
            }

            var usersRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            return Path.Combine(usersRoot, "Users", KioskUser);
        }
    }

    /// <inheritdoc />
    public string LocalAppData => Path.Combine(UserProfile, "AppData", "Local");

    /// <inheritdoc />
    public string RoamingAppData => Path.Combine(UserProfile, "AppData", "Roaming");

    /// <summary>
    /// Ensures the Shell is running in the active kiosk session and returns the tracked process. Idempotent: returns the
    /// current process when one is already alive, and <see langword="null"/> when no active kiosk session is present.
    /// </summary>
    public async Task<ShellProcess?> LaunchAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
            {
                return _current;
            }

            if (ActiveSessionId is not { } sessionId)
            {
                _logger.LogDebug("No active kiosk session for {User}; deferring Shell launch", KioskUser);
                return null;
            }

            var settings = _settings.CurrentValue;
            var exe = settings.Shell.ExePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                _logger.LogError("Shell executable '{Exe}' not found; cannot launch", exe);
                return null;
            }

            DisposeProcess();
            var job = JobObject.Create(name: null, killOnClose: !KeepShellOnAgentExit);
            var spec = new ProcessStartSpec
            {
                Exe = exe,
                WorkingDir = Path.GetDirectoryName(exe),
                Env = BuildEnvironment(settings),
                SessionId = (uint)sessionId,
                Job = job,
            };

            LaunchedProcess launched;
            try
            {
                launched = await _processes.LaunchAsync(spec, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                job.Dispose();
                throw;
            }

            _job = job;
            _launched = launched;
            _current = new ShellProcess(launched.Pid, launched.Handle, (uint)sessionId, launched.StartedAt);
            _logger.LogInformation("Shell launched as pid {Pid} in session {Session}", launched.Pid, sessionId);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Waits for the tracked Shell process to exit; completes immediately when none is tracked.</summary>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var launched = Volatile.Read(ref _launched);
        return launched is null ? Task.FromResult(-1) : launched.WaitForExitAsync(cancellationToken);
    }

    /// <summary>Stops the tracked Shell (WM_CLOSE within <see cref="StopGrace"/>, then terminate).</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Launches <c>explorer.exe</c> in the kiosk session as a usable fallback shell (safe mode).</summary>
    public async Task<bool> LaunchExplorerAsync(CancellationToken cancellationToken)
    {
        if (ActiveSessionId is not { } sessionId)
        {
            return false;
        }

        var explorer = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? "C:\\Windows", "explorer.exe");
        try
        {
            _fallback?.Dispose();
            _fallback = await _processes.LaunchAsync(
                new ProcessStartSpec { Exe = explorer, SessionId = (uint)sessionId },
                cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Fallback shell explorer.exe launched as pid {Pid} in session {Session}", _fallback.Pid, sessionId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Launching the fallback shell failed");
            return false;
        }
    }

    /// <inheritdoc />
    async Task IShellRelauncher.StopAsync(CancellationToken cancellationToken)
    {
        _suspended = true;
        _logger.LogInformation("Shell restarts suspended");
        await StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    async Task IShellRelauncher.RelaunchAsync(CancellationToken cancellationToken)
    {
        _suspended = false;
        _logger.LogInformation("Shell restarts resumed");
        _ = await LaunchAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeProcess();
        _fallback?.Dispose();
        _fallback = null;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task StopCurrentAsync(CancellationToken cancellationToken)
    {
        if (_launched is { } launched)
        {
            try
            {
                _ = await launched.CloseAsync(StopGrace, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Stopping the Shell (pid {Pid}) failed", launched.Pid);
            }
        }

        DisposeProcess();
    }

    private void DisposeProcess()
    {
        _launched?.Dispose();
        _launched = null;
        _job?.Dispose();
        _job = null;
        _current = null;
    }

    private Dictionary<string, string> BuildEnvironment(AgentSettings settings) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PipeEnvVar] = settings.Ipc.PipeName,
        [TokenEnvVar] = settings.ShellTokenPath,
        [LocaleEnvVar] = Locale,
    };
}
