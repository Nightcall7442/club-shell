using System.Runtime.Versioning;
using ClubShell.Contracts.Pcs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ClubShell.Windows.Registry;

/// <summary>One registry write derived from a <see cref="Policy"/>.</summary>
/// <param name="Hive">Hive.</param>
/// <param name="Key">Key path below the hive.</param>
/// <param name="Name">Value name.</param>
/// <param name="Value">Data to write, or <see langword="null"/> to delete the value.</param>
/// <param name="Kind">Value kind.</param>
public sealed record PolicyRegistryRule(RegistryHive Hive, string Key, string Name, object? Value, RegistryValueKind Kind);

/// <summary>Outcome of <see cref="PolicyRegistry.Apply"/>.</summary>
/// <param name="Applied">Rules written, in order.</param>
/// <param name="Snapshot">State before the write; pass to <see cref="PolicyRegistry.Revert"/>.</param>
public sealed record PolicyApplyResult(IReadOnlyList<PolicyRegistryRule> Applied, RegistrySnapshot Snapshot);

/// <summary>
/// Translates <see cref="ExplorerPolicy"/> and <see cref="UsbPolicy"/> into registry policy values and applies them
/// with snapshot/revert (ARCHITECTURE.md §3: registry writes only under fixed keys).
/// <para>
/// Explorer lockdown goes to the user hive (<c>HKU\&lt;sid&gt;\Software\...\Policies</c>) when a SID/mount name is
/// given so administrators keep Task Manager; without one it lands in HKLM and applies to every account.
/// USB rules are machine-wide. <c>HideTaskbar</c> is not a registry rule: the StuckRects3 blob is undocumented
/// and explorer rewrites it, so the taskbar is hidden at runtime by the Windows/TaskbarController instead.
/// <c>DisableAltTab</c> and <c>BlockedKeyCombos</c> are enforced by the low-level keyboard hook, not here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PolicyRegistry
{
    /// <summary>USB mass-storage driver service key (Start = 4 disables it).</summary>
    public const string UsbStorKey = @"SYSTEM\CurrentControlSet\Services\USBSTOR";

    /// <summary>Device installation restrictions policy key.</summary>
    public const string DeviceInstallRestrictionsKey = @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions";

    /// <summary>Setup class GUID of Human Interface Devices.</summary>
    public const string HidClassGuid = "{745a17a0-74d3-11d0-b6fe-00a0c9f57da2}";

    private const int ServiceStartManual = 3;
    private const int ServiceStartDisabled = 4;
    private readonly ILogger<PolicyRegistry> _logger;
    private readonly object _gate = new();
    private RegistrySnapshot? _last;

    /// <summary>Creates an applier.</summary>
    public PolicyRegistry(ILogger<PolicyRegistry>? logger = null) => _logger = logger ?? NullLogger<PolicyRegistry>.Instance;

    /// <summary>Pure translation of a policy into rules (no registry access).</summary>
    public static IReadOnlyList<PolicyRegistryRule> RulesFor(Policy policy, string? targetUserSid)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var rules = new List<PolicyRegistryRule>(16);
        RegistryHive hive = targetUserSid is null ? RegistryHive.LocalMachine : RegistryHive.Users;
        string root = targetUserSid is null ? "SOFTWARE" : targetUserSid + "\\Software";
        string system = root + @"\Microsoft\Windows\CurrentVersion\Policies\System";
        string explorer = root + @"\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        ExplorerPolicy e = policy.Explorer;

        void Flag(string key, string name, bool on) => rules.Add(new PolicyRegistryRule(hive, key, name, on ? 1 : 0, RegistryValueKind.DWord));

        Flag(system, "DisableTaskMgr", e.DisableTaskManager);
        Flag(system, "DisableLockWorkstation", e.DisableTaskManager);
        Flag(system, "DisableChangePassword", e.DisableTaskManager);
        Flag(explorer, "NoRun", e.DisableRun);
        Flag(explorer, "NoControlPanel", e.DisableSettings);
        Flag(explorer, "NoClose", e.DisableSettings);
        Flag(explorer, "NoLogoff", e.DisableSettings);
        Flag(explorer, "NoWinKeys", e.DisableWinKey);

        UsbPolicy usb = policy.Usb;
        rules.Add(new PolicyRegistryRule(RegistryHive.LocalMachine, UsbStorKey, "Start", usb.AllowStorage ? ServiceStartManual : ServiceStartDisabled, RegistryValueKind.DWord));
        rules.Add(new PolicyRegistryRule(RegistryHive.LocalMachine, DeviceInstallRestrictionsKey, "DenyDeviceClasses", usb.AllowHid ? 0 : 1, RegistryValueKind.DWord));
        rules.Add(new PolicyRegistryRule(RegistryHive.LocalMachine, DeviceInstallRestrictionsKey, "DenyDeviceClassesRetroactive", 0, RegistryValueKind.DWord));
        rules.Add(new PolicyRegistryRule(RegistryHive.LocalMachine, DeviceInstallRestrictionsKey + @"\DenyDeviceClasses", "1", usb.AllowHid ? null : HidClassGuid, RegistryValueKind.String));
        return rules;
    }

    /// <summary>
    /// Writes the rules for <paramref name="policy"/>. <paramref name="targetUserSid"/> is the HKEY_USERS sub-key of
    /// the kiosk user (its SID while logged on, or the mount name of a hive loaded with
    /// <see cref="RegistryHelper.LoadUserHive"/>); it must exist. The snapshot of the previous state is kept for <see cref="RevertAll"/>.
    /// </summary>
    public PolicyApplyResult Apply(Policy policy, string? targetUserSid)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (targetUserSid is not null && !RegistryHelper.Exists(RegistryHive.Users, targetUserSid))
        {
            throw new InvalidOperationException($"HKEY_USERS\\{targetUserSid} is not loaded; log the user on or load the hive first.");
        }

        IReadOnlyList<PolicyRegistryRule> rules = RulesFor(policy, targetUserSid);
        lock (_gate)
        {
            RegistrySnapshot before = RegistryHelper.Snapshot(rules.Select(r => (r.Hive, r.Key, r.Name)));
            var applied = new List<PolicyRegistryRule>(rules.Count);
            foreach (PolicyRegistryRule rule in rules)
            {
                if (rule.Value is null)
                {
                    _ = RegistryHelper.DeleteValue(rule.Hive, rule.Key, rule.Name);
                }
                else
                {
                    RegistryHelper.Set(rule.Hive, rule.Key, rule.Name, rule.Value, rule.Kind);
                }

                applied.Add(rule);
            }

            _last = _last is null ? before : Merge(_last, before);
            _logger.LogInformation("Applied {Count} registry policy rules (policy v{Version}, scope {Scope})", applied.Count, policy.Version, targetUserSid ?? "machine");
            return new PolicyApplyResult(applied, before);
        }
    }

    /// <summary>Restores the state captured in <paramref name="snapshot"/>.</summary>
    public void Revert(RegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            RegistryHelper.Apply(snapshot);
            _logger.LogInformation("Reverted {Count} registry policy values", snapshot.Values.Count);
        }
    }

    /// <summary>Restores everything this instance changed since it was created (oldest known state wins).</summary>
    public void RevertAll()
    {
        lock (_gate)
        {
            if (_last is null)
            {
                return;
            }

            RegistryHelper.Apply(_last);
            _logger.LogInformation("Reverted all {Count} registry policy values", _last.Values.Count);
            _last = null;
        }
    }

    /// <summary>Keeps the oldest capture per value so repeated applies still revert to the pre-ClubShell state.</summary>
    private static RegistrySnapshot Merge(RegistrySnapshot oldest, RegistrySnapshot newer)
    {
        var known = new HashSet<(RegistryHive, string, string)>();
        var merged = new List<RegistryValueSnapshot>(oldest.Values.Count + newer.Values.Count);
        foreach (RegistryValueSnapshot v in oldest.Values)
        {
            if (known.Add((v.Hive, v.Key.ToUpperInvariant(), v.Name.ToUpperInvariant())))
            {
                merged.Add(v);
            }
        }

        foreach (RegistryValueSnapshot v in newer.Values)
        {
            if (known.Add((v.Hive, v.Key.ToUpperInvariant(), v.Name.ToUpperInvariant())))
            {
                merged.Add(v);
            }
        }

        return new RegistrySnapshot(merged, oldest.TakenAt);
    }
}
