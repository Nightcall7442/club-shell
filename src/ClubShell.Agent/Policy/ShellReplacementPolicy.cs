using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using ClubShell.Contracts.Pcs;
using ClubShell.Windows.Registry;

using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Policy;

/// <summary>Kiosk account identity as provisioned by the Agent (TempUserProvisioner); the password never leaves the process.</summary>
public interface IKioskCredentials
{
    /// <summary>Local account name (<c>club</c>).</summary>
    string UserName { get; }

    /// <summary>Current password; empty until the account is provisioned.</summary>
    string Password { get; }

    /// <summary>Account SID; empty until the account exists.</summary>
    string Sid { get; }
}

/// <summary>
/// Section <c>shellReplacement</c>: points Winlogon's per-user <c>Shell</c> value of the kiosk account at the Shell
/// executable (or removes it), through the loaded NTUSER.DAT when the user is logged off, and keeps auto-logon
/// (LSA-stored password) in sync with the rotating kiosk password. The executable must exist; its Authenticode
/// signer is logged (chain trust is not validated here).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellReplacementPolicyModule : IPolicyModule
{
    /// <summary>Section key.</summary>
    public const string SectionKey = "shellReplacement";

    private readonly ShellRegistry _shell;
    private readonly IKioskCredentials _kiosk;
    private readonly ILogger<ShellReplacementPolicyModule> _logger;
    private readonly object _gate = new();
    private string? _appliedSid;
    private bool _autoLogonSet;

    /// <summary>Creates the module.</summary>
    public ShellReplacementPolicyModule(ShellRegistry shell, IKioskCredentials kiosk, ILogger<ShellReplacementPolicyModule> logger)
    {
        _shell = shell;
        _kiosk = kiosk;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Section => SectionKey;

    /// <inheritdoc />
    public Task<PolicyModuleResult> ApplyAsync(PcPolicy policy, PolicyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ShellReplacementPolicy shell = policy.ShellReplacement;
        var notes = new List<string>();
        string? sid = context.KioskUserSid ?? (string.IsNullOrWhiteSpace(_kiosk.Sid) ? null : _kiosk.Sid);
        if (sid is null)
        {
            return Task.FromResult(PolicyModuleResult.Failed(
                new InvalidOperationException("Kiosk user SID unknown; shell replacement deferred until the kiosk account is provisioned.")));
        }

        if (!shell.Enabled)
        {
            lock (_gate)
            {
                RegistryPolicyOps.WithUserHive(sid, root =>
                {
                    _shell.RestoreExplorer(root);
                    return true;
                });
                _appliedSid = sid;
            }

            notes.Add("shell replacement disabled: explorer.exe restored for the kiosk account (auto-logon untouched)");
            return Task.FromResult(PolicyModuleResult.Ok(notes.ToArray()));
        }

        string exe = shell.ShellExe;
        if (!Path.IsPathRooted(exe) || !File.Exists(exe))
        {
            return Task.FromResult(PolicyModuleResult.Failed(new FileNotFoundException("Shell executable not found or not an absolute path.", exe)));
        }

        notes.Add(DescribeSignature(exe));
        lock (_gate)
        {
            RegistryPolicyOps.WithUserHive(sid, root =>
            {
                _shell.SetCustomShell(root, exe);
                return true;
            });
            _appliedSid = sid;
            notes.Add($"shell for {sid} set to {exe}");

            if (string.IsNullOrWhiteSpace(_kiosk.UserName) || string.IsNullOrEmpty(_kiosk.Password))
            {
                return Task.FromResult(PolicyModuleResult.Failed(
                    new InvalidOperationException("Kiosk credentials unavailable; auto-logon deferred until the kiosk account is provisioned."),
                    notes.ToArray()));
            }

            _shell.SetAutoLogon(_kiosk.UserName, _kiosk.Password);
            _autoLogonSet = true;
            notes.Add($"auto-logon enabled for {_kiosk.UserName}");
        }

        return Task.FromResult(PolicyModuleResult.Ok(notes.ToArray()));
    }

    /// <inheritdoc />
    public Task RevertAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_appliedSid is { } sid)
            {
                RegistryPolicyOps.WithUserHive(sid, root =>
                {
                    _shell.RestoreExplorer(root);
                    return true;
                });
                _appliedSid = null;
                _logger.LogInformation("Shell replacement reverted for {Sid}", sid);
            }

            if (_autoLogonSet)
            {
                _shell.ClearAutoLogon();
                _autoLogonSet = false;
            }
        }

        return Task.CompletedTask;
    }

    private string DescribeSignature(string exe)
    {
        try
        {
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(exe);
            return "shell executable Authenticode signer: " + certificate.Subject;
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Shell executable {Exe} is not Authenticode-signed", exe);
            return "shell executable is NOT Authenticode-signed";
        }
    }
}
