using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ClubShell.Windows.Native;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Windows.Processes;

/// <summary>What to launch and how (<see cref="ProcessLauncher.LaunchAsync"/>).</summary>
public sealed record ProcessStartSpec
{
    /// <summary>Full path of the executable.</summary>
    public required string Exe { get; init; }

    /// <summary>Command-line arguments, already escaped.</summary>
    public string? Args { get; init; }

    /// <summary>Working directory; defaults to the executable's directory.</summary>
    public string? WorkingDir { get; init; }

    /// <summary>Extra environment variables merged over the target user's block.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Interactive WTS session to launch into (uses the logged-on user's token). Requires LocalSystem.</summary>
    public uint? SessionId { get; init; }

    /// <summary>Explicit primary token to launch with (wins over <see cref="SessionId"/>). Not disposed by the launcher.</summary>
    public SafeTokenHandle? RunAsUser { get; init; }

    /// <summary>Priority class (NORMAL_PRIORITY_CLASS by default).</summary>
    public uint Priority { get; init; } = NativeConst.NORMAL_PRIORITY_CLASS;

    /// <summary>Job the new process is assigned to right after creation.</summary>
    public JobObject? Job { get; init; }

    /// <summary>Start without a visible window / console.</summary>
    public bool Hidden { get; init; }

    /// <summary>When &gt; 0, wait up to this long for the process to show a top-level window before returning.</summary>
    public int WaitForInputIdleMs { get; init; }
}

/// <summary>A process started by <see cref="ProcessLauncher"/>; owns the process handle.</summary>
[SupportedOSPlatform("windows")]
public sealed class LaunchedProcess : IDisposable
{
    private nint _mainWindow;
    private bool _disposed;

    internal LaunchedProcess(int pid, SafeProcessHandle handle, string exe, DateTimeOffset startedAt)
    {
        Pid = pid;
        Handle = handle;
        Exe = exe;
        StartedAt = startedAt;
    }

    /// <summary>Process id.</summary>
    public int Pid { get; }

    /// <summary>Process handle (SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE at least).</summary>
    public SafeProcessHandle Handle { get; }

    /// <summary>Executable path.</summary>
    public string Exe { get; }

    /// <summary>Launch time (UTC).</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>First visible unowned top-level window of the process, resolved lazily; 0 while none exists.</summary>
    public nint MainWindowHandle
    {
        get
        {
            if (_mainWindow == 0 || !User32.IsWindow(_mainWindow))
            {
                _mainWindow = ProcessWindows.FindMainWindow((uint)Pid);
            }

            return _mainWindow;
        }
    }

    /// <summary><see langword="true"/> once the process has exited.</summary>
    public bool HasExited => Kernel32.GetExitCodeProcess(Handle, out uint code) && code != NativeConst.STILL_ACTIVE;

    /// <summary>Exit code, or <see langword="null"/> while running / when unknown.</summary>
    public int? ExitCode => Kernel32.GetExitCodeProcess(Handle, out uint code) && code != NativeConst.STILL_ACTIVE ? unchecked((int)code) : null;

    /// <summary>Waits for exit and returns the exit code.</summary>
    public Task<int> WaitForExitAsync(CancellationToken ct) => ProcessWait.WaitForExitAsync(Handle, ct);

    /// <summary>
    /// Posts WM_CLOSE to every top-level window of the process, waits up to <paramref name="gracefulTimeout"/>,
    /// then terminates it. Returns <see langword="true"/> when the process closed on its own.
    /// </summary>
    public async Task<bool> CloseAsync(TimeSpan gracefulTimeout, CancellationToken ct)
    {
        if (HasExited)
        {
            return true;
        }

        if (ProcessWindows.PostClose((uint)Pid) > 0 && gracefulTimeout > TimeSpan.Zero)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(gracefulTimeout);
            try
            {
                await WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // graceful window elapsed; fall through to Kill
            }
        }

