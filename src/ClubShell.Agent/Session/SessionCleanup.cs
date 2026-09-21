using System.Runtime.Versioning;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using ClubShell.Windows.Sessions;
using ClubShell.Windows.Users;
using ClubShell.Windows.Windows;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Session;

/// <summary>Game-side teardown at session end; implemented by the games subsystem.</summary>
public interface IGameSessionCleanup
{
    /// <summary>Terminates every running game of the session (job object) and reports the exits.</summary>
    Task KillAllAsync(SessionEndReason reason, CancellationToken cancellationToken);

    /// <summary>Releases every account-pool lease held by the session (cloud saves uploaded first when enabled).</summary>
    Task ReleaseLeasesAsync(SessionEndReason reason, CancellationToken cancellationToken);
}

/// <summary>One cleanup step outcome.</summary>
/// <param name="Name">Step name (<c>games.kill</c>, <c>windows.close</c>, …).</param>
/// <param name="Ok">Completed without error.</param>
/// <param name="Duration">Wall time of the step.</param>
/// <param name="Error">Failure message, when not <paramref name="Ok"/>.</param>
public sealed record CleanupStepResult(string Name, bool Ok, TimeSpan Duration, string? Error);

/// <summary>Outcome of <see cref="SessionCleanup.RunAsync"/>.</summary>
/// <param name="Steps">Every step, in execution order.</param>
/// <param name="Errors"><c>name: message</c> of every failed step.</param>
/// <param name="Duration">Total wall time.</param>
public sealed record CleanupReport(IReadOnlyList<CleanupStepResult> Steps, IReadOnlyList<string> Errors, TimeSpan Duration)
{
    /// <summary><see langword="true"/> when every step succeeded.</summary>
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Actions run when a session ends (IPC_PROTOCOL.md §9.2): kill games, release leases, close the player's windows, clear
/// the clipboard, empty temp/download/browser caches of the kiosk profile. Each step is isolated (own try/catch and
/// <see cref="StepTimeout"/>); a failing step never stops the next one. The kiosk profile reset is not a step here: the
/// Users subsystem (<c>ProfileResetService</c>) runs it on the <c>ended</c> session event, once the session is closed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionCleanup
{
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<SessionCleanup> _logger;
    private readonly IGameSessionCleanup? _games;
    private readonly ProcessLauncher _launcher = new();
    private readonly ProfileReset _profiles = new();

    /// <summary>Creates the cleanup; <paramref name="games"/> is an optional collaborator.</summary>
    public SessionCleanup(
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<SessionCleanup> logger,
        IGameSessionCleanup? games = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _games = games;
    }

    /// <summary>Per-step timeout.</summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Graceful close wait per window before it is left alone (games are killed by <see cref="IGameSessionCleanup"/> anyway).</summary>
    public TimeSpan WindowCloseTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Absolute directories emptied (not deleted) at session end, in addition to the kiosk profile caches.</summary>
    public ICollection<string> ExtraDirectories { get; } = new List<string>();

    /// <summary>Process ids whose windows are never closed (the Shell, remote-admin helpers).</summary>
    public ISet<int> ProtectedPids { get; } = new HashSet<int>();

    /// <summary>Runs every step and returns the report; never throws except on <paramref name="cancellationToken"/>.</summary>
    public async Task<CleanupReport> RunAsync(SessionEndReason reason, CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        var kiosk = _settings.CurrentValue.Shell.KioskUser;
        var steps = new List<(string Name, Func<CancellationToken, Task> Action)>();
        if (_games is not null)
        {
            var games = _games;
            steps.Add(("games.kill", ct => games.KillAllAsync(reason, ct)));
            steps.Add(("games.releaseLeases", ct => games.ReleaseLeasesAsync(reason, ct)));
        }

        steps.Add(("windows.close", CloseUserWindowsAsync));
        steps.Add(("clipboard.clear", ClearClipboardAsync));
        steps.Add(("profile.cleanCaches", ct => CleanProfileCachesAsync(kiosk.Name, ct)));
        if (ExtraDirectories.Count > 0)
        {
            steps.Add(("directories.clean", CleanExtraDirectoriesAsync));
        }

        var results = new List<CleanupStepResult>(steps.Count);
        foreach (var (name, action) in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunStepAsync(name, action, cancellationToken).ConfigureAwait(false));
        }

        var errors = results.Where(r => !r.Ok).Select(r => $"{r.Name}: {r.Error}").ToArray();
        var report = new CleanupReport(results, errors, _clock.GetElapsedTime(started));
        _logger.LogInformation("Session cleanup ({Reason}) finished in {Duration}: {Ok}/{Total} steps ok", reason, report.Duration, results.Count - errors.Length, results.Count);
        return report;
    }

    private async Task<CleanupStepResult> RunStepAsync(string name, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StepTimeout);
        try
        {
            await action(timeout.Token).ConfigureAwait(false);
            return new CleanupStepResult(name, true, _clock.GetElapsedTime(started), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Cleanup step {Step} timed out after {Timeout}", name, StepTimeout);
            return new CleanupStepResult(name, false, _clock.GetElapsedTime(started), $"timed out after {StepTimeout}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup step {Step} failed", name);
            return new CleanupStepResult(name, false, _clock.GetElapsedTime(started), ex.Message);
        }
    }

    // ponytail: EnumWindows sees the caller's window station, so from session 0 this only reaches windows the Agent itself
    // can see; games are torn down through the job object. Upgrade: ask the Shell to close windows over IPC.
    private Task CloseUserWindowsAsync(CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            var ownPid = (uint)Environment.ProcessId;
            var closed = 0;
            foreach (var window in WindowManager.EnumerateTopLevel())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!window.Visible || window.Pid == ownPid || ProtectedPids.Contains((int)window.Pid))
                {
                    continue;
                }

                if (WindowManager.Close(window.Hwnd, WindowCloseTimeout))
                {
                    closed++;
                }
            }

            _logger.LogDebug("Closed {Closed} user window(s)", closed);
        },
        cancellationToken);

    private async Task ClearClipboardAsync(CancellationToken cancellationToken)
    {
        var sessionId = WtsSessions.GetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue || sessionId == 0)
        {
            _logger.LogDebug("No interactive session; clipboard not cleared");
            return;
        }

        // The clipboard belongs to the interactive window station: run `clip` there with empty input.
        using var process = await _launcher.LaunchAsync(
            new ProcessStartSpec
            {
                Exe = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Args = "/c echo off | clip",
                SessionId = sessionId,
                Hidden = true,
            },
            cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task CleanProfileCachesAsync(string userName, CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            var result = _profiles.CleanCaches(userName);
            _logger.LogInformation("Kiosk profile caches cleaned: {FreedMb} MB freed, {Errors} error(s)", result.FreedBytes / (1024 * 1024), result.Errors.Count);
            foreach (var error in result.Errors)
            {
                _logger.LogDebug("Profile cache cleanup: {Error}", error);
            }
        },
        cancellationToken);

    private Task CleanExtraDirectoriesAsync(CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            var failures = 0;
            foreach (var directory in ExtraDirectories.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                failures += EmptyDirectory(directory);
            }

            if (failures > 0)
            {
                throw new IOException($"{failures} entries could not be deleted");
            }
        },
        cancellationToken);

    private int EmptyDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var failures = 0;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Cannot enumerate {Directory}", path);
            return 1;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                    File.Delete(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                _logger.LogDebug(ex, "Cannot delete {Entry}", entry);
            }
        }

        return failures;
    }
}
