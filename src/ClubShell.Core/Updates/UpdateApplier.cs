using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Core.Updates;

/// <summary>Outcome of a process run by <see cref="IProcessStarter.RunAsync"/>.</summary>
/// <param name="ExitCode">Exit code.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>Starts installer processes; abstracted so the applier is testable and the Windows layer can substitute <c>CreateProcessAsUser</c> variants.</summary>
public interface IProcessStarter
{
    /// <summary>Runs a process to completion (no shell, no window, output captured); kills it after <paramref name="timeout"/>.</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Starts a process without waiting (used for self-update, where the installer stops the calling service). Returns the process id.</summary>
    int StartDetached(string fileName, IReadOnlyList<string> arguments, string? workingDirectory);
}

/// <summary>Default <see cref="IProcessStarter"/> over <see cref="Process"/> (<c>UseShellExecute = false</c>, arguments passed as a list, never a shell).</summary>
public sealed class ProcessStarter : IProcessStarter
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        using var process = new Process();
        process.StartInfo = CreateStartInfo(fileName, arguments, null, redirect: true);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"{fileName} did not exit within {timeout}");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <inheritdoc />
    public int StartDetached(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        using var process = new Process();
        process.StartInfo = CreateStartInfo(fileName, arguments, workingDirectory, redirect: false);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}");
        }

        return process.Id;
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, bool redirect)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}

/// <summary>A verified package staged in <c>pending-update\</c> awaiting apply.</summary>
/// <param name="Component">Component.</param>
/// <param name="Version">Package version.</param>
/// <param name="PackagePath">Staged package path.</param>
/// <param name="Sha256">Package hash (re-checked before apply).</param>
/// <param name="Mandatory">Apply even during a session.</param>
/// <param name="StagedAt">Staging time.</param>
/// <param name="PreviousVersion">Version installed when staged (rollback target).</param>
/// <param name="Attempts">Apply attempts so far.</param>
public sealed record PendingUpdate(
    UpdateComponent Component,
    string Version,
    string PackagePath,
    string Sha256,
    bool Mandatory,
    DateTimeOffset StagedAt,
    string PreviousVersion,
    int Attempts);

/// <summary>Marker written before an apply so the next Agent start can detect a failed upgrade (ARCHITECTURE.md §11).</summary>
/// <param name="Component">Component.</param>
/// <param name="FromVersion">Version before the apply.</param>
/// <param name="ToVersion">Version being applied.</param>
/// <param name="PackagePath">Package applied.</param>
/// <param name="CreatedAt">Marker time.</param>
/// <param name="Attempts">Apply attempts.</param>
public sealed record RollbackMarker(
    UpdateComponent Component,
    string FromVersion,
    string ToVersion,
    string PackagePath,
    DateTimeOffset CreatedAt,
    int Attempts);

/// <summary>Marker suppressing a version after a failed apply.</summary>
/// <param name="Component">Component.</param>
/// <param name="Version">Failed version.</param>
/// <param name="Reason">Failure description.</param>
/// <param name="FailedAt">Failure time.</param>
/// <param name="RetryAfter">When the version may be attempted again.</param>
public sealed record FailedUpdateMarker(
    UpdateComponent Component,
    string Version,
    string Reason,
    DateTimeOffset FailedAt,
    DateTimeOffset RetryAfter);

/// <summary>Stages verified packages and applies them (msiexec / helper updater), tracking pending, rollback and failed state on disk.</summary>
public interface IUpdateApplier
{
    /// <summary>Copies the verified package into <c>pending-update\</c> and records it as pending.</summary>
    Task<PendingUpdate> StageAsync(UpdateManifest manifest, string packagePath, string currentVersion, CancellationToken cancellationToken);

