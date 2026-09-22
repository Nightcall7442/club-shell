using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Hardware;
using ClubShell.Windows.Network;
using ClubShell.Windows.Registry;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace ClubShell.Agent.AntiCheat;

/// <summary>
/// Platform prerequisites shared by every kernel anti-cheat (<see cref="Kind"/> is <see cref="AntiCheatKind.None"/>):
/// UEFI Secure Boot (<c>anticheat.requireSecureBoot</c>), TPM presence (<c>anticheat.requireTpm</c>), the HVCI / VBS
/// configuration (informational) and Windows test-signing mode, which is always a violation. Results are cached for
/// <see cref="CacheTtl"/>; <c>bcdedit</c> failures are tolerated as "unknown".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureBootChecker : IAntiCheatChecker, IDisposable
{
    /// <summary>DeviceGuard configuration key.</summary>
    public const string DeviceGuardKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";

    /// <summary>HVCI scenario key.</summary>
    public const string HvciKey = DeviceGuardKey + @"\Scenarios\HypervisorEnforcedCodeIntegrity";

    /// <summary>How long a result is reused.</summary>
    public static TimeSpan CacheTtl { get; } = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> YesValues = new(StringComparer.OrdinalIgnoreCase) { "yes", "on", "true", "1", "да", "ha", "oui", "ja", "sí", "si" };
    private static readonly HashSet<string> NoValues = new(StringComparer.OrdinalIgnoreCase) { "no", "off", "false", "0", "нет", "yo'q", "yoq", "non", "nein" };

    private readonly WmiQueries _wmi;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<SecureBootChecker> _logger;
    private readonly string _bcdedit = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AntiCheatCheckResult? _cached;
    private DateTimeOffset _cachedAt;
    private bool _disposed;

    /// <summary>Creates the checker.</summary>
    public SecureBootChecker(WmiQueries wmi, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<SecureBootChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _wmi = wmi;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public AntiCheatKind Kind => AntiCheatKind.None;

    /// <summary>UEFI Secure Boot state from the last check (<see langword="null"/> = legacy BIOS / unknown).</summary>
    public bool? SecureBootEnabled { get; private set; }

    /// <summary>TPM presence from the last check.</summary>
    public bool? TpmPresent { get; private set; }

    /// <summary>Virtualization-based security configured (<c>EnableVirtualizationBasedSecurity</c>).</summary>
    public bool? VbsEnabled { get; private set; }

    /// <summary>Hypervisor-enforced code integrity configured.</summary>
    public bool? HvciEnabled { get; private set; }

    /// <summary>Test-signing mode from <c>bcdedit</c> (<see langword="null"/> = could not be determined).</summary>
    public bool? TestSigningEnabled { get; private set; }

    /// <summary>Driver signature enforcement disabled from <c>bcdedit</c> (<c>nointegritychecks</c>).</summary>
    public bool? CodeIntegrityDisabled { get; private set; }

    /// <summary>Kernel debugging enabled from <c>bcdedit</c> (<c>debug</c>).</summary>
    public bool? KernelDebugEnabled { get; private set; }

    /// <summary>Hypervisor launched at boot (<c>hypervisorlaunchtype</c> other than <c>off</c>); <see langword="false"/> kills VBS and HVCI.</summary>
    public bool? HypervisorEnabled { get; private set; }

    /// <inheritdoc />
    public async Task<AntiCheatCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            if (_cached is { } cached && now - _cachedAt < CacheTtl)
            {
                return cached;
            }

            var result = await EvaluateAsync(cancellationToken).ConfigureAwait(false);
            _cached = result;
            _cachedAt = now;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public Task<AntiCheatCheckResult> CheckRuntimeAsync(int gamePid, CancellationToken cancellationToken) => CheckAsync(cancellationToken);

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

    /// <summary>Parses <c>bcdedit /enum</c> output for the <c>testsigning</c> element; <see langword="null"/> when the value is not recognisable.</summary>
    public static bool? ParseTestSigning(string bcdOutput) => ParseBcdFlag(bcdOutput, "testsigning");

    /// <summary>
    /// Parses <c>bcdedit /enum</c> output for a boolean boot element (<c>testsigning</c>, <c>nointegritychecks</c>,
    /// <c>debug</c>): <see langword="false"/> when absent (that is the element's default), <see langword="null"/> when
    /// present with a value this parser does not recognise in any of the locales <c>bcdedit</c> prints.
    /// </summary>
    public static bool? ParseBcdFlag(string bcdOutput, string element)
    {
        var value = ParseBcdElement(bcdOutput, element);
        if (value is null)
        {
            // Element absent: default is off.
            return false;
        }

        if (YesValues.Contains(value))
        {
            return true;
        }

        return NoValues.Contains(value) ? false : (bool?)null;
    }

    /// <summary>Last token of the <paramref name="element"/> line of <c>bcdedit /enum</c>, or <see langword="null"/> when the element is absent.</summary>
    public static string? ParseBcdElement(string bcdOutput, string element)
    {
        ArgumentNullException.ThrowIfNull(bcdOutput);
        ArgumentException.ThrowIfNullOrEmpty(element);
        foreach (var raw in bcdOutput.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[0], element, StringComparison.OrdinalIgnoreCase))
            {
                return parts[^1];
            }
        }

        return null;
    }

    private async Task<AntiCheatCheckResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        var anticheat = _settings.CurrentValue.Anticheat;
        SecureBootEnabled = WmiQueries.GetSecureBootEnabled();
        TpmPresent = await _wmi.GetTpmPresentAsync(cancellationToken).ConfigureAwait(false);
        VbsEnabled = ReadFlag(DeviceGuardKey, "EnableVirtualizationBasedSecurity");
        HvciEnabled = ReadFlag(HvciKey, "Enabled");
        await ReadBootConfigurationAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Platform security: secureBoot {SecureBoot}, tpm {Tpm}, vbs {Vbs}, hvci {Hvci}, hypervisor {Hypervisor}, testSigning {TestSigning}, noIntegrityChecks {CodeIntegrityOff}, kernelDebug {KernelDebug}",
            SecureBootEnabled,
            TpmPresent,
            VbsEnabled,
            HvciEnabled,
            HypervisorEnabled,
            TestSigningEnabled,
            CodeIntegrityDisabled,
            KernelDebugEnabled);

        if (anticheat.RequireSecureBoot && SecureBootEnabled != true)
        {
            _logger.LogWarning("UEFI Secure Boot is required but {State}", SecureBootEnabled is null ? "unavailable (legacy BIOS?)" : "off");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.SecureBootOff);
        }

        if (anticheat.RequireTpm && TpmPresent != true)
        {
            _logger.LogWarning("A TPM is required but {State}", TpmPresent is null ? "its state is unknown" : "absent");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.TpmOff);
        }

        if (TestSigningEnabled == true)
        {
            _logger.LogWarning("Windows test-signing mode is enabled");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.TestSigningOn);
        }

        // nointegritychecks and kernel debugging are unconditional violations for the same reason test-signing is:
        // both let unsigned drivers load, both are what an anti-cheat reads the boot configuration for.
        if (CodeIntegrityDisabled == true)
        {
            _logger.LogWarning("Driver signature enforcement is disabled (bcdedit nointegritychecks)");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.CodeIntegrityOff);
        }

        if (KernelDebugEnabled == true)
        {
            _logger.LogWarning("Kernel debugging is enabled (bcdedit debug)");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.KernelDebugOn);
        }

        bool hvci = HvciEnabled == true && HypervisorEnabled != false;
        if (!hvci)
        {
            if (anticheat.RequireHvci)
            {
                _logger.LogWarning("HVCI is required but {State}", HypervisorEnabled == false ? "the hypervisor is off in the boot configuration" : "it is not enabled");
                return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.HvciOff);
            }

            _logger.LogInformation("HVCI is not enabled (informational; {Check})", AntiCheatChecks.HvciOff);
        }

        return new AntiCheatCheckResult(Kind, true);
    }

    private static bool? ReadFlag(string key, string name)
    {
        try
        {
            if (!RegistryHelper.ValueExists(RegistryHive.LocalMachine, key, name))
            {
                return null;
            }

            return RegistryHelper.Get<int>(RegistryHive.LocalMachine, key, name) == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Reads every boot element from one <c>bcdedit /enum</c> run; all four stay <see langword="null"/> when the tool cannot be run.</summary>
    private async Task ReadBootConfigurationAsync(CancellationToken cancellationToken)
    {
        TestSigningEnabled = null;
        CodeIntegrityDisabled = null;
        KernelDebugEnabled = null;
        HypervisorEnabled = null;
        try
        {
            var result = await ProcessRunner.RunAsync(_bcdedit, new[] { "/enum", "{current}" }, ToolTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogDebug("bcdedit exited with {ExitCode}; boot configuration unknown", result.ExitCode);
                return;
            }

            TestSigningEnabled = ParseBcdFlag(result.StandardOutput, "testsigning");
            CodeIntegrityDisabled = ParseBcdFlag(result.StandardOutput, "nointegritychecks");
            KernelDebugEnabled = ParseBcdFlag(result.StandardOutput, "debug");
            // hypervisorlaunchtype is Auto/Off rather than yes/no, and an absent element means Auto (hypervisor on).
            var hypervisor = ParseBcdElement(result.StandardOutput, "hypervisorlaunchtype");
            HypervisorEnabled = hypervisor is null || !NoValues.Contains(hypervisor);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
        {
            _logger.LogDebug(ex, "bcdedit could not be run; boot configuration unknown");
        }
    }
}
