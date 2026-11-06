namespace ClubShell.Core.Realtime;

/// <summary>
/// Why the Agent is about to go (SHELL_CHANGES 60): set when it starts a reboot or a shutdown of Windows, and sent as the
/// description of its own WebSocket close, so the server can tell a reboot that never came back — an incident — from a
/// PC switched off on purpose. <see langword="null"/> (a plain service stop, or Windows shut down by someone else) sends
/// <see cref="Default"/>.
/// </summary>
public static class AgentStopReason
{
    /// <summary>Close description when no reason is known.</summary>
    public const string Default = "closing";

    /// <summary>The Agent started a reboot.</summary>
    public const string Reboot = "reboot";

    /// <summary>The Agent started a shutdown.</summary>
    public const string Shutdown = "shutdown";

    private static string? _current;

    /// <summary>The reason set last, or <see langword="null"/>.</summary>
    public static string? Current => Volatile.Read(ref _current);

    /// <summary>Records <paramref name="reason"/> (<see cref="Reboot"/> or <see cref="Shutdown"/>); <see langword="null"/> clears it (an aborted shutdown).</summary>
    public static void Set(string? reason) => Volatile.Write(ref _current, reason);
}