        Kill();
        return false;
    }

    /// <summary>TerminateProcess.</summary>
    public void Kill(uint exitCode = 1)
    {
        if (!Kernel32.TerminateProcess(Handle, exitCode) && !HasExited)
        {
            Win32Error.ThrowLastError(nameof(Kernel32.TerminateProcess));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Handle.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Starts processes either in the Agent's own context (<see cref="Process.Start()"/>) or, when a session id or
/// token is given, in an interactive session through <see cref="ProcessAsUser"/> (ARCHITECTURE.md §2 rule 3).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessLauncher
{
    private const uint DuplicateSameAccess = 0x2;
    private static readonly TimeSpan WindowPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly ILogger<ProcessLauncher> _logger;

    /// <summary>Creates a launcher.</summary>
    public ProcessLauncher(ILogger<ProcessLauncher>? logger = null) => _logger = logger ?? NullLogger<ProcessLauncher>.Instance;

    /// <summary>Launches <paramref name="spec"/>; throws <see cref="Win32Exception"/> when creation fails.</summary>
    public async Task<LaunchedProcess> LaunchAsync(ProcessStartSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Exe);
        ct.ThrowIfCancellationRequested();

        string workingDir = spec.WorkingDir ?? Path.GetDirectoryName(spec.Exe) ?? Environment.SystemDirectory;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        LaunchedProcess launched = spec.SessionId is not null || spec.RunAsUser is not null
            ? LaunchAsUser(spec, workingDir, startedAt)
            : LaunchLocal(spec, workingDir, startedAt);

        try
        {
            spec.Job?.Assign(launched.Handle);
            if (spec.Priority != NativeConst.NORMAL_PRIORITY_CLASS && !Kernel32.SetPriorityClass(launched.Handle, spec.Priority))
            {
                _logger.LogWarning("SetPriorityClass({Priority}) failed for pid {Pid}: {Error}", spec.Priority, launched.Pid, Win32Error.Message(Win32Error.Last()));
            }

            _logger.LogInformation("Launched {Exe} as pid {Pid} (session {SessionId})", spec.Exe, launched.Pid, spec.SessionId);

            if (spec.WaitForInputIdleMs > 0)
            {
                nint hwnd = await WaitForWindowAsync(launched.Pid, TimeSpan.FromMilliseconds(spec.WaitForInputIdleMs), ct).ConfigureAwait(false);
                if (hwnd == 0)
                {
                    _logger.LogDebug("Pid {Pid} showed no window within {Timeout} ms", launched.Pid, spec.WaitForInputIdleMs);
                }
            }

            return launched;
        }
        catch
        {
            launched.Dispose();
            throw;
        }
    }

    /// <summary>Polls until the process owns a visible top-level window; returns the HWND or 0 on timeout / exit.</summary>
    public static async Task<nint> WaitForWindowAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            nint hwnd = ProcessWindows.FindMainWindow((uint)pid);
            if (hwnd != 0)
            {
                return hwnd;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return 0;
            }

            using (SafeProcessHandle probe = Kernel32.OpenProcess(NativeConst.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid))
            {
                if (probe.IsInvalid || !Kernel32.GetExitCodeProcess(probe, out uint code) || code != NativeConst.STILL_ACTIVE)
                {
                    return 0;
                }
            }

            await Task.Delay(WindowPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Builds <c>"exe" args</c> for CreateProcess.</summary>
    public static string BuildCommandLine(string exe, string? args)
    {
        string quoted = exe.Contains(' ', StringComparison.Ordinal) && !exe.StartsWith('"') ? "\"" + exe + "\"" : exe;
        return string.IsNullOrWhiteSpace(args) ? quoted : quoted + " " + args;
    }

    private static LaunchedProcess LaunchAsUser(ProcessStartSpec spec, string workingDir, DateTimeOffset startedAt)
    {
        SafeTokenHandle? owned = null;
        SafeTokenHandle token = spec.RunAsUser ?? (owned = ProcessAsUser.GetUserToken(spec.SessionId.GetValueOrDefault()));
        try
        {
            using EnvironmentBlock env = EnvironmentBlock.Build(token, spec.Env);
            uint flags = NativeConst.CREATE_UNICODE_ENVIRONMENT | spec.Priority | (spec.Hidden ? NativeConst.CREATE_NO_WINDOW : NativeConst.CREATE_NEW_CONSOLE);
            (uint pid, SafeProcessHandle handle) = ProcessAsUser.CreateProcessAsUser(token, spec.Exe, spec.Args, workingDir, env.Pointer, ProcessAsUser.DefaultDesktop, flags, spec.Hidden);
            return new LaunchedProcess((int)pid, handle, spec.Exe, startedAt);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static LaunchedProcess LaunchLocal(ProcessStartSpec spec, string workingDir, DateTimeOffset startedAt)
    {
        var psi = new ProcessStartInfo(spec.Exe)
        {
            Arguments = spec.Args ?? string.Empty,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = spec.Hidden,
            WindowStyle = spec.Hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
        };
        if (spec.Env is not null)
        {
            foreach ((string key, string value) in spec.Env)
            {
                psi.Environment[key] = value;
            }
        }

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"Process.Start returned null for {spec.Exe}");
        nint self = Kernel32.GetCurrentProcess();
        Win32Error.ThrowIfFalse(
            Kernel32.DuplicateHandle(self, process.SafeHandle.DangerousGetHandle(), self, out nint dup, 0, false, DuplicateSameAccess),
            nameof(Kernel32.DuplicateHandle));
        GC.KeepAlive(process.SafeHandle);
        return new LaunchedProcess(process.Id, new SafeProcessHandle(dup, ownsHandle: true), spec.Exe, startedAt);
    }
}

/// <summary>Top-level window helpers shared by the launcher and the killer.</summary>
[SupportedOSPlatform("windows")]
internal static class ProcessWindows
{
    /// <summary>All top-level windows (visible or not) created by <paramref name="pid"/>.</summary>
    public static List<nint> TopLevelWindows(uint pid)
    {
        var result = new List<nint>();
        EnumWindowsProc callback = (hWnd, lParam) =>
        {
            _ = User32.GetWindowThreadProcessId(hWnd, out uint owner);
            if (owner == pid)
            {
                result.Add(hWnd);
            }

            return true;
        };
        _ = User32.EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        return result;
    }

    /// <summary>First visible, unowned top-level window of the process, or 0.</summary>
    public static nint FindMainWindow(uint pid)
    {
        foreach (nint hwnd in TopLevelWindows(pid))
        {
            if (User32.IsWindowVisible(hwnd) && User32.GetWindow(hwnd, NativeConst.GW_OWNER) == 0)
            {
                return hwnd;
            }
        }

        return 0;
    }

    /// <summary>Posts WM_CLOSE to every top-level window; returns how many were notified.</summary>
    public static int PostClose(uint pid)
    {
        int posted = 0;
        foreach (nint hwnd in TopLevelWindows(pid))
        {
            if (User32.PostMessageW(hwnd, NativeConst.WM_CLOSE, 0, 0))
            {
                posted++;
            }
        }

        return posted;
    }
}

/// <summary>Unicode environment block for CreateProcessAsUser: the user's block merged with extra variables.</summary>
[SupportedOSPlatform("windows")]
internal sealed class EnvironmentBlock : IDisposable
{
    private EnvironmentBlock(nint pointer) => Pointer = pointer;

    /// <summary>LPVOID lpEnvironment (double-null-terminated UTF-16 block); 0 lets CreateProcess inherit.</summary>
    public nint Pointer { get; private set; }

    /// <summary>Builds the block from <see cref="Userenv.CreateEnvironmentBlock"/> for <paramref name="token"/> plus <paramref name="extra"/>.</summary>
    public static EnvironmentBlock Build(SafeTokenHandle token, IReadOnlyDictionary<string, string>? extra)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool added = false;
        try
        {
            token.DangerousAddRef(ref added);
            if (!Userenv.CreateEnvironmentBlock(out nint native, token.DangerousGetHandle(), false))
            {
                Win32Error.ThrowLastError(nameof(Userenv.CreateEnvironmentBlock));
            }

            try
            {
                foreach (string entry in ReadBlock(native))
                {
                    int eq = entry.IndexOf('=', 1);
                    if (eq > 0)
                    {
                        variables[entry[..eq]] = entry[(eq + 1)..];
                    }
                }
            }
            finally
            {
                _ = Userenv.DestroyEnvironmentBlock(native);
            }
        }
        finally
        {
            if (added)
            {
                token.DangerousRelease();
            }
        }

        if (extra is not null)
        {
            foreach ((string key, string value) in extra)
            {
                variables[key] = value;
            }
        }

        var text = new StringBuilder();
        foreach ((string key, string value) in variables)
        {
            text.Append(key).Append('=').Append(value).Append('\0');
        }

        text.Append('\0');
        return new EnvironmentBlock(Marshal.StringToHGlobalUni(text.ToString()));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Pointer != 0)
        {
            Marshal.FreeHGlobal(Pointer);
            Pointer = 0;
        }

        GC.SuppressFinalize(this);
    }

    private static unsafe List<string> ReadBlock(nint block)
    {
        var entries = new List<string>();
        char* p = (char*)block;
        while (*p != '\0')
        {
            string entry = new(p);
            entries.Add(entry);
            p += entry.Length + 1;
        }

        return entries;
    }
}

/// <summary>Waits on a process handle without blocking a thread (RegisterWaitForSingleObject).</summary>
[SupportedOSPlatform("windows")]
internal static class ProcessWait
{
    /// <summary>Completes with the exit code once the process handle is signalled (-1 when the code cannot be read).</summary>
    public static async Task<int> WaitForExitAsync(SafeProcessHandle handle, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waitHandle = new ProcessWaitHandle(handle);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            completion,
            Timeout.Infinite,
            executeOnlyOnce: true);
        try
        {
            using (ct.Register(() => completion.TrySetCanceled(ct)))
            {
                await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            registration.Unregister(null);
        }

        return Kernel32.GetExitCodeProcess(handle, out uint code) ? unchecked((int)code) : -1;
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            bool added = false;
            try
            {
                process.DangerousAddRef(ref added);
                nint self = Kernel32.GetCurrentProcess();
                Win32Error.ThrowIfFalse(
                    Kernel32.DuplicateHandle(self, process.DangerousGetHandle(), self, out nint dup, NativeConst.SYNCHRONIZE, false, 0),
                    nameof(Kernel32.DuplicateHandle));
                SafeWaitHandle = new SafeWaitHandle(dup, ownsHandle: true);
            }
            finally
            {
                if (added)
                {
                    process.DangerousRelease();
                }
            }
        }
    }
}
