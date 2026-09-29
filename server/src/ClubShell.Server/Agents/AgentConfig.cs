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

    /// <summary>How long <c>adminCommand</c> waits for the agent's ack before answering <c>timeout</c> (§6.4 step 4).</summary>
    public int AckWaitSec { get; set; } = 30;
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
/// (SHELL_CHANGES п. 4), and a feature left on would drive the agent into 501s and its circuit breaker — whatever the owner
/// chose in <c>settings.features</c> (stored and shown to the console, OQ-10). <c>shell.club</c> comes from
/// <c>clubs.settings</c> (S5): <c>branding</c> (the club's name when unset), the banners live today and <c>rulesText</c>.
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
        Shell: new ShellConfigOverride(Theme: "default", Features: Features, Club: club.Club),
        Themes: []);

    /// <summary>
    /// <c>shell.club</c> of a club: <c>branding</c> (name falls back to the club's, empty URLs are left out), the live
    /// <c>banners</c> (<see cref="LiveBanners"/>; left out when none) and <c>rulesText</c> (left out when unset).
    /// </summary>
    public static ShellClub ShellClubOf(string clubName, string? settingsJson, DateOnly today)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(settingsJson) ? "{}" : settingsJson);
        var settings = doc.RootElement;
        var branding = settings.TryGetProperty("branding", out var b) && b.ValueKind == JsonValueKind.Object ? b : default;
        var banners = LiveBanners(settingsJson, today);
        ClubRules? rules = null;
        if (settings.TryGetProperty("rulesText", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            rules = new ClubRules(Text(r, "ru"), Text(r, "uz"), Text(r, "en"));
        }

        return new ShellClub(
            Name: Text(branding, "clubName") ?? clubName,
            Accent: Text(branding, "accent"),
            LogoUrl: Text(branding, "logoUrl"),
            WallpaperUrl: Text(branding, "wallpaperUrl"),
            Banners: banners.Count > 0 ? banners : null,
            Rules: rules);
    }

    /// <summary>
    /// Banners shown on <paramref name="today"/> (the club's local date), in the owner's order: enabled, with an image, and
    /// <c>from</c> ≤ today ≤ <c>to</c> where set. The <see cref="Admin.ClubTickWorker"/> hashes this set daily (OQ-21).
    /// </summary>
    public static IReadOnlyList<ClubBanner> LiveBanners(string? settingsJson, DateOnly today)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(settingsJson) ? "{}" : settingsJson);
        if (!doc.RootElement.TryGetProperty("banners", out var banners) || banners.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var live = new List<ClubBanner>();
        foreach (var banner in banners.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
        {
            var image = Text(banner, "imageUrl");
            if (!(banner.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.True) || image is null
                || (Date(banner, "from") is { } from && from > today) || (Date(banner, "to") is { } to && to < today))
            {
                continue;
            }

            live.Add(new ClubBanner(Text(banner, "id") ?? "", Text(banner, "title") ?? "", image));
        }

        return live;
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static DateOnly? Date(JsonElement parent, string name) =>
        DateOnly.TryParseExact(Text(parent, name), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
