using System.Runtime.Versioning;
using System.Security;
using ClubShell.Agent.Users;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Registry;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace ClubShell.Agent.Games;

/// <summary>
/// Writes <c>games.registryDefaults</c> into the kiosk's HKCU before a launch, value by value and only where the value is
/// missing (SHELL_CHANGES 82): the kiosk's profile is new after every reset, and a game that keeps its settings in the
/// registry (GoldSrc's display mode) would otherwise start from its own defaults rather than the club's. What the player
/// changed during the session stays until the profile is reset.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameRegistryDefaults
{
    private readonly TempUserProvisioner _provisioner;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<GameRegistryDefaults> _logger;

    /// <summary>Creates the writer.</summary>
    public GameRegistryDefaults(TempUserProvisioner provisioner, IOptionsMonitor<AgentSettings> settings, ILogger<GameRegistryDefaults> logger)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _provisioner = provisioner;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Writes the missing values; returns how many were written. Never throws for registry errors.</summary>
    public int Apply()
    {
        List<GameRegistryDefault> defaults = _settings.CurrentValue.Games.RegistryDefaults;
        if (defaults.Count == 0)
        {
            return 0;
        }

        string sid = _provisioner.Sid;
        if (string.IsNullOrEmpty(sid) || !RegistryHelper.Exists(RegistryHive.Users, sid))
        {
            _logger.LogWarning("Game registry defaults skipped: the kiosk user's hive is not loaded");
            return 0;
        }

        int written = 0;
        foreach (GameRegistryDefault entry in defaults)
        {
            if (GameRegistryDefault.NormalizeKey(entry.Key) is not { } key)
            {
                continue;
            }

            string path = sid + "\\" + key;
            try
            {
                foreach ((string name, int value) in entry.Dwords)
                {
                    written += SetIfMissing(path, name, value, RegistryValueKind.DWord);
                }

                foreach ((string name, string value) in entry.Strings)
                {
                    written += SetIfMissing(path, name, value, RegistryValueKind.String);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
            {
                _logger.LogWarning(ex, "Game registry defaults for HKCU\\{Key} not written", key);
            }
        }

        if (written > 0)
        {
            _logger.LogInformation("Wrote {Count} game registry default(s) into the kiosk's HKCU", written);
        }

        return written;
    }

    private static int SetIfMissing(string key, string name, object value, RegistryValueKind kind)
    {
        if (RegistryHelper.ValueExists(RegistryHive.Users, key, name))
        {
            return 0;
        }

        RegistryHelper.Set(RegistryHive.Users, key, name, value, kind);
        return 1;
    }
}
