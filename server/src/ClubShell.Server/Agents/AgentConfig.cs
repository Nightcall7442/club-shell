using System.Text.Json;
using ClubShell.Contracts.Pcs;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Agents;

/// <summary><c>Agents:*</c> (DESIGN §2.5).</summary>
public sealed class AgentOptions
{
    /// <summary>→ <c>AgentServerConfig.session.heartbeatSec</c>.</summary>
    public int HeartbeatSec { get; set; } = 30;

    /// <summary>No live socket and no heartbeat for this long → <c>offline</c> (§6.6).</summary>
    public int OfflineAfterSec { get; set; } = 90;

    /// <summary><c>expiresAt</c> of every command v1 sends (§6.4).</summary>
    public int CommandTtlMin { get; set; } = 10;
}

/// <summary>The <c>Sessions:*</c> values the agent config carries (DESIGN §2.5, §5.9); S2 adds the billing ones.</summary>
public sealed class SessionsOptions
{
    public int GraceSec { get; set; } = 60;

    public int MaxOfflineMinutes { get; set; } = 240;
}

/// <summary>
/// <c>AgentServerConfig</c> of a PC (DESIGN §5.9; port of club-server <c>AgentEndpoints.BuildConfig</c>). Every feature
/// whose operations answer 501 is sent as an explicit <c>false</c>: an agent treats a missing flag as <c>true</c>
/// (SHELL_CHANGES п. 4), and a feature left on would drive the agent into 501s and its circuit breaker.
/// ponytail: <c>shell.club</c> carries only the club name; branding, banners and rules come from <c>clubs.settings</c>
/// once <c>PATCH /admin/club</c> can write them (S5).
/// </summary>
public static class AgentConfig
{
    private static readonly JsonElement Features = JsonSerializer.SerializeToElement(new Dictionary<string, bool>
    {
        ["shop"] = false,
        ["chat"] = false,
        ["booking"] = false,
        ["tournaments"] = false,
        ["profile"] = true,
        ["topup"] = false,
        ["apps"] = false,
        ["callAdmin"] = false,
    });

    private static readonly JsonElement Games = JsonSerializer.SerializeToElement(new
    {
        accountPool = new { enabled = false },
        cloudSave = new { enabled = false },
    });

    // anticheat/report is implemented (S3): violations queued while offline still reach the server.
    private static readonly JsonElement Anticheat = JsonSerializer.SerializeToElement(new { reportViolations = true });

    public static AgentServerConfig Build(PcRow pc, ClubAgentView club, AgentOptions agents, SessionsOptions sessions) => new(
        Version: club.ConfigVersion,
        PcName: pc.Name,
        Zone: pc.Zone,
        Number: pc.Number,
        Session: JsonSerializer.SerializeToElement(new { heartbeatSec = agents.HeartbeatSec, graceSec = sessions.GraceSec }),
        Offline: JsonSerializer.SerializeToElement(new { maxOfflineMinutes = sessions.MaxOfflineMinutes }),
        Games: Games,
        Updates: new UpdatesConfigOverride(Enabled: false),
        Anticheat: Anticheat,
        Shell: new ShellConfigOverride(Theme: "default", Features: Features, Club: new ShellClub(Name: club.Name)),
        Themes: []);
}