    /// <summary>Pending update of <paramref name="component"/>, when any.</summary>
    Task<PendingUpdate?> GetPendingAsync(UpdateComponent component, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the pending update: Shell packages run synchronously through <c>msiexec /i /qn /norestart</c>;
    /// Agent packages are handed to the helper updater (or msiexec) detached, because the installer stops this
    /// service. Writes the rollback marker first. Returns <c>scheduled = false</c> when nothing is pending.
    /// </summary>
    Task<UpdateApplyResponse> ApplyAsync(UpdateComponent component, CancellationToken cancellationToken);

    /// <summary>Removes the pending record and staged package.</summary>
    Task ClearPendingAsync(UpdateComponent component, CancellationToken cancellationToken);

    /// <summary>Called at start-up with the running version: clears pending/rollback state when the update reached <paramref name="runningVersion"/>.</summary>
    Task ConfirmAppliedAsync(UpdateComponent component, string runningVersion, CancellationToken cancellationToken);

    /// <summary>Records a failed apply; the version is suppressed for <see cref="UpdateChecker.FailedSuppression"/>.</summary>
    Task MarkFailedAsync(UpdateComponent component, string version, string reason, CancellationToken cancellationToken);

    /// <summary><see langword="true"/> while <paramref name="version"/> is suppressed by a failed marker.</summary>
    Task<bool> IsMarkedFailedAsync(UpdateComponent component, string version, CancellationToken cancellationToken);

    /// <summary>Rollback marker of <paramref name="component"/>, when an apply is in flight or failed to complete.</summary>
    Task<RollbackMarker?> GetRollbackMarkerAsync(UpdateComponent component, CancellationToken cancellationToken);

    /// <summary>Deletes the rollback marker.</summary>
    Task ClearRollbackMarkerAsync(UpdateComponent component, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IUpdateApplier"/>: state files live in <c>pending-update\</c>
/// (<c>&lt;component&gt;.json</c>, <c>rollback-&lt;component&gt;.json</c>, <c>failed-&lt;component&gt;.json</c>), packages
/// under <c>pending-update\&lt;component&gt;\</c>. Installer logs go to <c>logs\update-&lt;component&gt;-&lt;version&gt;.log</c>.
/// </summary>
public sealed class UpdateApplier : IUpdateApplier, IDisposable
{
    /// <summary>Windows Installer executable.</summary>
    public const string MsiExec = "msiexec.exe";

    /// <summary>Helper that stops the service, installs the Agent MSI and restarts it.</summary>
    public const string UpdaterHelperFileName = "ClubShell.Updater.exe";

    /// <summary><c>ERROR_SUCCESS_REBOOT_REQUIRED</c>.</summary>
    public const int MsiRebootRequired = 3010;

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IProcessStarter _processStarter;
    private readonly IClock _clock;
    private readonly ILogger<UpdateApplier> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    /// <summary>Creates the applier.</summary>
    public UpdateApplier(IOptionsMonitor<AgentSettings> settings, IProcessStarter processStarter, IClock clock, ILogger<UpdateApplier> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(processStarter);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _processStarter = processStarter;
        _clock = clock;
        _logger = logger;
        UpdaterHelperPath = Path.Combine(AppContext.BaseDirectory, UpdaterHelperFileName);
    }

    /// <summary>Path of the helper updater used for Agent self-update; msiexec is used directly when the file does not exist.</summary>
    public string UpdaterHelperPath { get; set; }

    /// <summary>Root of the staging area (<c>pending-update\</c>).</summary>
    public string PendingDirectory => _settings.CurrentValue.PendingUpdateDir;

    /// <inheritdoc />
    public async Task<PendingUpdate> StageAsync(UpdateManifest manifest, string packagePath, string currentVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        ArgumentException.ThrowIfNullOrEmpty(currentVersion);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(PendingDirectory, ComponentName(manifest.Component));
            Directory.CreateDirectory(directory);
            var staged = Path.Combine(directory, Path.GetFileName(packagePath));
            if (!string.Equals(Path.GetFullPath(staged), Path.GetFullPath(packagePath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(packagePath, staged, overwrite: true);
            }

            var pending = new PendingUpdate(manifest.Component, manifest.Version, staged, manifest.Sha256, manifest.Mandatory, _clock.UtcNow, currentVersion, 0);
            await WriteAsync(PendingPath(manifest.Component), pending, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Component} {Version} staged at {Path}", manifest.Component, manifest.Version, staged);
            return pending;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public Task<PendingUpdate?> GetPendingAsync(UpdateComponent component, CancellationToken cancellationToken) =>
        ReadAsync<PendingUpdate>(PendingPath(component), cancellationToken);

    /// <inheritdoc />
    public async Task<UpdateApplyResponse> ApplyAsync(UpdateComponent component, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = await ReadAsync<PendingUpdate>(PendingPath(component), cancellationToken).ConfigureAwait(false);
            if (pending is null)
            {
                _logger.LogInformation("No pending {Component} update to apply", component);
                return new UpdateApplyResponse(false);
            }

            if (!File.Exists(pending.PackagePath))
            {
                await DeleteAsync(PendingPath(component), cancellationToken).ConfigureAwait(false);
                throw new UpdateException(UpdatePhase.Applying, $"Staged package {pending.PackagePath} is missing");
            }

            var now = _clock.UtcNow;
            pending = pending with { Attempts = pending.Attempts + 1 };
            await WriteAsync(PendingPath(component), pending, cancellationToken).ConfigureAwait(false);
            await WriteAsync(RollbackPath(component), new RollbackMarker(component, pending.PreviousVersion, pending.Version, pending.PackagePath, now, pending.Attempts), cancellationToken).ConfigureAwait(false);

            var logsDir = _settings.CurrentValue.LogsDir;
            Directory.CreateDirectory(logsDir);
            var logPath = Path.Combine(logsDir, $"update-{ComponentName(component)}-{pending.Version}.log");
            var (fileName, arguments) = BuildInstallCommand(pending.PackagePath, logPath);

            if (component == UpdateComponent.Agent)
            {
                if (File.Exists(UpdaterHelperPath))
                {
                    fileName = UpdaterHelperPath;
                    arguments = new[] { "--package", pending.PackagePath, "--version", pending.Version, "--log", logPath };
                }

                var pid = _processStarter.StartDetached(fileName, arguments, Path.GetDirectoryName(pending.PackagePath));
                _logger.LogInformation("Agent update {Version} handed to {FileName} (pid {Pid}); the service will be restarted by the installer", pending.Version, fileName, pid);
                return new UpdateApplyResponse(true, now);
            }

            _logger.LogInformation("Applying Shell update {Version} via {FileName}", pending.Version, fileName);
            var result = await _processStarter.RunAsync(fileName, arguments, InstallTimeout, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode is 0 or MsiRebootRequired)
            {
                await DeleteAsync(PendingPath(component), cancellationToken).ConfigureAwait(false);
                await DeleteAsync(RollbackPath(component), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Shell update {Version} applied (exit code {ExitCode})", pending.Version, result.ExitCode);
                return new UpdateApplyResponse(true, now);
            }

            var reason = $"{Path.GetFileName(fileName)} exited with {result.ExitCode}";
            await WriteFailedAsync(component, pending.Version, reason, cancellationToken).ConfigureAwait(false);
            _logger.LogError("Shell update {Version} failed: {Reason}; stderr: {StdErr}", pending.Version, reason, result.StandardError);
            throw new UpdateException(UpdatePhase.Applying, reason);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearPendingAsync(UpdateComponent component, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = await ReadAsync<PendingUpdate>(PendingPath(component), cancellationToken).ConfigureAwait(false);
            await DeleteAsync(PendingPath(component), cancellationToken).ConfigureAwait(false);
            if (pending is not null && File.Exists(pending.PackagePath))
            {
                File.Delete(pending.PackagePath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ConfirmAppliedAsync(UpdateComponent component, string runningVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runningVersion);
        var marker = await GetRollbackMarkerAsync(component, cancellationToken).ConfigureAwait(false);
        if (marker is null)
        {
            return;
        }

        if (SemanticVersion.TryParse(marker.ToVersion, out var target) && SemanticVersion.TryParse(runningVersion, out var running) && running == target)
        {
            _logger.LogInformation("{Component} update to {Version} confirmed; clearing pending state", component, runningVersion);
            await ClearPendingAsync(component, cancellationToken).ConfigureAwait(false);
            await ClearRollbackMarkerAsync(component, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning("{Component} is running {Running} but the last apply targeted {Target} (attempt {Attempts}); rollback marker kept", component, runningVersion, marker.ToVersion, marker.Attempts);
    }

    /// <inheritdoc />
    public Task MarkFailedAsync(UpdateComponent component, string version, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(reason);
        return WriteFailedAsync(component, version, reason, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsMarkedFailedAsync(UpdateComponent component, string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        var marker = await ReadAsync<FailedUpdateMarker>(FailedPath(component), cancellationToken).ConfigureAwait(false);
        return marker is not null
            && string.Equals(marker.Version, version, StringComparison.OrdinalIgnoreCase)
            && marker.RetryAfter > _clock.UtcNow;
    }

    /// <inheritdoc />
    public Task<RollbackMarker?> GetRollbackMarkerAsync(UpdateComponent component, CancellationToken cancellationToken) =>
        ReadAsync<RollbackMarker>(RollbackPath(component), cancellationToken);

    /// <inheritdoc />
    public Task ClearRollbackMarkerAsync(UpdateComponent component, CancellationToken cancellationToken) =>
        DeleteAsync(RollbackPath(component), cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Installer command line for a staged package: <c>msiexec /i &lt;msi&gt; /qn /norestart /l*v &lt;log&gt;</c>, or <c>&lt;exe&gt; /quiet /norestart</c>.</summary>
    public static (string FileName, IReadOnlyList<string> Arguments) BuildInstallCommand(string packagePath, string logPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        ArgumentException.ThrowIfNullOrEmpty(logPath);
        if (packagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return (packagePath, new[] { "/quiet", "/norestart", "/log", logPath });
        }

        return (MsiExec, new[] { "/i", packagePath, "/qn", "/norestart", "/l*v", logPath });
    }

    private static string ComponentName(UpdateComponent component) => component == UpdateComponent.Agent ? "agent" : "shell";

    private string PendingPath(UpdateComponent component) => Path.Combine(PendingDirectory, ComponentName(component) + ".json");

    private string RollbackPath(UpdateComponent component) => Path.Combine(PendingDirectory, "rollback-" + ComponentName(component) + ".json");

    private string FailedPath(UpdateComponent component) => Path.Combine(PendingDirectory, "failed-" + ComponentName(component) + ".json");

    private Task WriteFailedAsync(UpdateComponent component, string version, string reason, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        return WriteAsync(FailedPath(component), new FailedUpdateMarker(component, version, reason, now, now + UpdateChecker.FailedSuppression), cancellationToken);
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Update state file {Path} is unreadable; ignoring it", path);
            return null;
        }
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete update state file {Path}", path);
        }

        return Task.CompletedTask;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
