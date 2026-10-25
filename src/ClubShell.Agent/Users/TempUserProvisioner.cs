using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Agent.Policy;
using ClubShell.Contracts.Commands;
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
/// this user is re-pointed at the new password the moment it is set. A rotation due while auto-logon has yet to log
/// the kiosk on waits for the kiosk session (<see cref="RotateIfOwedAsync"/>). An existing account in a privileged
/// local group is refused (<see cref="KioskAccountRefusedException"/>, <see cref="Status"/>) unless
/// <c>removeFromPrivilegedGroups</c>. User shell folders are redirected (<c>shell.kioskUser.folderRedirect</c>,
/// <see cref="ApplyFolderRedirect"/>) once the profile exists and emptied on a profile reset
/// (<see cref="ClearRedirectedFolders"/>). Registered as the first hosted service so the SID exists before the pipe
/// server (pipe DACL, Shell token ACL) and the watchdog start.
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
    private HeartbeatKioskUser? _status;
    private string? _redirectTarget;
    private volatile bool _rotationOwed;
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

    /// <summary>Maximum age of a password before it is rotated (<c>shell.kioskUser.passwordRotationDays</c>).</summary>
    public TimeSpan RotationPeriod => TimeSpan.FromDays(_settings.CurrentValue.Shell.KioskUser.PasswordRotationDays);

    /// <summary><see langword="true"/> once <see cref="ProvisionAsync"/> completed.</summary>
    public bool IsProvisioned => Volatile.Read(ref _current) is not null;

    /// <summary><see langword="true"/> while a rotation that was due at provisioning waits for the kiosk session (<see cref="RotateIfOwedAsync"/>).</summary>
    public bool RotationOwed => _rotationOwed;

    /// <summary>State of the kiosk account as the heartbeat reports it (<c>kioskUser</c>).</summary>
    public HeartbeatKioskUser Status =>
        IsProvisioned ? new HeartbeatKioskUser(UserName, Ready: true) : Volatile.Read(ref _status) ?? new HeartbeatKioskUser(UserName, Ready: false);

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
            Volatile.Write(ref _status, null);
            var kiosk = _settings.CurrentValue.Shell.KioskUser;
            var name = kiosk.Name;
            var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.UtcNow;
            var created = false;
            var source = KioskPasswordSource.New;
            var owed = false;
            string password;

            if (!_users.Exists(name))
            {
                if (!kiosk.CreateIfMissing)
                {
                    Volatile.Write(ref _status, new HeartbeatKioskUser(name, Ready: false, KioskUserProblems.Missing));
                    throw new InvalidOperationException($"Kiosk user '{name}' does not exist and shell.kioskUser.createIfMissing is false.");
                }

                password = LocalUserManager.GeneratePassword();
                _ = _users.Create(name, password, name, "ClubShell kiosk account");
                created = true;
            }
            else
            {
                // Before anything is changed: a privileged account is left exactly as it was.
                EnsureNotPrivileged(name, kiosk.RemoveFromPrivilegedGroups);
                var storedMatches = stored is not null && string.Equals(stored.UserName, name, StringComparison.OrdinalIgnoreCase);
                string? autoLogonPassword = null;
                (source, owed) = KioskProvisioningRules.PlanPassword(
                    storedLogsOn: () => storedMatches && CanLogon(name, stored!.Password),
                    storedFresh: storedMatches && now - stored!.RotatedAt < RotationPeriod,
                    rotateOnStart: kiosk.RotatePasswordOnStart,
                    autoLogonPending: IsAutoLogonPending(name),
                    autoLogonPasswordLogsOn: () => (autoLogonPassword = ReadAutoLogonPassword()) is { Length: > 0 } candidate && CanLogon(name, candidate));
                switch (source)
                {
                    case KioskPasswordSource.Stored:
                        password = stored!.Password;
                        break;
                    case KioskPasswordSource.AutoLogon:
                        password = autoLogonPassword!;
                        break;
                    default:
                        password = LocalUserManager.GeneratePassword();
                        _users.SetPassword(name, password);
                        // Winlogon checks the LSA secret against SAM: point it at the new password at once, not after the
                        // profile and the credential store, or a logon in between fails (SHELL_CHANGES 29).
                        RefreshAutoLogon(name, password);
                        break;
                }

                _users.SetEnabled(name, true);
                _users.AddToGroup(name);
            }

            var sid = _users.GetSid(name) ?? throw new InvalidOperationException($"SID of kiosk user '{name}' could not be resolved.");

            // A fresh account has no profile until its first logon, and the shell replacement (per-user hive) and the
            // auto-logon it turns on wait for one: create the profile now, so the first boot already logs the kiosk on.
            try
            {
                _ = _users.EnsureProfile(name, sid);
            }
            catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
            {
                _logger.LogWarning(ex, "Profile of kiosk user {User} could not be created; the shell waits for its first logon", name);
            }
            var record = source == KioskPasswordSource.Stored
                ? stored! with { Sid = sid }
                : new KioskCredentialsRecord(name, password, sid, now);
            await SaveAsync(record, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, record);
            _rotationOwed = owed;
            RefreshAutoLogon(record.UserName, record.Password);
            _ = ApplyFolderRedirect();
            _logger.LogInformation(
                "Kiosk user {User} provisioned (created {Created}, password {PasswordState}{Owed})",
                name,
                created,
                created ? "set" : source switch { KioskPasswordSource.Stored => "kept", KioskPasswordSource.AutoLogon => "taken from auto-logon", _ => "rotated" },
                owed ? ", rotation waits for the kiosk logon" : "");
            RaiseChanged(record);
            return record;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not KioskAccountRefusedException && Volatile.Read(ref _status)?.Problem is null)
        {
            Volatile.Write(ref _status, new HeartbeatKioskUser(_settings.CurrentValue.Shell.KioskUser.Name, Ready: false, KioskUserProblems.ProvisioningFailed));
            throw;
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
            RefreshAutoLogon(current.UserName, password);
            var record = current with { Password = password, RotatedAt = _clock.UtcNow };
            await SaveAsync(record, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, record);
            _rotationOwed = false;
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
    /// Rotates the password that was due at provisioning but waited for Windows auto-logon (<see cref="RotationOwed"/>),
    /// once the kiosk session exists. Called by the maintenance loop; returns <see langword="true"/> when it rotated.
    /// </summary>
    public async Task<bool> RotateIfOwedAsync(CancellationToken cancellationToken)
    {
        if (!_rotationOwed || Volatile.Read(ref _current) is not { } current || !HasSession(current.UserName))
        {
            return false;
        }

        _ = await RotatePasswordAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Redirects Desktop/Documents/Downloads/Pictures/Videos/Saved Games of the kiosk user (<c>shell.kioskUser.folderRedirect</c>).
    /// Returns <see langword="false"/> (without error) when redirection is off, when not provisioned, when no data drive
    /// is available or when the user's profile does not exist yet (first logon pending): call again after the profile was created.
    /// </summary>
    public bool ApplyFolderRedirect()
    {
        var current = Volatile.Read(ref _current);
        var settings = _settings.CurrentValue.Shell.KioskUser.FolderRedirect;
        if (current is null || !settings.Enabled)
        {
            return false;
        }

        var target = RedirectTargetOf(current.UserName, settings);
        if (target is null)
        {
            _logger.LogDebug("Folder redirection skipped: no data drive");
            return false;
        }

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
            Volatile.Write(ref _redirectTarget, target);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Folder redirection of {User} to {Target} failed", current.UserName, target);
            return false;
        }
    }

    /// <summary>
    /// Empties the kiosk user's redirected folders (<c>shell.kioskUser.folderRedirect.clearOnReset</c>); called by the
    /// profile reset. They live outside the profile, so deleting the profile alone would hand the previous player's files
    /// to the next one. Never throws.
    /// </summary>
    public void ClearRedirectedFolders()
    {
        var settings = _settings.CurrentValue.Shell.KioskUser.FolderRedirect;
        if (!settings.Enabled || !settings.ClearOnReset || Volatile.Read(ref _current) is not { } current)
        {
            return;
        }

        // Where the folders were redirected at the last apply; before the first one of this start, where they would go.
        var target = Volatile.Read(ref _redirectTarget) ?? RedirectTargetOf(current.UserName, settings);
        if (target is null || !Directory.Exists(target))
        {
            return;
        }

        try
        {
            _ = _redirect.Clear(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Redirected folders of {User} under {Target} could not be emptied", current.UserName, target);
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

    private static bool HasSession(string userName)
    {
        try
        {
            return WtsSessions.FindByUser(userName) is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where the folders go: <c>&lt;root&gt;\&lt;user&gt;</c> for a configured root, else <c>ClubShell\Users\&lt;user&gt;</c>
    /// on the non-system fixed drive that already holds it, else on the one with the most free space. Sticking to the
    /// drive that already holds the folders keeps them in one place when free space shifts between starts.
    /// </summary>
    private static string? RedirectTargetOf(string userName, FolderRedirectSettings settings) =>
        KioskProvisioningRules.RedirectTarget(settings.Root, string.IsNullOrWhiteSpace(settings.Root) ? DetectDataRoot(userName) : null, userName);

    private static string? DetectDataRoot(string userName)
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

                if (Directory.Exists(Path.Combine(drive.Name, "ClubShell", "Users", userName)))
                {
                    return drive.Name;
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

    private bool IsAutoLogonPending(string userName)
    {
        try
        {
            return _shellRegistry.IsAutoLogonEnabled
                && string.Equals(_shellRegistry.AutoLogonUserName, userName, StringComparison.OrdinalIgnoreCase)
                && !HasSession(userName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Auto-logon state unreadable; the kiosk password is handled as without auto-logon");
            return false;
        }
    }

    private string? ReadAutoLogonPassword()
    {
        try
        {
            return _shellRegistry.GetAutoLogonPassword();
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Auto-logon password unreadable");
            return null;
        }
    }

    /// <summary>
    /// Refuses an existing account in a privileged local group, or removes it from those groups when
    /// <paramref name="removeFromPrivilegedGroups"/>. Runs before the account is changed in any way.
    /// </summary>
    /// <exception cref="KioskAccountRefusedException">The account is privileged and may not be taken over.</exception>
    private void EnsureNotPrivileged(string userName, bool removeFromPrivilegedGroups)
    {
        var groups = _users.GetPrivilegedGroups(userName);
        var names = groups.Select(g => g.Name).ToArray();
        switch (KioskProvisioningRules.OnPrivilegedGroups(groups.Count, removeFromPrivilegedGroups))
        {
            case PrivilegedAccountAction.Accept:
                return;
            case PrivilegedAccountAction.RemoveFromGroups:
                foreach (var (sid, _) in groups)
                {
                    _users.RemoveFromGroup(userName, sid);
                }

                _logger.LogWarning(
                    "Kiosk account {User} was a member of {Groups}; removed from them (shell.kioskUser.removeFromPrivilegedGroups)",
                    userName,
                    string.Join(", ", names));
                return;
            default:
                Volatile.Write(ref _status, new HeartbeatKioskUser(userName, Ready: false, KioskUserProblems.PrivilegedAccount, names));
                throw new KioskAccountRefusedException(userName, names);
        }
    }

    private void RefreshAutoLogon(string userName, string password)
    {
        try
        {
            if (_shellRegistry.IsAutoLogonEnabled && string.Equals(_shellRegistry.AutoLogonUserName, userName, StringComparison.OrdinalIgnoreCase))
            {
                _shellRegistry.SetAutoLogon(userName, password);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Auto-logon secret for {User} could not be refreshed", userName);
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

/// <summary>
/// The configured kiosk account exists and is a member of Administrators or another privileged local group
/// (<see cref="LocalUserManager.PrivilegedGroupSids"/>): the Agent leaves it untouched rather than run the kiosk with those rights.
/// </summary>
public sealed class KioskAccountRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public KioskAccountRefusedException(string userName, IReadOnlyList<string> groups)
        : base($"Kiosk account '{userName}' already exists and is a member of {string.Join(", ", groups)}; the Agent will not run the kiosk under it. " +
               "Set shell.kioskUser.name to another account, or turn on shell.kioskUser.removeFromPrivilegedGroups to take it over without those rights.")
    {
        UserName = userName;
        Groups = groups;
    }

    /// <summary>Account name.</summary>
    public string UserName { get; }

    /// <summary>Names of the privileged groups it belongs to.</summary>
    public IReadOnlyList<string> Groups { get; }
}
