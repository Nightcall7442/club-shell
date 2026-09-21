using System.Collections.Immutable;
using System.Runtime.Versioning;

using ClubShell.Contracts.Pcs;
using ClubShell.Windows.Hooks;
using ClubShell.Windows.Registry;
using ClubShell.Windows.Windows;

using Microsoft.Win32;

using KeyComboSets = ClubShell.Windows.Hooks.BlockedKeyCombos;
using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Policy;

/// <summary>
/// Section <c>explorer</c>: Task Manager / Run / Settings / Windows-key lockdown written to the kiosk user's hive
/// through <see cref="PolicyRegistry.RulesFor"/>, taskbar hide (best effort from session 0; the Shell hides it in
/// the interactive session) and the resolved <see cref="BlockedKeyCombos"/> the Shell's low-level hook enforces
/// (Alt+Tab and the custom combos have no registry equivalent). Revert restores the pre-ClubShell values.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExplorerPolicyModule : IPolicyModule, IDisposable
{
    /// <summary>Section key.</summary>
    public const string SectionKey = "explorer";

    private readonly ILogger<ExplorerPolicyModule> _logger;
    private readonly object _gate = new();
    private RegistrySnapshot? _snapshot;
    private string? _snapshotRoot;
    private string? _sid;
    private TaskbarController? _taskbar;
    private ImmutableHashSet<KeyCombo> _blocked = ImmutableHashSet<KeyCombo>.Empty;
    private IReadOnlyList<string> _blockedNames = Array.Empty<string>();

    /// <summary>Creates the module.</summary>
    public ExplorerPolicyModule(ILogger<ExplorerPolicyModule> logger) => _logger = logger;

    /// <inheritdoc />
    public string Section => SectionKey;

    /// <summary>Combos the Shell hook must swallow (policy flags + <c>blockedKeyCombos</c>), empty before the first apply.</summary>
    public ImmutableHashSet<KeyCombo> BlockedKeyCombos => _blocked;

    /// <summary><see cref="BlockedKeyCombos"/> in the <c>Ctrl+Shift+Esc</c> wire grammar, sorted.</summary>
    public IReadOnlyList<string> BlockedKeyComboNames => _blockedNames;

    /// <inheritdoc />
    public Task<PolicyModuleResult> ApplyAsync(PcPolicy policy, PolicyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ExplorerPolicy explorer = policy.Explorer;
        var notes = new List<string>();

        ImmutableHashSet<KeyCombo> blocked = KeyComboSets.FromPolicy(explorer);
        _blocked = blocked;
        _blockedNames = blocked.Select(c => c.ToString()).Order(StringComparer.Ordinal).ToList();
        notes.Add($"{blocked.Count} key combo(s) blocked by the Shell hook (Alt+Tab={explorer.DisableAltTab}, Win={explorer.DisableWinKey})");
        notes.Add(ApplyTaskbar(explorer.HideTaskbar));

        if (context.KioskUserSid is null)
        {
            return Task.FromResult(PolicyModuleResult.Failed(
                new InvalidOperationException("Kiosk user SID unknown; explorer lockdown deferred until the kiosk account is provisioned."),
                notes.ToArray()));
        }

        lock (_gate)
        {
            _sid = context.KioskUserSid;
            RegistryPolicyOps.WithUserHive(context.KioskUserSid, root =>
            {
                List<PolicyRegistryRule> rules = PolicyRegistry.RulesFor(policy, root).Where(r => r.Hive == RegistryHive.Users).ToList();
                RegistrySnapshot before = RegistryPolicyOps.Apply(rules);
                if (_snapshot is null)
                {
                    _snapshot = before;
                    _snapshotRoot = root;
                }

                notes.Add($"{rules.Count} registry value(s) written under HKU\\{root} (taskMgr={explorer.DisableTaskManager}, run={explorer.DisableRun}, settings={explorer.DisableSettings}, winKeys={explorer.DisableWinKey})");
                return true;
            });
        }

        return Task.FromResult(PolicyModuleResult.Ok(notes.ToArray()));
    }

    /// <inheritdoc />
    public Task RevertAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _blocked = ImmutableHashSet<KeyCombo>.Empty;
            _blockedNames = Array.Empty<string>();
            _taskbar?.Dispose();
            _taskbar = null;
            if (_snapshot is { } snapshot && _snapshotRoot is { } oldRoot && _sid is { } sid)
            {
                RegistryPolicyOps.WithUserHive(sid, root =>
                {
                    RegistryHelper.Apply(RegistryPolicyOps.Rebase(snapshot, oldRoot, root));
                    return true;
                });
                _snapshot = null;
                _snapshotRoot = null;
                _logger.LogInformation("Explorer lockdown reverted for {Sid}", sid);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _taskbar?.Dispose();
            _taskbar = null;
        }

        GC.SuppressFinalize(this);
    }

    private string ApplyTaskbar(bool hide)
    {
        try
        {
            lock (_gate)
            {
                _taskbar ??= new TaskbarController(_logger);
                if (hide)
                {
                    _taskbar.Hide();
                }
                else
                {
                    _taskbar.Show();
                }
            }

            return hide ? "taskbar hidden (best effort; the Shell repeats this in the interactive session)" : "taskbar visible";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Taskbar visibility could not be changed");
            return "taskbar visibility unchanged: " + ex.Message;
        }
    }
}

