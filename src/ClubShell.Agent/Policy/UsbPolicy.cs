using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using ClubShell.Agent.Watchdog;
using ClubShell.Contracts.Pcs;
using ClubShell.Windows.Native;
using ClubShell.Windows.Registry;

using Microsoft.Win32;

using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Policy;

/// <summary>
/// Section <c>usb</c>: machine-wide USBSTOR service start value and DeviceInstall class restrictions from
/// <see cref="PolicyRegistry.RulesFor"/> (HKLM rules only), plus ejection of removable volumes that are already
/// mounted when storage is blocked (the registry only stops newly attached devices). Revert restores the snapshot.
/// During an administrator's maintenance window (<see cref="AdminMaintenance"/>, SHELL_CHANGES 28) storage is allowed
/// whatever the policy says; the policy is applied again when the window ends.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsbPolicyModule : IPolicyModule, IDisposable
{
    /// <summary>Section key.</summary>
    public const string SectionKey = "usb";

    private const uint IoctlStorageEjectMedia = 0x002D4808;

    private readonly ILogger<UsbPolicyModule> _logger;
    private readonly AdminMaintenance? _maintenance;
    private readonly object _gate = new();
    private RegistrySnapshot? _snapshot;
    private PcPolicy? _applied;

    /// <summary>Creates the module; <paramref name="maintenance"/> lets an administrator use USB storage.</summary>
    public UsbPolicyModule(ILogger<UsbPolicyModule> logger, AdminMaintenance? maintenance = null)
    {
        _logger = logger;
        _maintenance = maintenance;
        if (maintenance is not null)
        {
            maintenance.Changed += OnMaintenanceChanged;
        }
    }

    /// <inheritdoc />
    public string Section => SectionKey;

    /// <inheritdoc />
    public Task<PolicyModuleResult> ApplyAsync(PcPolicy policy, PolicyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PolicyModuleResult.Ok(Apply(policy)));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_maintenance is not null)
        {
            _maintenance.Changed -= OnMaintenanceChanged;
        }
    }

    private string[] Apply(PcPolicy policy)
    {
        bool maintenance = _maintenance?.IsActive == true;
        UsbPolicy usb = maintenance ? policy.Usb with { AllowStorage = true } : policy.Usb;
        var notes = new List<string>();
        lock (_gate)
        {
            List<PolicyRegistryRule> rules = PolicyRegistry.RulesFor(policy with { Usb = usb }, null)
                .Where(r => r.Hive == RegistryHive.LocalMachine && IsUsbRule(r.Key))
                .ToList();
            RegistrySnapshot before = RegistryPolicyOps.Apply(rules);
            _snapshot ??= before;
            _applied = policy;
            notes.Add($"{rules.Count} HKLM value(s) written");
        }

        if (maintenance && !policy.Usb.AllowStorage)
        {
            notes.Add("administrator maintenance: USB mass storage allowed until it ends");
        }

        notes.Add(usb.AllowStorage ? "USB mass storage allowed (USBSTOR start=3)" : "USB mass storage disabled for new devices (USBSTOR start=4)");
        notes.Add(usb.AllowHid ? "USB HID installation allowed" : "USB HID class installation denied");
        if (!usb.AllowStorage)
        {
            notes.AddRange(EjectRemovableVolumes());
        }

        return notes.ToArray();
    }

    /// <summary>Re-applies the last policy when a maintenance window opens or ends (storage on, then back to the policy).</summary>
    private void OnMaintenanceChanged(object? sender, bool active)
    {
        PcPolicy? policy;
        lock (_gate)
        {
            policy = _applied;
        }

        if (policy is null || policy.Usb.AllowStorage)
        {
            return;
        }

        try
        {
            string[] notes = Apply(policy);
            _logger.LogInformation("USB policy re-applied for maintenance {State}: {Notes}", active ? "start" : "end", string.Join("; ", notes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "USB policy could not be re-applied for maintenance {State}", active ? "start" : "end");
        }
    }

    /// <inheritdoc />
    public Task RevertAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_snapshot is { } snapshot)
            {
                RegistryHelper.Apply(snapshot);
                _snapshot = null;
                _logger.LogInformation("USB policy reverted ({Count} value(s))", snapshot.Values.Count);
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsUsbRule(string key) =>
        key.Equals(PolicyRegistry.UsbStorKey, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(PolicyRegistry.DeviceInstallRestrictionsKey, StringComparison.OrdinalIgnoreCase);

    private List<string> EjectRemovableVolumes()
    {
        var notes = new List<string>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Removable volumes could not be enumerated");
            notes.Add("removable volumes not enumerated: " + ex.Message);
            return notes;
        }

        foreach (DriveInfo drive in drives)
        {
            if (drive.DriveType != DriveType.Removable)
            {
                continue;
            }

            string volume = drive.Name.TrimEnd('\\');
            notes.Add(TryEject(volume) ? $"ejected removable volume {volume}" : $"removable volume {volume} could not be ejected");
        }

        return notes;
    }

    /// <summary>Lock (best effort), dismount and eject <paramref name="volume"/> (<c>E:</c>) through <c>\\.\E:</c>.</summary>
    private bool TryEject(string volume)
    {
        using SafeDeviceHandle handle = Kernel32.CreateFileW(
            @"\\.\" + volume,
            NativeConst.GENERIC_READ | NativeConst.GENERIC_WRITE,
            NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE,
            0,
            NativeConst.OPEN_EXISTING,
            0,
            0);
        if (handle.IsInvalid)
        {
            _logger.LogWarning("CreateFile({Volume}) failed: {Error}", volume, Win32Error.Message(Win32Error.Last()));
            return false;
        }

        if (!Ioctl(handle, NativeConst.FSCTL_LOCK_VOLUME))
        {
            _logger.LogDebug("Volume {Volume} is in use; dismounting anyway", volume);
        }

        _ = Ioctl(handle, NativeConst.FSCTL_DISMOUNT_VOLUME);
        if (!Ioctl(handle, IoctlStorageEjectMedia))
        {
            _logger.LogWarning("Eject({Volume}) failed: {Error}", volume, Win32Error.Message(Win32Error.Last()));
            return false;
        }

        _logger.LogInformation("Removable volume {Volume} ejected (USB storage blocked by policy)", volume);
        return true;
    }

    private static bool Ioctl(SafeHandle handle, uint code) => Kernel32.DeviceIoControl(handle, code, 0, 0, 0, 0, out _, 0);
}
