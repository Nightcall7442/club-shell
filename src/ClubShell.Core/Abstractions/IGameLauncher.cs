using ClubShell.Contracts.Games;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Core.Abstractions;

/// <summary>Interactive-session context a game is launched into (ARCHITECTURE.md §2: <c>CreateProcessAsUser</c> + job object).</summary>
/// <param name="WtsSessionId">Windows Terminal Services session id of the kiosk user.</param>
/// <param name="UserName">Kiosk user account name (e.g. <c>club</c>).</param>
/// <param name="Env">Extra environment variables merged into the user's environment block (launcher credentials never go here).</param>
/// <param name="GameCreated">
/// Called by a launcher that starts the game's executable itself (no launcher client in between) with the new process's
/// id and handle, before its first instruction runs: the job goes on there, and a handle is kept for the real exit code
/// (SHELL_CHANGES 83). The handle is the launcher's: duplicate it to keep it.
/// </param>
public sealed record LaunchContext(
    int WtsSessionId,
    string UserName,
    IReadOnlyDictionary<string, string> Env,
    Action<int, SafeProcessHandle>? GameCreated = null);

/// <summary>
/// One launcher backend (Steam, Epic, Battle.net, Riot, EA, Ubisoft, plain exe). Implementations live in the Agent
/// and use the Windows layer for process creation; this contract keeps launch orchestration platform-neutral.
/// </summary>
public interface IGameLauncher
{
    /// <summary>Launcher this backend handles.</summary>
    LauncherType Launcher { get; }

    /// <summary><see langword="true"/> when the launcher client is installed (configured <c>exePath</c> exists) and usable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Resolves local install state of <paramref name="game"/> (library scan, manifest check).</summary>
    Task<GameInstallStatus> GetInstallStatusAsync(Game game, CancellationToken cancellationToken);

    /// <summary>
    /// Launches <paramref name="game"/> for <paramref name="request"/> in <paramref name="context"/>, injecting
    /// <paramref name="lease"/> credentials when the game requires a pooled account. Returns a failed
    /// <see cref="LaunchResult"/> instead of throwing for launcher-level errors.
    /// </summary>
    Task<LaunchResult> LaunchAsync(Game game, LaunchRequest request, AccountLease? lease, LaunchContext context, CancellationToken cancellationToken);

    /// <summary>Terminates process <paramref name="pid"/> (and its job tree); graceful close first unless <paramref name="force"/>.</summary>
    Task KillAsync(int pid, bool force, CancellationToken cancellationToken);
}
