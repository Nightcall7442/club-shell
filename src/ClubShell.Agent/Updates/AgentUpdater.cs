using System.Security.Cryptography;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Security;
using ClubShell.Core.Updates;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Updates;

/// <summary>Delivers update events to the Shell (<c>update.available</c>, <c>update.progress</c>, <c>update.ready</c>); implemented by the IPC server.</summary>
public interface IUpdateEventSink
{
    /// <summary>Publishes <c>update.available</c>.</summary>
    ValueTask PublishAsync(UpdateAvailable available, CancellationToken cancellationToken);

    /// <summary>Publishes <c>update.progress</c> (≤ 2/s per download).</summary>
    ValueTask PublishAsync(UpdateProgress progress, CancellationToken cancellationToken);

    /// <summary>Publishes <c>update.ready</c>.</summary>
    ValueTask PublishAsync(UpdateReady ready, CancellationToken cancellationToken);
}

/// <summary>
/// Update orchestration for both components (ARCHITECTURE.md §11): runs <see cref="UpdateChecker"/> on its
/// schedule, downloads and verifies applicable packages (<see cref="UpdateDownloader"/>), stages them
/// (<see cref="IUpdateApplier"/>) and notifies the Shell. A staged update is applied when no session is open and
/// <see cref="UpdateChecker.MayApplyNow"/> allows it (auto-install inside the apply window), or for mandatory
/// packages <see cref="MandatoryNoticeDelay"/> after <c>update.ready</c> even during a session. Agent packages are
/// handed to the helper updater (the service restarts); Shell packages go through <see cref="ShellUpdater"/>. At
/// start-up a leftover rollback marker means the previous apply never reached the target version: the version is
/// suppressed for <see cref="UpdateChecker.FailedSuppression"/> and the failure logged.
/// </summary>
public sealed class AgentUpdater : BackgroundService
{
    /// <summary>Delay between <c>update.ready</c> and a forced apply of a mandatory package during a session.</summary>
    public static TimeSpan MandatoryNoticeDelay { get; } = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StalePackageAge = TimeSpan.FromDays(7);

    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader;
    private readonly IUpdateApplier _applier;
    private readonly ShellUpdater _shell;
    private readonly ISessionService _sessions;
    private readonly IUpdateEventSink _events;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<AgentUpdater> _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _work = new(1, 1);
    private RSA? _publicKey;
    private DateTimeOffset? _agentReadyAt;
    private DateTimeOffset? _shellReadyAt;
    private bool _disposed;

