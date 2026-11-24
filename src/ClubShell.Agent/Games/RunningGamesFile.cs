using System.Text.Json;
using ClubShell.Contracts.Games;

namespace ClubShell.Agent.Games;

/// <summary>What the Agent needs to take a running game back after its own restart (D-99).</summary>
/// <param name="GameId">Catalogue entry.</param>
/// <param name="Title">Title, for the log.</param>
/// <param name="Pid">Process followed (the successor after a launcher's hand-over).</param>
/// <param name="ProcessStartUtc">Start time of that process (UTC): a pid Windows gave to another process is not the game.</param>
/// <param name="StartedAt">Launch time reported to the Shell.</param>
/// <param name="SessionId">Session the game was launched in.</param>
/// <param name="UserId">Player.</param>
/// <param name="LeaseId">Account-pool lease in use, when any (released by id at the game's exit).</param>
/// <param name="AntiCheat">Anti-cheat of the launch check.</param>
/// <param name="LaunchDurationMs">Launch latency (echoed in the exit report).</param>
public sealed record RunningGameEntry(
    Guid GameId,
    string Title,
    int Pid,
    DateTime? ProcessStartUtc,
    DateTimeOffset StartedAt,
    Guid SessionId,
    Guid UserId,
    Guid? LeaseId,
    AntiCheatKind AntiCheat,
    int LaunchDurationMs);

/// <summary>What becomes of a game the previous Agent was following (<see cref="RunningGamesFile.Decide"/>).</summary>
public enum ReadoptAction
{
    /// <summary>Gone while the Agent was down (or the pid is another process now): nothing to follow.</summary>
    Gone,

    /// <summary>Still running in the open session it was launched in: followed again.</summary>
    Adopt,

    /// <summary>Still running, but its session is over: closed like at a session end.</summary>
    Close,
}

/// <summary>
/// <c>cache\running-games.json</c>: the games the tracker follows, rewritten on every change (D-99). A game's job object
/// no longer kills it when the Agent exits, so after an Agent restart (an update, a crash) the new Agent reads this file
/// and takes the games back: time-up, "close game" and the session's end reach them again.
/// </summary>
public sealed class RunningGamesFile
{
    /// <summary>File name in the cache directory.</summary>
    public const string FileName = "running-games.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Func<string> _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    /// <summary>Creates the file accessor; <paramref name="path"/> is read on every access (the cache directory may move).</summary>
    public RunningGamesFile(Func<string> path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(logger);
        _path = path;
        _logger = logger;
    }

    /// <summary>What to do with <paramref name="entry"/>: its process must still be the same one, and its session the open one.</summary>
    /// <param name="entry">Persisted game.</param>
    /// <param name="processStartUtc">Start time of the process now holding the pid, <see langword="null"/> when there is none.</param>
    /// <param name="openSessionId">The session open now, if any.</param>
    public static ReadoptAction Decide(RunningGameEntry entry, DateTime? processStartUtc, Guid? openSessionId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (processStartUtc is not { } started || (entry.ProcessStartUtc is { } expected && Math.Abs((started - expected).TotalSeconds) > 1))
        {
            return ReadoptAction.Gone;
        }

        return openSessionId == entry.SessionId ? ReadoptAction.Adopt : ReadoptAction.Close;
    }

    /// <summary>The persisted games; empty when there is no file or it cannot be read.</summary>
    public IReadOnlyList<RunningGameEntry> Load()
    {
        lock (_gate)
        {
            string path = _path();
            try
            {
                if (!File.Exists(path))
                {
                    return [];
                }

                return JsonSerializer.Deserialize<List<RunningGameEntry>>(File.ReadAllText(path), Json) ?? [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Running games file {Path} unreadable; no game is taken back", path);
                return [];
            }
        }
    }

    /// <summary>Replaces the file with <paramref name="entries"/> (deleted when empty). Never throws.</summary>
    public void Save(IReadOnlyCollection<RunningGameEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            string path = _path();
            try
            {
                if (entries.Count == 0)
                {
                    File.Delete(path);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Running games file {Path} not written", path);
            }
        }
    }
}
