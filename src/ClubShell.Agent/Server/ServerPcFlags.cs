using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Server;

/// <summary>
/// What the club server says about this PC beyond its status, from every heartbeat answer: so far whether the owner
/// marked it as without persistent storage (<c>pcs.stateless</c>, SHELL_CHANGES 78). Remembered on disk, so a restart of
/// the Agent knows it before the first heartbeat.
/// </summary>
public sealed class ServerPcFlags
{
    private const string FileName = "pc-stateless.txt";

    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<ServerPcFlags> _logger;
    private readonly object _gate = new();
    private bool _loaded;
    private bool? _stateless;

    /// <summary>Creates the store; the file is read on first use.</summary>
    public ServerPcFlags(IOptionsMonitor<AgentSettings> settings, ILogger<ServerPcFlags> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The PC's system disk comes back clean at every boot; <see langword="null"/> until a server has said.</summary>
    public bool? Stateless
    {
        get
        {
            lock (_gate)
            {
                Load();
                return _stateless;
            }
        }
    }

    /// <summary>A heartbeat answer: <paramref name="stateless"/> <see langword="null"/> (an older server) changes nothing.</summary>
    public void Update(bool? stateless)
    {
        if (stateless is not { } value)
        {
            return;
        }

        lock (_gate)
        {
            Load();
            if (_stateless == value)
            {
                return;
            }

            _logger.LogInformation("The server marks this PC as {Kind}", value ? "without persistent storage" : "with persistent storage");
            _stateless = value;
            try
            {
                string path = PathOf();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, value ? "true" : "false");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remember whether the PC is stateless");
            }
        }
    }

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            string path = PathOf();
            if (File.Exists(path) && bool.TryParse(File.ReadAllText(path).Trim(), out bool value))
            {
                _stateless = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Stateless flag not read");
        }
    }

    private string PathOf()
    {
        AgentSettings settings = _settings.CurrentValue;
        return Path.Combine(settings.ResolvePersistentPath(settings.Paths.Cache), FileName);
    }
}