    /// <summary>Creates the updater.</summary>
    public AgentUpdater(UpdateChecker checker, UpdateDownloader downloader, IUpdateApplier applier, ShellUpdater shell, ISessionService sessions, IUpdateEventSink events, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<AgentUpdater> logger)
    {
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _checker = checker;
        _downloader = downloader;
        _applier = applier;
        _shell = shell;
        _sessions = sessions;
        _events = events;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary><see langword="true"/> when the package-signing public key was loaded; without it packages are refused.</summary>
    public bool HasPublicKey => _publicKey is not null;

    /// <summary>Latest applicable Agent manifest, when any.</summary>
    public UpdateManifestDocument? LatestAgent => _checker.LatestAgent;

    /// <summary>Latest applicable Shell manifest, when any.</summary>
    public UpdateManifestDocument? LatestShell => _checker.LatestShell;

    /// <summary>IPC <c>update.check</c>: checks both manifests now and wakes the processing loop.</summary>
    public async Task<UpdateCheckResponse> CheckNowAsync(CancellationToken cancellationToken)
    {
        var response = await _checker.CheckOnceAsync(cancellationToken).ConfigureAwait(false);
        Signal();
        return response;
    }

    /// <summary>
    /// IPC <c>update.apply</c>: applies the staged package of <paramref name="component"/> now when no session is
    /// open (or the package is mandatory); otherwise it stays scheduled for the next idle moment
    /// (<c>scheduled = true, at = null</c>). <c>scheduled = false</c> when nothing is staged.
    /// </summary>
    public async Task<UpdateApplyResponse> ApplyAsync(UpdateComponent component, CancellationToken cancellationToken)
    {
        await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = await _applier.GetPendingAsync(component, cancellationToken).ConfigureAwait(false);
            if (pending is null)
            {
                return new UpdateApplyResponse(false);
            }

            if (_sessions.State.IsOpen() && !pending.Mandatory)
            {
                _logger.LogInformation("{Component} {Version} apply deferred until the session ends", component, pending.Version);
                return new UpdateApplyResponse(true);
            }

            return await ApplyPendingAsync(component, pending, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _work.Release();
        }
    }

    /// <summary>
    /// Server <c>update</c> command: stages <see cref="UpdateCommand.Manifest"/> when given (bypassing suppression),
    /// otherwise the latest applicable manifest of the component; with <see cref="UpdateCommand.ApplyNow"/> the
    /// package is applied immediately, regardless of sessions or the apply window.
    /// </summary>
    public async Task<UpdateApplyResponse> HandleCommandAsync(UpdateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var component = command.Component;
            var pending = await _applier.GetPendingAsync(component, cancellationToken).ConfigureAwait(false);
            if (command.Manifest is { } manifest)
            {
                if (manifest.Component != component)
                {
                    throw new ArgumentException($"Manifest component {manifest.Component} does not match command component {component}", nameof(command));
                }

                if (pending is null || !string.Equals(pending.Version, manifest.Version, StringComparison.Ordinal))
                {
                    pending = await StageAsync(manifest, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (pending is null)
            {
                _ = await _checker.CheckOnceAsync(cancellationToken).ConfigureAwait(false);
                var latest = component == UpdateComponent.Agent ? _checker.LatestAgent : _checker.LatestShell;
                if (latest is null)
                {
                    _logger.LogInformation("Update command for {Component}: nothing applicable on channel {Channel}", component, _checker.Channel);
                    return new UpdateApplyResponse(false);
                }

                pending = await StageAsync(latest.Manifest, cancellationToken).ConfigureAwait(false);
            }

            if (pending is null)
            {
                return new UpdateApplyResponse(false);
            }

            if (command.ApplyNow)
            {
                return await ApplyPendingAsync(component, pending, cancellationToken).ConfigureAwait(false);
            }

            Signal();
            return new UpdateApplyResponse(true);
        }
        finally
        {
            _work.Release();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _checker.Available -= OnAvailable;
            _sessions.Changed -= OnSessionChanged;
            _signal.Dispose();
            _work.Dispose();
            _publicKey?.Dispose();
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StartupAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Updater start-up checks failed; continuing");
        }

        _checker.Available += OnAvailable;
        _sessions.Changed += OnSessionChanged;
        var checkerLoop = _checker.RunAsync(stoppingToken);
        try
        {
            try
            {
                _ = await _checker.CheckOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Initial update check failed");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Update processing failed");
                }

                try
                {
                    _ = await _signal.WaitAsync(EvaluateInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            _checker.Available -= OnAvailable;
            _sessions.Changed -= OnSessionChanged;
            try
            {
                await checkerLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Checker loop cancelled with the host.
            }
        }
    }

    private static string ComponentName(UpdateComponent component) => component == UpdateComponent.Agent ? "Agent" : "Shell";

    private async Task StartupAsync(CancellationToken cancellationToken)
    {
        var keyPath = _settings.CurrentValue.Updates.PublicKeyPath;
        if (File.Exists(keyPath))
        {
            try
            {
                _publicKey = await Signing.LoadRsaPublicKeyFileAsync(keyPath, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Update signing key loaded from {Path}", keyPath);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or FormatException)
            {
                _logger.LogError(ex, "Update signing key {Path} could not be loaded; packages will be refused", keyPath);
            }
        }
        else
        {
            _logger.LogWarning("Update signing key {Path} not found; packages will be refused", keyPath);
        }

        await ReconcileRollbackAsync(UpdateComponent.Agent, cancellationToken).ConfigureAwait(false);
        await ReconcileRollbackAsync(UpdateComponent.Shell, cancellationToken).ConfigureAwait(false);

        var keep = new List<string>();
        foreach (var component in new[] { UpdateComponent.Agent, UpdateComponent.Shell })
        {
            if (await _applier.GetPendingAsync(component, cancellationToken).ConfigureAwait(false) is { } pending)
            {
                keep.Add(pending.PackagePath);
                if (component == UpdateComponent.Agent)
                {
                    _agentReadyAt = _clock.UtcNow;
                }
                else
                {
                    _shellReadyAt = _clock.UtcNow;
                }

                await PublishReadyAsync(pending, cancellationToken).ConfigureAwait(false);
            }
        }

        var removed = _downloader.CleanStale(StalePackageAge, keep);
        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} stale update files", removed);
        }
    }

    private async Task ReconcileRollbackAsync(UpdateComponent component, CancellationToken cancellationToken)
    {
        if (component == UpdateComponent.Agent)
        {
            await _applier.ConfirmAppliedAsync(UpdateComponent.Agent, ClubShellVersion.Current, cancellationToken).ConfigureAwait(false);
        }

        var marker = await _applier.GetRollbackMarkerAsync(component, cancellationToken).ConfigureAwait(false);
        if (marker is null)
        {
            return;
        }

        var reason = component == UpdateComponent.Agent
            ? $"Agent still runs {ClubShellVersion.Current} after applying {marker.ToVersion} (attempt {marker.Attempts})"
            : $"Shell apply of {marker.ToVersion} did not complete (attempt {marker.Attempts})";
        _logger.LogError("{Component} update {From} -> {To} failed: {Reason}; suppressing for {Duration}", ComponentName(component), marker.FromVersion, marker.ToVersion, reason, UpdateChecker.FailedSuppression);
        await _applier.MarkFailedAsync(component, marker.ToVersion, reason, cancellationToken).ConfigureAwait(false);
        _checker.Suppress(component, marker.ToVersion, UpdateChecker.FailedSuppression);
        await _applier.ClearPendingAsync(component, cancellationToken).ConfigureAwait(false);
        await _applier.ClearRollbackMarkerAsync(component, cancellationToken).ConfigureAwait(false);
        if (component == UpdateComponent.Shell)
        {
            _ = await _shell.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProcessComponentAsync(UpdateComponent.Agent, _checker.LatestAgent, cancellationToken).ConfigureAwait(false);
            await ProcessComponentAsync(UpdateComponent.Shell, _checker.LatestShell, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task ProcessComponentAsync(UpdateComponent component, UpdateManifestDocument? document, CancellationToken cancellationToken)
    {
        var pending = await _applier.GetPendingAsync(component, cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            var manifest = document.Manifest;
            var blocked = _checker.IsSuppressed(component, manifest.Version)
                || await _applier.IsMarkedFailedAsync(component, manifest.Version, cancellationToken).ConfigureAwait(false);
            if (!blocked && (pending is null || !string.Equals(pending.Version, manifest.Version, StringComparison.Ordinal)))
            {
                pending = await StageAsync(manifest, cancellationToken).ConfigureAwait(false);
            }
        }

        if (pending is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        var readyAt = component == UpdateComponent.Agent ? _agentReadyAt : _shellReadyAt;
        var sessionOpen = _sessions.State.IsOpen();
        bool allowed;
        if (pending.Mandatory)
        {
            allowed = !sessionOpen || (readyAt is { } shown && now - shown >= MandatoryNoticeDelay);
        }
        else
        {
            allowed = !sessionOpen && _checker.AutoInstall && _checker.IsInApplyWindow();
        }

        if (!allowed)
        {
            return;
        }

        _ = await ApplyPendingAsync(component, pending, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PendingUpdate?> StageAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        var component = manifest.Component;
        var current = component == UpdateComponent.Agent ? _checker.Current.Agent : _checker.Current.Shell;
        if (_publicKey is null && !_downloader.AllowUnsignedPackages)
        {
            // Local configuration problem, not a bad package: do not suppress the version.
            _logger.LogError("Cannot stage {Component} {Version}: no update signing key is configured", ComponentName(component), manifest.Version);
            await PublishProgressAsync(new UpdateProgress(component, manifest.Version, UpdatePhase.Failed, 0, 0, manifest.Size, IpcError.Of(ErrorCode.Internal, "No update signing key configured"))).ConfigureAwait(false);
            return null;
        }

        try
        {
            var progress = new Progress<UpdateProgress>(p => _ = PublishProgressAsync(p));
            var packagePath = await _downloader.DownloadAsync(manifest, _publicKey, progress, cancellationToken).ConfigureAwait(false);
            await PublishProgressAsync(new UpdateProgress(component, manifest.Version, UpdatePhase.Staging, 0, manifest.Size, manifest.Size)).ConfigureAwait(false);
            var pending = await _applier.StageAsync(manifest, packagePath, current, cancellationToken).ConfigureAwait(false);
            await PublishProgressAsync(new UpdateProgress(component, manifest.Version, UpdatePhase.Staging, 100, manifest.Size, manifest.Size)).ConfigureAwait(false);
            if (component == UpdateComponent.Agent)
            {
                _agentReadyAt = _clock.UtcNow;
            }
            else
            {
                _shellReadyAt = _clock.UtcNow;
            }

            await PublishReadyAsync(pending, cancellationToken).ConfigureAwait(false);
            return pending;
        }
        catch (UpdateException ex)
        {
            await FailAsync(component, manifest.Version, ex, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<UpdateApplyResponse> ApplyPendingAsync(UpdateComponent component, PendingUpdate pending, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Applying {Component} update {Version}", ComponentName(component), pending.Version);
        try
        {
            if (component == UpdateComponent.Shell)
            {
                return await _shell.ApplyAsync(cancellationToken).ConfigureAwait(false);
            }

            await PublishProgressAsync(new UpdateProgress(component, pending.Version, UpdatePhase.Applying, 0, 0, 0)).ConfigureAwait(false);
            var response = await _applier.ApplyAsync(component, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Agent update {Version} handed to the installer; the service will restart", pending.Version);
            return response;
        }
        catch (UpdateException ex)
        {
            await FailAsync(component, pending.Version, ex, cancellationToken).ConfigureAwait(false);
            return new UpdateApplyResponse(false);
        }
    }

    private async Task FailAsync(UpdateComponent component, string version, UpdateException exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "{Component} update {Version} failed in phase {Phase}", ComponentName(component), version, exception.Phase);
        _checker.Suppress(component, version, UpdateChecker.FailedSuppression);
        try
        {
            await _applier.MarkFailedAsync(component, version, exception.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed-update marker could not be written");
        }

        await PublishProgressAsync(new UpdateProgress(component, version, UpdatePhase.Failed, 0, 0, 0, IpcError.Of(ErrorCode.Internal, exception.Message))).ConfigureAwait(false);
    }

    private async Task PublishReadyAsync(PendingUpdate pending, CancellationToken cancellationToken)
    {
        var applyAt = pending.Mandatory ? _clock.UtcNow + MandatoryNoticeDelay : (DateTimeOffset?)null;
        try
        {
            await _events.PublishAsync(new UpdateReady(pending.Component, pending.Version, true, pending.Mandatory, applyAt), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "update.ready could not be published");
        }
    }

    private async Task PublishProgressAsync(UpdateProgress progress)
    {
        try
        {
            await _events.PublishAsync(progress, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "update.progress could not be published");
        }
    }

    private async Task PublishAvailableAsync(UpdateAvailable available)
    {
        try
        {
            await _events.PublishAsync(available, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "update.available could not be published");
        }
    }

    private void OnAvailable(object? sender, UpdateAvailable e)
    {
        _ = PublishAvailableAsync(e);
        Signal();
    }

    private void OnSessionChanged(object? sender, SessionEvent e)
    {
        if (e.Type == SessionEventType.Ended)
        {
            Signal();
        }
    }

    private void Signal()
    {
        if (_disposed || _signal.CurrentCount > 0)
        {
            return;
        }

        try
        {
            _ = _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently.
        }
    }
}
