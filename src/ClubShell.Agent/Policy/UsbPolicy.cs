using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsbPolicyModule : IPolicyModule
{
    /// <summary>Section key.</summary>
    public const string SectionKey = "usb";

    private const uint IoctlStorageEjectMedia = 0x002D4808;

    private readonly ILogger<UsbPolicyModule> _logger;
    private readonly object _gate = new();
    private RegistrySnapshot? _snapshot;

    /// <summary>Creates the module.</summary>
    public UsbPolicyModule(ILogger<UsbPolicyModule> logger) => _logger = logger;

    /// <inheritdoc />
    public string Section => SectionKey;

    /// <inheritdoc />
    public Task<PolicyModuleResult> ApplyAsync(PcPolicy policy, PolicyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        UsbPolicy usb = policy.Usb;
        var notes = new List<string>();
        lock (_gate)
        {
            List<PolicyRegistryRule> rules = PolicyRegistry.RulesFor(policy, null)
                .Where(r => r.Hive == RegistryHive.LocalMachine && IsUsbRule(r.Key))
                .ToList();
            RegistrySnapshot before = RegistryPolicyOps.Apply(rules);
            _snapshot ??= before;
            notes.Add($"{rules.Count} HKLM value(s) written");
        }

        notes.Add(usb.AllowStorage ? "USB mass storage allowed (USBSTOR start=3)" : "USB mass storage disabled for new devices (USBSTOR start=4)");
        notes.Add(usb.AllowHid ? "USB HID installation allowed" : "USB HID class installation denied");
        if (!usb.AllowStorage)
        {
            notes.AddRange(EjectRemovableVolumes());
        }

        return Task.FromResult(PolicyModuleResult.Ok(notes.ToArray()));
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
