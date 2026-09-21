using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Agent.Policy;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using ClubShell.Windows.Registry;
using ClubShell.Windows.Sessions;
using ClubShell.Windows.Users;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace ClubShell.Agent.Users;

/// <summary>
/// Provisions the kiosk account (<c>shell.kioskUser</c>, ARCHITECTURE.md §6.1 step 9): creates the local user when
/// missing, keeps it enabled and a plain member of <c>BUILTIN\Users</c>, owns a strong random password that is
/// rotated at start (<c>rotatePasswordOnStart</c>), every <see cref="RotationPeriod"/>, or whenever the stored one no
/// longer logs on. The password lives DPAPI-protected in <c>secure\kiosk.cred</c>. The live credentials are exposed
/// through <see cref="IKioskCredentials"/> for the shell-replacement / auto-logon policy; an enabled auto-logon for
/// this user is re-pointed at the new password on every rotation. User shell folders are redirected to the data
/// drive (<see cref="ApplyFolderRedirect"/>) once the profile exists. Registered as the first hosted service so the
/// SID exists before the pipe server (pipe DACL, Shell token ACL) and the watchdog start.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TempUserProvisioner : IKioskCredentials, IHostedService, IDisposable
{
    /// <summary>File name of the protected credential store under <c>secure\</c>.</summary>
    public const string CredentialsFileName = "kiosk.cred";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly LocalUserManager _users;
    private readonly FolderRedirect _redirect;
    private readonly ShellRegistry _shellRegistry;
    private readonly ITokenProtector _protector;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<TempUserProvisioner> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private KioskCredentialsRecord? _current;
    private bool _disposed;

    /// <summary>Creates the provisioner.</summary>
    public TempUserProvisioner(LocalUserManager users, FolderRedirect redirect, ShellRegistry shellRegistry, ITokenProtector protector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<TempUserProvisioner> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(redirect);
        ArgumentNullException.ThrowIfNull(shellRegistry);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _users = users;
        _redirect = redirect;
        _shellRegistry = shellRegistry;
        _protector = protector;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised after provisioning and after every password rotation with the new credentials.</summary>
    public event EventHandler<IKioskCredentials>? CredentialsChanged;

    /// <summary>Maximum age of a password before it is rotated at the next <see cref="ProvisionAsync"/>.</summary>
    public TimeSpan RotationPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Base directory for shell-folder redirection (<c>&lt;root&gt;\&lt;user&gt;\Desktop</c>, ...). <see langword="null"/> picks
    /// <c>&lt;largest non-system fixed drive&gt;\ClubShell\Users</c>; redirection is skipped when no such drive exists.
    /// </summary>
    public string? RedirectRoot { get; set; }

    /// <summary><see langword="true"/> once <see cref="ProvisionAsync"/> completed.</summary>
    public bool IsProvisioned => Volatile.Read(ref _current) is not null;

    /// <summary>Kiosk account name (<c>shell.kioskUser.name</c>).</summary>
    public string UserName => Volatile.Read(ref _current)?.UserName ?? _settings.CurrentValue.Shell.KioskUser.Name;

    /// <summary>Current password; empty until provisioned. Never logged.</summary>
    public string Password => Volatile.Read(ref _current)?.Password ?? string.Empty;

    /// <summary>SID of the kiosk account; empty until provisioned.</summary>
    public string Sid => Volatile.Read(ref _current)?.Sid ?? string.Empty;

    /// <summary>Time of the last password rotation, when provisioned.</summary>
    public DateTimeOffset? RotatedAt => Volatile.Read(ref _current)?.RotatedAt;

    /// <summary>Path of the protected credential store.</summary>
    public string CredentialsPath => Path.Combine(_settings.CurrentValue.SecureDir, CredentialsFileName);

    /// <summary>Bound on the provisioning attempt made by <see cref="StartAsync"/>.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Hosted-service start: runs <see cref="ProvisionAsync"/> once, bounded by <see cref="StartupTimeout"/>. Failures
    /// are logged rather than thrown so the host still starts; <c>AgentWorker</c> retries while
    /// <see cref="IsProvisioned"/> is <see langword="false"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timed.CancelAfter(StartupTimeout);
        try
        {
            _ = await ProvisionAsync(timed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kiosk user provisioning at start failed; pipe and watchdog run without the kiosk SID until a retry succeeds");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Idempotent provisioning: ensures the account exists (when <c>createIfMissing</c>), is enabled, in
    /// <c>BUILTIN\Users</c>, has a known password (rotated when required or when the stored one fails to log on),
    /// stores it, refreshes an enabled auto-logon and redirects the shell folders when the profile already exists.
    /// </summary>
    /// <exception cref="InvalidOperationException">The account is missing and <c>createIfMissing</c> is off, or its SID cannot be resolved.</exception>
    public async Task<IKioskCredentials> ProvisionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var kiosk = _settings.CurrentValue.Shell.KioskUser;
            var name = kiosk.Name;
            var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.UtcNow;
            var created = false;
            var rotated = true;
            string password;

            if (!_users.Exists(name))
            {
                if (!kiosk.CreateIfMissing)
                {
                    throw new InvalidOperationException($"Kiosk user '{name}' does not exist and shell.kioskUser.createIfMissing is false.");
                }

                password = LocalUserManager.GeneratePassword();
                _ = _users.Create(name, password, name, "ClubShell kiosk account");
                created = true;
            }
            else
            {
                var reusable = stored is not null
                    && string.Equals(stored.UserName, name, StringComparison.OrdinalIgnoreCase)
                    && !kiosk.RotatePasswordOnStart
                    && now - stored.RotatedAt < RotationPeriod
                    && CanLogon(name, stored.Password);
                if (reusable && stored is not null)
                {
                    password = stored.Password;
                    rotated = false;
                }
                else
                {
                    password = LocalUserManager.GeneratePassword();
                    _users.SetPassword(name, password);
                }

                _users.SetEnabled(name, true);
                _users.AddToGroup(name);
            }

            var sid = _users.GetSid(name) ?? throw new InvalidOperationException($"SID of kiosk user '{name}' could not be resolved.");
            var record = rotated || stored is null
                ? new KioskCredentialsRecord(name, password, sid, now)
                : stored with { Sid = sid };
            await SaveAsync(record, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, record);
            RefreshAutoLogon(record);
            _ = ApplyFolderRedirect();
            _logger.LogInformation("Kiosk user {User} provisioned (created {Created}, password rotated {Rotated})", name, created, rotated);
            RaiseChanged(record);
            return record;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Snapshot of the current credentials.</summary>
    /// <exception cref="InvalidOperationException"><see cref="ProvisionAsync"/> has not completed yet.</exception>
    public IKioskCredentials GetCredentials() =>
        Volatile.Read(ref _current) ?? throw new InvalidOperationException("Kiosk user is not provisioned yet; call ProvisionAsync first.");

    /// <summary>Sets a fresh random password, stores it and refreshes an enabled auto-logon.</summary>
    /// <exception cref="InvalidOperationException"><see cref="ProvisionAsync"/> has not completed yet.</exception>
    public async Task<IKioskCredentials> RotatePasswordAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _current) ?? throw new InvalidOperationException("Kiosk user is not provisioned yet; call ProvisionAsync first.");
            var password = LocalUserManager.GeneratePassword();
            _users.SetPassword(current.UserName, password);
            var record = current with { Password = password, RotatedAt = _clock.UtcNow };
            await SaveAsync(record, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, record);
            RefreshAutoLogon(record);
            _logger.LogInformation("Kiosk user {User} password rotated", record.UserName);
            RaiseChanged(record);
            return record;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Redirects Desktop/Documents/Downloads/Pictures/Videos/Saved Games of the kiosk user to the data drive.
    /// Returns <see langword="false"/> (without error) when not provisioned, when no data drive is available or when
    /// the user's profile does not exist yet (first logon pending): call again after the profile was created.
    /// </summary>
    public bool ApplyFolderRedirect()
    {
        var current = Volatile.Read(ref _current);
        if (current is null)
        {
            return false;
        }

        var root = RedirectRoot ?? DetectDataRoot();
        if (root is null)
        {
            _logger.LogDebug("Folder redirection skipped: no data drive");
            return false;
        }

        var target = RedirectRoot is null
            ? Path.Combine(root, "ClubShell", "Users", current.UserName)
            : Path.Combine(root, current.UserName);

        var profile = RegistryHelper.GetProfileImagePath(current.Sid);
        var ntUserDat = profile is null ? null : Path.Combine(profile, "NTUSER.DAT");
        if (!RegistryHelper.Exists(RegistryHive.Users, current.Sid) && (ntUserDat is null || !File.Exists(ntUserDat)))
        {
            _logger.LogInformation("Folder redirection for {User} deferred: profile not created yet", current.UserName);
            return false;
        }

        try
        {
            _ = _redirect.Apply(current.Sid, target, ntUserDat);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Folder redirection of {User} to {Target} failed", current.UserName, target);
            return false;
        }
    }

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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static bool CanLogon(string name, string password)
    {
        try
        {
            using var token = ProcessAsUser.LogonUser(name, ".", password);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string? DetectDataRoot()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        DriveInfo? best = null;
        long bestFree = -1;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var free = drive.AvailableFreeSpace;
                if (free > bestFree)
                {
                    bestFree = free;
                    best = drive;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive vanished or is inaccessible; skip it.
            }
        }

        return best?.Name;
    }

    private void RefreshAutoLogon(KioskCredentialsRecord record)
    {
        try
        {
            if (_shellRegistry.IsAutoLogonEnabled && string.Equals(_shellRegistry.AutoLogonUserName, record.UserName, StringComparison.OrdinalIgnoreCase))
            {
                _shellRegistry.SetAutoLogon(record.UserName, record.Password);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Auto-logon secret for {User} could not be refreshed", record.UserName);
        }
    }

    private void RaiseChanged(KioskCredentialsRecord record)
    {
        try
        {
            CredentialsChanged?.Invoke(this, record);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "CredentialsChanged listener threw");
        }
    }

    private async Task<KioskCredentialsRecord?> LoadAsync(CancellationToken cancellationToken)
    {
        var path = CredentialsPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var ciphertext = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (ciphertext.Length == 0)
            {
                return null;
            }

            var plaintext = _protector.Unprotect(ciphertext);
            var record = JsonSerializer.Deserialize<KioskCredentialsRecord>(plaintext, JsonOptions);
            CryptographicOperations.ZeroMemory(plaintext);
            return record is { UserName.Length: > 0, Password.Length: > 0 } ? record : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Kiosk credential store {Path} is unreadable; a new password will be generated", path);
            return null;
        }
    }

    private async Task SaveAsync(KioskCredentialsRecord record, CancellationToken cancellationToken)
    {
        var path = CredentialsPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        var ciphertext = _protector.Protect(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, ciphertext, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Persisted credential set (DPAPI-wrapped JSON).</summary>
    private sealed record KioskCredentialsRecord(string UserName, string Password, string Sid, DateTimeOffset RotatedAt) : IKioskCredentials;
}
