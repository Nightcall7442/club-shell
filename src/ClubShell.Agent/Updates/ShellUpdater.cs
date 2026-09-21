using System.IO.Compression;
using System.Runtime.Versioning;
using ClubShell.Agent.Users;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using ClubShell.Core.Updates;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Updates;

/// <summary>
/// Applies a staged Shell package (downloaded, verified and staged by <see cref="AgentUpdater"/>): stops the Shell
/// through <see cref="IShellRelauncher"/>, keeps a copy of the current install in <c>cache\updates\shell-backup\</c>,
/// runs the MSI / EXE silently as SYSTEM (<see cref="IUpdateApplier"/>) or extracts a ZIP over the Shell directory,
/// then relaunches the Shell. A failed apply restores the backup. <see cref="ConfirmRunningVersionAsync"/> is called
/// with the version the Shell reports in <c>auth.hello</c>; <see cref="RollbackAsync"/> restores the previous version
/// when the new Shell keeps crashing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellUpdater
{
    /// <summary>Backup directory name under <c>updates.downloadDir</c>.</summary>
    public const string BackupDirName = "shell-backup";

    /// <summary>Marker file inside the backup with the version it holds.</summary>
    public const string BackupVersionFileName = ".version";

    private const string DefaultShellDirectory = @"C:\Program Files\ClubShell\Shell";
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

    private readonly IUpdateApplier _applier;
    private readonly IShellRelauncher _relauncher;
    private readonly IUpdateEventSink _events;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<ShellUpdater> _logger;

    /// <summary>Creates the updater.</summary>
    public ShellUpdater(IUpdateApplier applier, IShellRelauncher relauncher, IUpdateEventSink events, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<ShellUpdater> logger)
    {
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(relauncher);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _applier = applier;
        _relauncher = relauncher;
        _events = events;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Directory of <c>shell.exePath</c>.</summary>
    public string ShellDirectory => Path.GetDirectoryName(_settings.CurrentValue.Shell.ExePath) is { Length: > 0 } directory ? directory : DefaultShellDirectory;

    /// <summary>Where the previous Shell install is kept for rollback.</summary>
    public string BackupDirectory => Path.Combine(_settings.CurrentValue.UpdatesDownloadDir, BackupDirName);

    /// <summary>Version held in <see cref="BackupDirectory"/>, or <see langword="null"/> when there is no backup.</summary>
    public string? BackupVersion
    {
        get
        {
            var marker = Path.Combine(BackupDirectory, BackupVersionFileName);
            try
            {
                return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Staged Shell update, when any.</summary>
    public Task<PendingUpdate?> GetPendingAsync(CancellationToken cancellationToken) =>
        _applier.GetPendingAsync(UpdateComponent.Shell, cancellationToken);

    /// <summary>
    /// Applies the staged Shell update now. Returns <c>scheduled = false</c> when nothing is staged; throws
    /// <see cref="UpdateException"/> after restoring the backup when the install failed. The Shell is relaunched in
    /// every case.
    /// </summary>
    public async Task<UpdateApplyResponse> ApplyAsync(CancellationToken cancellationToken)
    {
        var pending = await _applier.GetPendingAsync(UpdateComponent.Shell, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            _logger.LogInformation("No staged Shell update to apply");
            return new UpdateApplyResponse(false);
        }

        var version = pending.Version;
        _logger.LogInformation("Applying Shell update {Version} (previous {Previous})", version, pending.PreviousVersion);
        await ReportAsync(version, UpdatePhase.Applying, 0, null, cancellationToken).ConfigureAwait(false);
        var backedUp = false;
        try
        {
            await _relauncher.StopAsync(cancellationToken).ConfigureAwait(false);
            backedUp = BackupCurrent(pending.PreviousVersion);
            await ReportAsync(version, UpdatePhase.Applying, 25, null, cancellationToken).ConfigureAwait(false);

            var response = IsZipPackage(pending.PackagePath)
                ? await ApplyZipAsync(pending, cancellationToken).ConfigureAwait(false)
                : await _applier.ApplyAsync(UpdateComponent.Shell, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(_settings.CurrentValue.Shell.ExePath))
            {
                throw new UpdateException(UpdatePhase.Applying, $"Shell executable {_settings.CurrentValue.Shell.ExePath} is missing after the install");
            }

            await ReportAsync(version, UpdatePhase.Applying, 100, null, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Shell update {Version} applied", version);
            return response;
        }
        catch (UpdateException ex)
        {
            _logger.LogError(ex, "Shell update {Version} failed in phase {Phase}", version, ex.Phase);
            if (backedUp)
            {
                RestoreBackup();
            }

            await _applier.MarkFailedAsync(UpdateComponent.Shell, version, ex.Message, cancellationToken).ConfigureAwait(false);
            await _applier.ClearPendingAsync(UpdateComponent.Shell, cancellationToken).ConfigureAwait(false);
            await _applier.ClearRollbackMarkerAsync(UpdateComponent.Shell, cancellationToken).ConfigureAwait(false);
            await ReportAsync(version, UpdatePhase.Failed, 0, IpcError.Of(ErrorCode.Internal, ex.Message), cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            try
            {
                await _relauncher.RelaunchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Shell could not be relaunched after the update");
            }
        }
    }

    /// <summary>Restores the backed-up Shell install and relaunches; <see langword="false"/> when there is no backup.</summary>
    public async Task<bool> RollbackAsync(CancellationToken cancellationToken)
    {
        var backupVersion = BackupVersion;
        if (backupVersion is null || !Directory.Exists(BackupDirectory))
        {
            _logger.LogWarning("Shell rollback requested but no backup is available");
            return false;
        }

        _logger.LogWarning("Rolling the Shell back to {Version}", backupVersion);
        try
        {
            await _relauncher.StopAsync(cancellationToken).ConfigureAwait(false);
            RestoreBackup();
            await _applier.ClearPendingAsync(UpdateComponent.Shell, cancellationToken).ConfigureAwait(false);
            await _applier.ClearRollbackMarkerAsync(UpdateComponent.Shell, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            try
            {
                await _relauncher.RelaunchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Shell could not be relaunched after the rollback");
            }
        }
    }

    /// <summary>Called with the version the Shell reported in <c>auth.hello</c>; clears pending / rollback state when it matches the applied update.</summary>
    public Task ConfirmRunningVersionAsync(string shellVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(shellVersion);
        return _applier.ConfirmAppliedAsync(UpdateComponent.Shell, shellVersion, cancellationToken);
    }

    /// <summary><see langword="true"/> when <paramref name="path"/> starts with the ZIP local-file-header magic.</summary>
    public static bool IsZipPackage(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[4];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header.SequenceEqual(ZipMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private async Task<UpdateApplyResponse> ApplyZipAsync(PendingUpdate pending, CancellationToken cancellationToken)
    {
        var sha256 = await Signing.Sha256FileAsync(pending.PackagePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(sha256, pending.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdatePhase.Verifying, "Staged Shell package hash does not match the manifest");
        }

        var target = ShellDirectory;
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(pending.PackagePath, target, overwriteFiles: true);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdatePhase.Applying, $"Extracting the Shell package failed: {ex.Message}", ex);
        }

        await _applier.ClearPendingAsync(UpdateComponent.Shell, cancellationToken).ConfigureAwait(false);
        await _applier.ClearRollbackMarkerAsync(UpdateComponent.Shell, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Shell package {Version} extracted to {Directory}", pending.Version, target);
        return new UpdateApplyResponse(true, _clock.UtcNow);
    }

    private bool BackupCurrent(string previousVersion)
    {
        var source = ShellDirectory;
        var backup = BackupDirectory;
        if (!Directory.Exists(source))
        {
            _logger.LogWarning("Shell directory {Directory} does not exist; nothing to back up", source);
            return false;
        }

        try
        {
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            CopyDirectory(source, backup);
            File.WriteAllText(Path.Combine(backup, BackupVersionFileName), previousVersion);
            _logger.LogInformation("Shell {Version} backed up to {Backup}", previousVersion, backup);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Shell backup failed; rollback will not be possible");
            return false;
        }
    }

    private void RestoreBackup()
    {
        var backup = BackupDirectory;
        var target = ShellDirectory;
        try
        {
            CopyDirectory(backup, target);
            File.Delete(Path.Combine(target, BackupVersionFileName));
            _logger.LogInformation("Shell restored from {Backup}", backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Shell restore from {Backup} failed", backup);
        }
    }

    private async Task ReportAsync(string version, UpdatePhase phase, int percent, IpcError? error, CancellationToken cancellationToken)
    {
        try
        {
            await _events.PublishAsync(new UpdateProgress(UpdateComponent.Shell, version, phase, percent, 0, 0, error), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "update.progress could not be published");
        }
    }
}
