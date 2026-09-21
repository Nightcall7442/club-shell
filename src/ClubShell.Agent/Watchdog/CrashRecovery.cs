using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Agent.Server;
using ClubShell.Contracts.Commands;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Watchdog;

/// <summary>What the watchdog should do after a Shell exit.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RecoveryActionKind>))]
public enum RecoveryActionKind
{
    /// <summary>Relaunch immediately.</summary>
    RestartImmediately,

    /// <summary>Relaunch after <see cref="RecoveryAction.Delay"/>.</summary>
    RestartAfter,

    /// <summary>Crash loop detected: drop the fallback shell and stop relaunching until the session recovers.</summary>
    SafeMode,
}

/// <summary>A recovery decision.</summary>
/// <param name="Kind">Action to take.</param>
/// <param name="Delay">Delay before relaunch (only for <see cref="RecoveryActionKind.RestartAfter"/>).</param>
public sealed record RecoveryAction(RecoveryActionKind Kind, TimeSpan Delay)
{
    /// <summary>Relaunch immediately.</summary>
    public static RecoveryAction RestartImmediately { get; } = new(RecoveryActionKind.RestartImmediately, TimeSpan.Zero);

    /// <summary>Enter safe mode (fallback shell).</summary>
    public static RecoveryAction SafeMode { get; } = new(RecoveryActionKind.SafeMode, TimeSpan.Zero);

    /// <summary>Relaunch after <paramref name="delay"/>.</summary>
    public static RecoveryAction RestartAfter(TimeSpan delay) => new(RecoveryActionKind.RestartAfter, delay);
}

/// <summary>Diagnostics collected about a single Shell crash.</summary>
/// <param name="ExitCode">Process exit code, or <see langword="null"/> when it was killed for being unresponsive.</param>
/// <param name="Uptime">How long the Shell ran before exiting.</param>
/// <param name="At">When the exit was observed (UTC).</param>
/// <param name="Graceful">Whether the exit was an intentional stop rather than a crash.</param>
/// <param name="LogTail">Last lines of the Shell log.</param>
/// <param name="DumpPath">WER dump file when one was produced.</param>
public sealed record CrashReport(
    int? ExitCode,
    TimeSpan Uptime,
    DateTimeOffset At,
    bool Graceful,
    IReadOnlyList<string> LogTail,
    string? DumpPath);

