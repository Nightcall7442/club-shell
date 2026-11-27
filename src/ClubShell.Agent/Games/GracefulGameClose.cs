using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Windows.Processes;

namespace ClubShell.Agent.Games;

/// <summary>
/// Asks a game to close the way the player would (its windows get <c>WM_CLOSE</c>), from the player's own session: the
/// Agent runs in session 0, where the game's windows cannot be seen or posted to.
/// </summary>
public interface IGameCloseRequester
{
    /// <summary>
    /// Asks the process tree of <paramref name="pid"/> in WTS session <paramref name="sessionId"/> to close. Returns whether
    /// the request went out; never throws for a process that is gone.
    /// </summary>
    Task<bool> RequestCloseAsync(int pid, int sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// <c>taskkill /PID n /T</c> without <c>/F</c>, started as the kiosk user in the player's session: it posts <c>WM_CLOSE</c>
/// to the top-level windows of the tree, so a game saves and quits as it would from its own menu. Ships with every Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TaskkillCloseRequester : IGameCloseRequester
{
    private static readonly TimeSpan HelperWait = TimeSpan.FromSeconds(5);
    private readonly ProcessLauncher _launcher;
    private readonly ILogger<TaskkillCloseRequester> _logger;

    /// <summary>Creates the requester.</summary>
    public TaskkillCloseRequester(ILogger<TaskkillCloseRequester> logger, ILogger<ProcessLauncher>? launcherLogger = null)
    {
        _logger = logger;
        _launcher = new ProcessLauncher(launcherLogger);
    }

    /// <inheritdoc />
    public async Task<bool> RequestCloseAsync(int pid, int sessionId, CancellationToken cancellationToken)
    {
        var spec = new ProcessStartSpec
        {
            Exe = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
            Args = FormattableString.Invariant($"/PID {pid} /T"),
            SessionId = (uint)sessionId,
            Hidden = true,
        };
        try
        {
            using LaunchedProcess helper = await _launcher.LaunchAsync(spec, cancellationToken).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(HelperWait);
            try
            {
                int code = await helper.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                _logger.LogInformation("Asked pid {Pid} to close (taskkill exit code {Code})", pid, code);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("taskkill for pid {Pid} still running after {Wait}", pid, HelperWait);
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Cannot ask pid {Pid} to close in session {SessionId}; it is closed by force", pid, sessionId);
            return false;
        }
    }
}

/// <summary>
/// Close first, kill after the grace (D-99): every game of the list is asked to close at once, then given
/// <c>games.killGraceSec</c> together to exit on its own; the caller kills whatever is still there.
/// </summary>
public static class GracefulGameClose
{
    /// <summary>
    /// Asks each of <paramref name="pids"/> to close (<paramref name="requestClose"/>) and waits up to
    /// <paramref name="grace"/> for them (<paramref name="waitForExit"/>). Returns the pids that exited on their own.
    /// A zero grace asks nothing.
    /// </summary>
    public static async Task<IReadOnlyList<int>> CloseAsync(
        IReadOnlyList<int> pids,
        TimeSpan grace,
        Func<int, CancellationToken, Task<bool>> requestClose,
        Func<int, CancellationToken, Task> waitForExit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pids);
        ArgumentNullException.ThrowIfNull(requestClose);
        ArgumentNullException.ThrowIfNull(waitForExit);
        if (pids.Count == 0 || grace <= TimeSpan.Zero)
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(grace);
        var exited = new List<int>();
        var gate = new object();
        await Task.WhenAll(pids.Select(async pid =>
        {
            try
            {
                if (!await requestClose(pid, timeout.Token).ConfigureAwait(false))
                {
                    return;
                }

                await waitForExit(pid, timeout.Token).ConfigureAwait(false);
                lock (gate)
                {
                    exited.Add(pid);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Still running after the grace: killed by the caller.
            }
        })).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return exited;
    }
}
