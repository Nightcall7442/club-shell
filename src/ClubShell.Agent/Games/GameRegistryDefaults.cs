using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using ClubShell.Agent.Users;
using ClubShell.Contracts.Games;
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

    /// <summary>
    /// SHELL_CHANGES 82: the game's own registry values from the catalogue (<see cref="Game.RegistryValues"/>), written over
    /// whatever is there before every launch — after the player's settings are restored, so the club's value wins: a
    /// game that broke its display mode in the registry (GoldSrc's <c>ScreenWindowed</c>, a crash flag) starts the way
    /// the club set it. Keys under <c>HKCU\Software</c> only, never the policy keys; <c>dword</c> and <c>string</c>. A
    /// value that cannot be written is a warning and the launch goes on. Returns how many were written.
    /// </summary>
    public int ApplyCatalogue(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.RegistryValues is not { Count: > 0 } values)
        {
            return 0;
        }

        string sid = _provisioner.Sid;
        if (string.IsNullOrEmpty(sid) || !RegistryHelper.Exists(RegistryHive.Users, sid))
        {
            _logger.LogWarning("Registry values of {Title} skipped: the kiosk user's hive is not loaded", game.Title);
            return 0;
        }

        int written = 0;
        foreach (GameRegistryValue value in values)
        {
            if (GameRegistryDefault.NormalizeKey(value.Key) is not { } key || string.IsNullOrWhiteSpace(value.Name))
            {
                _logger.LogWarning("Registry value {Key}\\{Name} of {Title} skipped: not under HKCU\\Software or a policy key", value.Key, value.Name, game.Title);
                continue;
            }

            try
            {
                string path = sid + "\\" + key;
                switch (value.Type)
                {
                    case "dword" when value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetUInt32(out uint number):
                        RegistryHelper.Set(RegistryHive.Users, path, value.Name, unchecked((int)number), RegistryValueKind.DWord);
                        written++;
                        break;
                    case "string" when value.Value.ValueKind == JsonValueKind.String:
                        RegistryHelper.Set(RegistryHive.Users, path, value.Name, value.Value.GetString()!, RegistryValueKind.String);
                        written++;
                        break;
                    default:
                        _logger.LogWarning("Registry value {Key}\\{Name} of {Title} skipped: {Type} with a {Kind} value", key, value.Name, game.Title, value.Type, value.Value.ValueKind);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
            {
                _logger.LogWarning(ex, "Registry value HKCU\\{Key}\\{Name} of {Title} not written", key, value.Name, game.Title);
            }
        }

        if (written > 0)
        {
            _logger.LogInformation("Wrote {Count} registry value(s) of {Title} from the catalogue into the kiosk's HKCU", written, game.Title);
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
