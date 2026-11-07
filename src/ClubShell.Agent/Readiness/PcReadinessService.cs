using ClubShell.Agent.Ipc;
using ClubShell.Agent.Saves;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Readiness;

/// <summary>What <see cref="PcReadinessService"/> needs of the game library.</summary>
public interface IReadinessLibrary
{
    /// <summary>The startup load of the catalogue finished.</summary>
    bool Loaded { get; }

    /// <summary>Catalogue version.</summary>
    string CatalogVersion { get; }

    /// <summary>Install detection again, now (no waiting for the next tick).</summary>
    Task RescanAsync(CancellationToken cancellationToken);

    /// <summary>The game list changed; argument is the new catalogue version.</summary>
    event EventHandler<string>? Changed;
}

/// <summary>
/// Whether the PC may let a player in (SHELL_CHANGES 86). The diskless helper attaches the games disks (G:, H:) only
/// after Windows has started, the kiosk is logged on automatically before that, and a player signing in meanwhile saw an
/// empty library. So the PC is <see cref="PcReadinessState.Preparing"/> until the disks are there (or the wait ends),
/// then <see cref="PcReadinessState.Ready"/> once per Agent run — between players on a hybrid PC the disks stay — or
/// <see cref="PcReadinessState.NotReady"/> when no disk came, until one does.
/// <list type="bullet">
/// <item>No <c>ClubDisklessHelper</c> service, or <c>shell.readiness.enabled</c> off: ready at once.</item>
/// <item>A helper whose <c>ping</c> reports <c>ready</c> and <c>disks</c>: its word, polled every
/// <see cref="PollInterval"/>, waited for up to <c>shell.readiness.timeoutSec</c> from the start of the wait.</item>
/// <item>An older helper (no <c>ready</c>, or no channel for <see cref="NoChannelAfter"/>): ready when every library root
/// off the system drive exists, waited for up to the timeout counted from Windows' start.</item>
/// </list>
/// Before turning ready the games are re-scanned, so the player signs in to a list where the disks' games are
/// installed. Sign-in, QR and session start are refused until then (<c>pcNotReady</c>); the Shell shows the progress
/// (<c>sys.readinessChanged</c>), the cash desk sees it in the heartbeat.
/// </summary>
public sealed class PcReadinessService : BackgroundService
{
    /// <summary>How often the helper and the roots are looked at while preparing.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>A helper channel missing this long is an older helper (or none running): the roots decide.</summary>
    public TimeSpan NoChannelAfter { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan LibraryLoadWait = TimeSpan.FromSeconds(90);

    private readonly IDisklessHelperChannel _channel;
    private readonly IReadinessLibrary _library;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<PcReadinessService> _logger;
    private readonly IIpcEventPublisher? _events;
    private readonly Func<bool> _helperInstalled;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<TimeSpan> _uptime;
    private readonly object _gate = new();
    private PcReadiness _current = new(PcReadinessState.Preparing, []);
    private Task _publishing = Task.CompletedTask;

    /// <summary>Creates the service.</summary>
    public PcReadinessService(
        IDisklessHelperChannel channel,
        IReadinessLibrary library,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<PcReadinessService> logger,
        IIpcEventPublisher? events = null,
        Func<bool>? helperInstalled = null,
        Func<string, bool>? directoryExists = null,
        Func<TimeSpan>? uptime = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _channel = channel;
        _library = library;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _events = events;
        _helperInstalled = helperInstalled ?? HelperServiceInstalled;
        _directoryExists = directoryExists ?? Directory.Exists;
        _uptime = uptime ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        _library.Changed += OnLibraryChanged;
    }

    /// <summary>Where the PC is now.</summary>
    public PcReadiness Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>A player may sign in and a session may start.</summary>
    public bool IsReady => Current.State == PcReadinessState.Ready;

    /// <summary>Raised on every change of <see cref="Current"/>.</summary>
    public event EventHandler<PcReadiness>? Changed;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReadinessSettings config = _settings.CurrentValue.Shell.Readiness;
        long started = _clock.GetTimestamp();
        if (!config.Enabled || !SafeHelperInstalled())
        {
            await TurnReadyAsync(started, Array.Empty<PcReadinessDisk>(), config.Enabled ? "no diskless helper on this PC" : "readiness wait off", stoppingToken).ConfigureAwait(false);
            return;
        }

        TimeSpan timeout = TimeSpan.FromSeconds(config.TimeoutSec);
        DateTimeOffset helperDeadline = _clock.UtcNow + timeout;
        DateTimeOffset bootDeadline = _clock.UtcNow - _uptime() + timeout;
        long? channelMissingSince = null;
        bool rootsMode = false;
        bool notReadyLogged = false;
        Set(new PcReadiness(PcReadinessState.Preparing, [], helperDeadline));
        _logger.LogInformation("Readiness: preparing; waiting for the games disks (up to {Timeout})", timeout);

        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<PcReadinessDisk> disks;
            bool done;
            bool anyDisk;
            DateTimeOffset deadline;
            HelperResult? ping = rootsMode ? null : await _channel.SendAsync(HelperProtocol.Ping(), TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            if (ping is { IsOk: true, Ready: { } helperReady })
            {
                channelMissingSince = null;
                disks = (ping.Disks ?? []).Select(d => new PcReadinessDisk(d.Library, d.Letter, d.State)).ToList();
                done = helperReady;
                anyDisk = disks.Count == 0 || ping.Disks!.Any(d => d.Mounted);
                deadline = helperDeadline;
            }
            else
            {
                if (ping is { IsOk: true })
                {
                    rootsMode = true; // 1.7.0–1.8.1: no ready/disks; the roots tell
                }
                else if (ping is not null)
                {
                    channelMissingSince ??= _clock.GetTimestamp();
                    if (_clock.GetElapsedTime(channelMissingSince.Value) >= NoChannelAfter)
                    {
                        rootsMode = true; // 1.6.0 or the service not running
                    }
                }

                (disks, done, anyDisk) = Roots();
                deadline = bootDeadline;
            }

            bool timedOut = _clock.UtcNow >= deadline;
            if ((done || timedOut) && anyDisk && (rootsMode || ping is { IsOk: true }))
            {
                await TurnReadyAsync(started, disks, done ? "disks attached" : "wait ended with a disk attached", stoppingToken).ConfigureAwait(false);
                return;
            }

            if ((done || timedOut) && !anyDisk && (rootsMode || ping is { IsOk: true }))
            {
                Set(new PcReadiness(PcReadinessState.NotReady, disks));
                if (!notReadyLogged)
                {
                    notReadyLogged = true;
                    _logger.LogWarning("Readiness: not ready after {Elapsed}: no games disk attached ({Disks}); still watching", _clock.GetElapsedTime(started), Describe(disks));
                }
            }
            else if (!notReadyLogged)
            {
                Set(new PcReadiness(PcReadinessState.Preparing, disks, deadline));
            }

            await _clock.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Library roots off the system drive: each one a "disk", attached when it exists.</summary>
    private (IReadOnlyList<PcReadinessDisk> Disks, bool All, bool Any) Roots()
    {
        string? system = DriveLetter(Environment.SystemDirectory);
        var disks = new List<PcReadinessDisk>();
        foreach (string root in _settings.CurrentValue.Games.LibraryRoots ?? [])
        {
            string? letter = DriveLetter(root);
            if (string.IsNullOrWhiteSpace(root) || (letter is not null && string.Equals(letter, system, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            bool exists = SafeExists(root);
            disks.Add(new PcReadinessDisk(root, letter, exists ? "mounted" : "attaching"));
        }

        return (disks, disks.All(d => d.State == "mounted"), disks.Count == 0 || disks.Any(d => d.State == "mounted"));
    }

    private async Task TurnReadyAsync(long started, IReadOnlyList<PcReadinessDisk> disks, string why, CancellationToken cancellationToken)
    {
        // The startup sequence loads the catalogue first; then the disks' games are detected before anyone signs in.
        long waitStarted = _clock.GetTimestamp();
        while (!_library.Loaded && _clock.GetElapsedTime(waitStarted) < LibraryLoadWait && !cancellationToken.IsCancellationRequested)
        {
            await _clock.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _library.RescanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Readiness: rescan before ready failed");
        }

        Set(new PcReadiness(PcReadinessState.Ready, disks));
        _logger.LogInformation("Readiness: ready after {Elapsed} ({Why}); disks: {Disks}", _clock.GetElapsedTime(started), why, Describe(disks));
    }

    private void Set(PcReadiness next)
    {
        lock (_gate)
        {
            if (_current.State == next.State && _current.Deadline == next.Deadline && _current.Disks.SequenceEqual(next.Disks))
            {
                return;
            }

            _current = next;
            if (_events is not null)
            {
                _publishing = _publishing.ContinueWith(_ => PublishAsync(IpcMessages.Events.SysReadinessChanged, next), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }

        try
        {
            Changed?.Invoke(this, next);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Readiness.Changed handler threw");
        }
    }

    private void OnLibraryChanged(object? sender, string version)
    {
        // SHELL_CHANGES 86: a disk that came (or went) later, a game the owner added — the Shell re-reads the list.
        if (_events is not null)
        {
            lock (_gate)
            {
                _publishing = _publishing.ContinueWith(_ => PublishAsync(IpcMessages.Events.GamesChanged, new GamesChanged(version)), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }
    }

    private async Task PublishAsync<T>(string name, T payload)
    {
        try
        {
            await _events!.PublishAsync(name, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "{Event} not delivered to the Shell", name);
        }
    }

    private bool SafeExists(string path)
    {
        try
        {
            return _directoryExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool SafeHelperInstalled()
    {
        try
        {
            return _helperInstalled();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Readiness: helper service probe failed");
            return false;
        }
    }

    /// <summary><c>G</c> for <c>G:\Steam</c>; <see langword="null"/> for a path without a drive letter.</summary>
    private static string? DriveLetter(string? path) =>
        path is { Length: >= 2 } && path[1] == ':' && char.IsAsciiLetter(path[0]) ? char.ToUpperInvariant(path[0]).ToString() : null;

    private static string Describe(IReadOnlyList<PcReadinessDisk> disks) =>
        disks.Count == 0 ? "none expected" : string.Join(", ", disks.Select(d => $"{d.Library}{(d.Letter is null ? string.Empty : " " + d.Letter + ":")} {d.State}"));

    private static bool HelperServiceInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + HelperProtocol.ServiceName);
        return key is not null;
    }
}
