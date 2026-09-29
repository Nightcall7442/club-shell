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

/// <summary><c>Sessions:*</c> (DESIGN §2.5): the agent config carries grace and the offline budget; the rest is billing (§5.10).</summary>
public sealed class SessionsOptions
{
    /// <summary>Free time after <c>ends_at</c> before the tick ends a prepaid session with <c>timeUp</c> (D-12).</summary>
    public int GraceSec { get; set; } = 60;

    /// <summary>Oldest accepted offline <c>startedAt</c>; silence after which the tick ends a session of an offline PC.</summary>
    public int MaxOfflineMinutes { get; set; } = 240;

    public int TickMs { get; set; } = 1000;

    /// <summary>Every this many seconds an open session is pushed to its PC again (<c>sessionUpdated</c>).</summary>
    public int ResyncSec { get; set; } = 30;

    /// <summary>Postpaid stops when its cost reaches balance + this (tiyin, D-10); null = no limit.</summary>
    public long? PostpaidCreditLimit { get; set; } = 0;
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
