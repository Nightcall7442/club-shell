namespace ClubShell.Agent.Session;

/// <summary>
/// Open once the Agent's start has restored the session and its signed-in player (<see cref="SignedInUserStore.Restore"/>),
/// or given up on it (D-100). The Shell's pipe is served before that — the restore can wait up to 30 s for an unreachable
/// server — and an <c>auth.status</c> answered meanwhile said «nobody signed in» to a Shell that reconnected after an Agent
/// restart, which then dropped its player to the lock screen with their session still running. <c>auth.status</c> waits
/// here, bounded; whatever is restored later is pushed (<c>session.updated</c>) and the Shell reads <c>auth.status</c> again.
/// </summary>
public sealed class SignInRestoreGate
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary><see langword="true"/> once the restore is done (or skipped).</summary>
    public bool IsDone => _done.Task.IsCompleted;

    /// <summary>Opens the gate; later calls do nothing.</summary>
    public void Complete() => _done.TrySetResult();

    /// <summary>Waits for the restore at most <paramref name="timeout"/>; <see langword="false"/> when it is still running.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _done.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
