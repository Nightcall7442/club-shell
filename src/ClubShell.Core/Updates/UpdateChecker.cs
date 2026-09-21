using System.Collections.Concurrent;
using System.Security.Cryptography;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Core.Updates;

/// <summary>
/// Periodically asks the server for newer Agent/Shell packages (ARCHITECTURE.md §11): channel from the policy
/// (fallback <c>updates.channel</c>), jittered <c>updates.checkIntervalSec</c>, downgrade protection, per-version
/// suppression after a failed apply (6 h), and <see cref="Available"/> for applicable manifests. Apply-window and
/// auto-install decisions are exposed for the Agent's updater.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>Suppression applied by the Agent after a failed apply (ARCHITECTURE.md §7).</summary>
    public static TimeSpan FailedSuppression { get; } = TimeSpan.FromHours(6);

    private const double JitterFraction = 0.2;

    private readonly IServerClient _server;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<UpdateChecker> _logger;
    private readonly ConcurrentDictionary<(UpdateComponent Component, string Version), DateTimeOffset> _suppressed = new();

    /// <summary>Creates the checker.</summary>
    public UpdateChecker(IServerClient server, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<UpdateChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _server = server;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised once per applicable manifest found by a check (payload of the <c>update.available</c> IPC event).</summary>
    public event EventHandler<UpdateAvailable>? Available;

    /// <summary>Installed versions; the Agent sets <c>Shell</c> after <c>auth.hello</c>.</summary>
    public ComponentVersions Current { get; set; } = new(ClubShellVersion.Current, "0.0.0");

    /// <summary>Applied policy; its <c>updates</c> section overrides <c>agent.json</c>.</summary>
    public Policy? Policy { get; set; }

    /// <summary>Effective channel.</summary>
    public UpdateChannel Channel => Policy?.Updates.Channel ?? _settings.CurrentValue.Updates.Channel;

    /// <summary>Effective auto-install flag.</summary>
    public bool AutoInstall => Policy?.Updates.AutoInstall ?? _settings.CurrentValue.Updates.AutoInstall;

    /// <summary>Effective apply window (<see langword="null"/> = any time when idle).</summary>
    public TimeWindow? ApplyWindow => _settings.CurrentValue.Updates.ApplyWindow?.ToTimeWindow();

    /// <summary>Latest applicable Agent manifest seen, when any.</summary>
    public UpdateManifestDocument? LatestAgent { get; private set; }

    /// <summary>Latest applicable Shell manifest seen, when any.</summary>
    public UpdateManifestDocument? LatestShell { get; private set; }

    /// <summary>Time of the last completed check.</summary>
    public DateTimeOffset? LastCheckAt { get; private set; }

    /// <summary>Adds ±<c>fraction</c> uniform jitter to <paramref name="interval"/>.</summary>
    public static TimeSpan Jitter(TimeSpan interval, double fraction = JitterFraction)
    {
        if (interval <= TimeSpan.Zero || fraction <= 0)
        {
            return interval;
        }

        var spread = (int)Math.Min(int.MaxValue / 2, interval.TotalMilliseconds * fraction);
        if (spread == 0)
        {
            return interval;
        }

        var offset = RandomNumberGenerator.GetInt32(-spread, spread + 1);
        return interval + TimeSpan.FromMilliseconds(offset);
    }

    /// <summary><see langword="true"/> when non-mandatory updates may be applied now (local time inside the window).</summary>
    public bool IsInApplyWindow() =>
        UpdateManifestExtensions.IsInApplyWindow(ApplyWindow, TimeOnly.FromDateTime(_clock.LocalNow.DateTime));

    /// <summary><see langword="true"/> when <paramref name="manifest"/> may be applied now: mandatory packages always, others when <see cref="AutoInstall"/> and inside the window.</summary>
    public bool MayApplyNow(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Mandatory || (AutoInstall && IsInApplyWindow());
    }

    /// <summary>Ignores <paramref name="version"/> of <paramref name="component"/> for <paramref name="duration"/> (after a failed apply).</summary>
    public void Suppress(UpdateComponent component, string version, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        _suppressed[(component, version)] = _clock.UtcNow + duration;
        _logger.LogWarning("Suppressing {Component} {Version} for {Duration}", component, version, duration);
    }

    /// <summary><see langword="true"/> while <paramref name="version"/> of <paramref name="component"/> is suppressed.</summary>
    public bool IsSuppressed(UpdateComponent component, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (!_suppressed.TryGetValue((component, version), out var until))
        {
            return false;
        }

        if (until > _clock.UtcNow)
        {
            return true;
        }

        _ = _suppressed.TryRemove((component, version), out _);
        return false;
    }

    /// <summary>
    /// Fetches the manifests of both components, raises <see cref="Available"/> for applicable ones and returns the
    /// <c>update.check</c> response. Server failures (offline) are logged and yield <see langword="null"/> manifests.
    /// </summary>
    public async Task<UpdateCheckResponse> CheckOnceAsync(CancellationToken cancellationToken)
    {
        var current = Current;
        var agent = await CheckComponentAsync(UpdateComponent.Agent, current.Agent, current.Agent, cancellationToken).ConfigureAwait(false);
        var shell = await CheckComponentAsync(UpdateComponent.Shell, current.Shell, current.Agent, cancellationToken).ConfigureAwait(false);
        LastCheckAt = _clock.UtcNow;
        LatestAgent = agent;
        LatestShell = shell;
        return new UpdateCheckResponse(current, agent?.Manifest, shell?.Manifest);
    }

    /// <summary>Runs <see cref="CheckOnceAsync"/> every jittered <c>updates.checkIntervalSec</c> until cancelled (the first check happens after one interval; call <see cref="CheckOnceAsync"/> at start-up explicitly).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(60, _settings.CurrentValue.Updates.CheckIntervalSec));
            await _clock.Delay(Jitter(interval), cancellationToken).ConfigureAwait(false);
            try
            {
                await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Update check failed");
            }
        }
    }

    private async Task<UpdateManifestDocument?> CheckComponentAsync(UpdateComponent component, string currentVersion, string agentVersion, CancellationToken cancellationToken)
    {
        UpdateManifest? manifest;
        try
        {
            manifest = await _server.GetUpdateManifestAsync(Channel, component, currentVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex)
        {
            _logger.LogWarning("Update manifest for {Component} unavailable: {Code} {Message}", component, ex.Code, ex.Message);
            return null;
        }

        if (manifest is null)
        {
            _logger.LogDebug("{Component} {Version} is up to date on channel {Channel}", component, currentVersion, Channel);
            return null;
        }

        var document = new UpdateManifestDocument(manifest, _clock.UtcNow, currentVersion, agentVersion);
        if (!document.IsApplicable)
        {
            _logger.LogInformation("Ignoring {Component} manifest {Version} (current {Current}, minAgent {MinAgent})", component, manifest.Version, currentVersion, manifest.MinAgentVersion ?? "-");
            return null;
        }

        if (IsSuppressed(component, manifest.Version))
        {
            _logger.LogInformation("{Component} {Version} is suppressed after a failed apply", component, manifest.Version);
            return null;
        }

        _logger.LogInformation("{Component} update available: {Version} (current {Current}, mandatory {Mandatory})", component, manifest.Version, currentVersion, manifest.Mandatory);
        try
        {
            Available?.Invoke(this, new UpdateAvailable(manifest, currentVersion));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update listener threw");
        }

        return document;
    }
}