/// <summary>
/// Crash-loop detection and recovery for the kiosk Shell. Counts crashes in a sliding one-minute window against
/// <c>shell.maxRestartsPerMinute</c>; once the budget is exceeded it returns <see cref="RecoveryAction.SafeMode"/> so the
/// watchdog drops an <c>explorer.exe</c> fallback and raises <c>shellCrashLoop</c> telemetry. Otherwise it asks for an
/// immediate relaunch, or a delayed one after a very short run. Every exit is reported to the server via
/// <see cref="TelemetryBus"/> (<c>shellCrash</c>) with the exit code, uptime, log tail and WER dump path, and appended to
/// <c>ProgramData\ClubShell\crash-history.json</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CrashRecovery
{
    /// <summary>File the crash history is persisted to (relative to the ProgramData root).</summary>
    public const string HistoryFileName = "crash-history.json";

    private const int MaxHistoryEntries = 50;
    private const int LogTailLines = 50;
    private static readonly TimeSpan ShortRunThreshold = TimeSpan.FromSeconds(10);

    private readonly TelemetryBus _telemetry;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<CrashRecovery> _logger;
    private readonly object _sync = new();
    private readonly Queue<DateTimeOffset> _crashWindow = new();

    /// <summary>Creates the recovery helper.</summary>
    public CrashRecovery(
        TelemetryBus telemetry,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<CrashRecovery> logger)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _telemetry = telemetry;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Number of crashes counted in the current one-minute window.</summary>
    public int RecentCrashCount
    {
        get
        {
            lock (_sync)
            {
                Trim(_clock.UtcNow);
                return _crashWindow.Count;
            }
        }
    }

    /// <summary>Clears the crash window (call when a fresh session starts, so a new user is not penalised).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _crashWindow.Clear();
        }
    }

    /// <summary>
    /// Records a crash and decides how to recover. A graceful exit (intentional stop) is not counted and yields
    /// <see cref="RecoveryAction.RestartImmediately"/>.
    /// </summary>
    public RecoveryAction Decide(int? exitCode, TimeSpan uptime, bool graceful)
    {
        if (graceful)
        {
            return RecoveryAction.RestartImmediately;
        }

        var now = _clock.UtcNow;
        int count;
        lock (_sync)
        {
            _crashWindow.Enqueue(now);
            Trim(now);
            count = _crashWindow.Count;
        }

        var budget = Math.Max(1, _settings.CurrentValue.Shell.MaxRestartsPerMinute);
        if (count >= budget)
        {
            _logger.LogError("Shell crash loop: {Count} crashes in the last minute (budget {Budget}); entering safe mode", count, budget);
            return RecoveryAction.SafeMode;
        }

        if (uptime < ShortRunThreshold)
        {
            var delay = TimeSpan.FromMilliseconds(Math.Max(0, _settings.CurrentValue.Shell.RestartDelayMs));
            _logger.LogWarning("Shell exited after {Uptime}; restarting after {Delay}", uptime, delay);
            return RecoveryAction.RestartAfter(delay);
        }

        _logger.LogInformation("Shell exited (code {ExitCode}) after {Uptime}; restarting", exitCode, uptime);
        return RecoveryAction.RestartImmediately;
    }

    /// <summary>Collects diagnostics for a Shell exit (log tail, WER dump).</summary>
    public CrashReport BuildReport(int? exitCode, TimeSpan uptime, bool graceful, DateTimeOffset startedAt)
    {
        var logsDir = _settings.CurrentValue.LogsDir;
        return new CrashReport(exitCode, uptime, _clock.UtcNow, graceful, ReadLogTail(logsDir), FindDump(logsDir, startedAt));
    }

    /// <summary>Reports the crash to the server (telemetry) and appends it to the on-disk history.</summary>
    public async Task ReportAsync(CrashReport report, bool safeMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var payload = new
        {
            exitCode = report.ExitCode,
            uptimeSec = (int)report.Uptime.TotalSeconds,
            graceful = report.Graceful,
            dumpPath = report.DumpPath,
            logTail = report.LogTail,
            recentCrashes = RecentCrashCount,
        };

        _ = _telemetry.Publish(TelemetryEventKinds.ShellCrash, payload);
        if (safeMode)
        {
            _ = _telemetry.Publish(TelemetryEventKinds.ShellCrashLoop, payload);
        }

        await AppendHistoryAsync(report, safeMode, cancellationToken).ConfigureAwait(false);
    }

    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromMinutes(1);
        while (_crashWindow.TryPeek(out var oldest) && oldest < cutoff)
        {
            _crashWindow.Dequeue();
        }
    }

    private string[] ReadLogTail(string logsDir)
    {
        try
        {
            if (!Directory.Exists(logsDir))
            {
                return Array.Empty<string>();
            }

            var newest = new DirectoryInfo(logsDir)
                .EnumerateFiles("shell-*.json")
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                return Array.Empty<string>();
            }

            return ReadLastLines(newest.FullName, LogTailLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Reading the Shell log tail from {Dir} failed", logsDir);
            return Array.Empty<string>();
        }
    }

    private static string[] ReadLastLines(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var buffer = new LinkedList<string>();
        while (reader.ReadLine() is { } line)
        {
            buffer.AddLast(line);
            if (buffer.Count > count)
            {
                buffer.RemoveFirst();
            }
        }

        return buffer.ToArray();
    }

    private string? FindDump(string logsDir, DateTimeOffset startedAt)
    {
        foreach (var directory in DumpDirectories(logsDir))
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var dump = new DirectoryInfo(directory)
                    .EnumerateFiles("*.dmp")
                    .Where(f => f.LastWriteTimeUtc >= startedAt.UtcDateTime.AddMinutes(-1))
                    .OrderByDescending(static f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (dump is not null)
                {
                    return dump.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Scanning {Dir} for crash dumps failed", directory);
            }
        }

        return null;
    }

    private static IEnumerable<string> DumpDirectories(string logsDir)
    {
        yield return logsDir;
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "CrashDumps");
        }
    }

    private async Task AppendHistoryAsync(CrashReport report, bool safeMode, CancellationToken cancellationToken)
    {
        var path = _settings.CurrentValue.ResolvePath(HistoryFileName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var history = await LoadHistoryAsync(path, cancellationToken).ConfigureAwait(false);
            history.Add(new HistoryEntry(report.At, report.ExitCode, (int)report.Uptime.TotalSeconds, report.Graceful, safeMode, report.DumpPath));
            if (history.Count > MaxHistoryEntries)
            {
                history.RemoveRange(0, history.Count - MaxHistoryEntries);
            }

            var json = JsonSerializer.Serialize(history, HistoryJson);
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Persisting crash history to {Path} failed", path);
        }
    }

    private static async Task<List<HistoryEntry>> LoadHistoryAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new List<HistoryEntry>();
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<HistoryEntry>>(text, HistoryJson) ?? new List<HistoryEntry>();
        }
        catch (JsonException)
        {
            return new List<HistoryEntry>();
        }
    }

    private static readonly JsonSerializerOptions HistoryJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record HistoryEntry(DateTimeOffset At, int? ExitCode, int UptimeSec, bool Graceful, bool SafeMode, string? DumpPath);
}