/// <summary>Registry helpers shared by the registry-backed modules: user hive access, rule writes with snapshot, snapshot re-rooting.</summary>
[SupportedOSPlatform("windows")]
internal static class RegistryPolicyOps
{
    /// <summary>HKEY_USERS mount name used while the kiosk user is logged off.</summary>
    public const string KioskHiveMount = "ClubShell-Kiosk";

    /// <summary>
    /// Runs <paramref name="action"/> with the HKEY_USERS root of <paramref name="sid"/>: the SID itself while the
    /// user is logged on, otherwise the profile's NTUSER.DAT loaded temporarily under <see cref="KioskHiveMount"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The account has no profile yet (never logged on).</exception>
    public static T WithUserHive<T>(string sid, Func<string, T> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        ArgumentNullException.ThrowIfNull(action);
        if (RegistryHelper.Exists(RegistryHive.Users, sid))
        {
            return action(sid);
        }

        string profile = RegistryHelper.GetProfileImagePath(sid)
            ?? throw new InvalidOperationException($"No profile directory for {sid}; the kiosk account has not logged on yet.");
        if (RegistryHelper.Exists(RegistryHive.Users, KioskHiveMount))
        {
            RegistryHelper.UnloadUserHive(KioskHiveMount);
        }

        using LoadedHive hive = RegistryHelper.LoadUserHive(KioskHiveMount, Path.Combine(profile, "NTUSER.DAT"));
        return action(hive.MountName);
    }

    /// <summary>Writes <paramref name="rules"/> and returns the state before the write.</summary>
    public static RegistrySnapshot Apply(IReadOnlyList<PolicyRegistryRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        RegistrySnapshot before = RegistryHelper.Snapshot(rules.Select(r => (r.Hive, r.Key, r.Name)));
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
        }

        return before;
    }

    /// <summary>Re-roots HKEY_USERS keys captured under <paramref name="oldRoot"/> (SID or mount name) to <paramref name="newRoot"/>.</summary>
    public static RegistrySnapshot Rebase(RegistrySnapshot snapshot, string oldRoot, string newRoot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return snapshot;
        }

        var values = new List<RegistryValueSnapshot>(snapshot.Values.Count);
        foreach (RegistryValueSnapshot value in snapshot.Values)
        {
            values.Add(value.Hive == RegistryHive.Users && value.Key.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase)
                ? value with { Key = newRoot + value.Key[oldRoot.Length..] }
                : value);
        }

        return new RegistrySnapshot(values, snapshot.TakenAt);
    }
}
