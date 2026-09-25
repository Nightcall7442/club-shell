using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Users;

namespace ClubShell.Contracts.Pcs;

#region Pc

/// <summary>Gaming PC as known to the server (IPC_PROTOCOL.md §6.5).</summary>
/// <param name="Id">PC id.</param>
/// <param name="Name">Name, e.g. <c>PC-12</c>.</param>
/// <param name="Zone">Zone, e.g. <c>VIP</c>, <c>Standard</c>, <c>Bootcamp</c>.</param>
/// <param name="Number">Seat number.</param>
/// <param name="Hwid">Hardware id (sha256 hex); omitted for other PCs in club-wide lists.</param>
/// <param name="IpAddress">IPv4 address.</param>
/// <param name="Status">Status.</param>
/// <param name="CurrentSessionId">Open session, when any.</param>
/// <param name="AgentVersion">Agent semver.</param>
/// <param name="ShellVersion">Shell semver; <c>0.0.0</c> if unknown.</param>
/// <param name="LastHeartbeatAt">Last heartbeat received by the server.</param>
public sealed record Pc(
    Guid Id,
    string Name,
    string Zone,
    int Number,
    string? Hwid,
    string IpAddress,
    PcStatus Status,
    Guid? CurrentSessionId,
    string AgentVersion,
    string ShellVersion,
    DateTimeOffset LastHeartbeatAt);

/// <summary>Response of <c>GET /pcs</c>.</summary>
/// <param name="Items">PCs of the club (optionally filtered by zone).</param>
public sealed record PcsResponse(IReadOnlyList<Pc> Items);

/// <summary>Response of <c>sys.pcInfo</c> (IPC_PROTOCOL.md §6.21).</summary>
/// <param name="Pc">The PC.</param>
/// <param name="AgentVersion">Agent semver.</param>
/// <param name="ShellVersion">Shell semver.</param>
/// <param name="ProtocolVersion">IPC protocol major.</param>
/// <param name="UptimeSec">OS uptime.</param>
/// <param name="KioskUser">Kiosk Windows account name.</param>
/// <param name="Connectivity">Server connectivity.</param>
/// <param name="ServerTime">Agent's best estimate of server time.</param>
/// <param name="PolicyVersion">Applied policy version.</param>
public sealed record PcInfo(
    Pc Pc,
    string AgentVersion,
    string ShellVersion,
    int ProtocolVersion,
    long UptimeSec,
    string KioskUser,
    ConnectivityState Connectivity,
    DateTimeOffset ServerTime,
    int PolicyVersion);

#endregion

#region Policy

/// <summary>Semantics of <see cref="ProcessAllowlistPolicy.Patterns"/>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AllowlistMode>))]
public enum AllowlistMode
{
    /// <summary>Only matching processes may run.</summary>
    Allow,

    /// <summary>Matching processes are killed.</summary>
    Deny,
}

/// <summary>Shell replacement for the kiosk user.</summary>
/// <param name="Enabled">Replace <c>explorer.exe</c>.</param>
/// <param name="ShellExe">Path of the shell executable.</param>
public sealed record ShellReplacementPolicy(
    bool Enabled,
    string ShellExe);

/// <summary>Process allow/deny list; patterns are wildcard image names (<c>*cheat*</c>).</summary>
/// <param name="Mode">Allow-list or deny-list semantics.</param>
/// <param name="Patterns">Wildcard patterns.</param>
public sealed record ProcessAllowlistPolicy(
    AllowlistMode Mode,
    IReadOnlyList<string> Patterns);

/// <summary>USB device policy.</summary>
/// <param name="AllowStorage">Allow USB mass storage.</param>
/// <param name="AllowHid">Allow USB HID devices.</param>
public sealed record UsbPolicy(
    bool AllowStorage,
    bool AllowHid);

/// <summary>DNS-based web filter.</summary>
/// <param name="Enabled">Filter active.</param>
/// <param name="BlockedDomains">Wildcard domains to block.</param>
/// <param name="AllowedDomains">Non-empty = allow-list mode.</param>
/// <param name="DnsServers">Filtering DNS resolvers to enforce.</param>
public sealed record WebFilterPolicy(
    bool Enabled,
    IReadOnlyList<string> BlockedDomains,
    IReadOnlyList<string> AllowedDomains,
    IReadOnlyList<string> DnsServers);

/// <summary>
/// Explorer / input lockdown. Key combos use the grammar
/// <c>(Ctrl|Alt|Shift|Win)(\+(Ctrl|Alt|Shift|Win))*\+&lt;Key&gt;</c> with Win32 virtual-key names without <c>VK_</c>.
/// </summary>
/// <param name="DisableTaskManager">Disable Task Manager.</param>
/// <param name="DisableRun">Disable the Run dialog.</param>
/// <param name="DisableSettings">Disable Settings / Control Panel.</param>
/// <param name="HideTaskbar">Hide the taskbar.</param>
/// <param name="DisableAltTab">Block Alt+Tab.</param>
/// <param name="DisableWinKey">Block the Windows key.</param>
/// <param name="BlockedKeyCombos">Additional blocked combos, e.g. <c>Ctrl+Shift+Esc</c>.</param>
public sealed record ExplorerPolicy(
    bool DisableTaskManager,
    bool DisableRun,
    bool DisableSettings,
    bool HideTaskbar,
    bool DisableAltTab,
    bool DisableWinKey,
    IReadOnlyList<string> BlockedKeyCombos);

/// <summary>Power policy.</summary>
/// <param name="IdleShutdownMin">Shut down after this many idle minutes; <see langword="null"/> = never.</param>
/// <param name="ScheduledShutdown">Daily shutdown time (local <c>HH:mm</c>); <see langword="null"/> = none.</param>
public sealed record PowerPolicy(
    int? IdleShutdownMin = null,
    TimeOnly? ScheduledShutdown = null);

/// <summary>Update policy (overrides <c>agent.json → updates</c>).</summary>
/// <param name="Channel">Update channel.</param>
/// <param name="AutoInstall">Apply staged updates automatically when idle.</param>
public sealed record UpdatesPolicy(
    UpdateChannel Channel,
    bool AutoInstall);

/// <summary>Anti-cheat policy.</summary>
/// <param name="Required">Subsystems whose drivers/services must be healthy.</param>
/// <param name="BlockOnViolation">Block launches / lock session on violations.</param>
public sealed record AntiCheatPolicy(
    IReadOnlyList<AntiCheatKind> Required,
    bool BlockOnViolation);

/// <summary>Kiosk UI policy.</summary>
/// <param name="IdleTimeoutSec">Idle lock timeout.</param>
/// <param name="AdsIntervalSec">Interval between ad breaks.</param>
/// <param name="AllowVirtualKeyboard">Allow the on-screen keyboard.</param>
public sealed record KioskPolicy(
    int IdleTimeoutSec,
    int AdsIntervalSec,
    bool AllowVirtualKeyboard);

/// <summary>Enforced PC policy (IPC_PROTOCOL.md §6.18, ARCHITECTURE.md §12.3). Snapshot persisted to <c>policies.json</c>.</summary>
/// <param name="Version">Monotonic server-assigned version.</param>
/// <param name="UpdatedAt">Last change.</param>
/// <param name="ShellReplacement">Shell replacement.</param>
/// <param name="ProcessAllowlist">Process allow/deny list.</param>
/// <param name="Usb">USB policy.</param>
/// <param name="WebFilter">Web filter.</param>
/// <param name="Explorer">Explorer lockdown.</param>
/// <param name="Power">Power policy.</param>
/// <param name="Updates">Update policy.</param>
/// <param name="Anticheat">Anti-cheat policy (wire key <c>anticheat</c>).</param>
/// <param name="Kiosk">Kiosk policy.</param>
public sealed record Policy(
    int Version,
    DateTimeOffset UpdatedAt,
    ShellReplacementPolicy ShellReplacement,
    ProcessAllowlistPolicy ProcessAllowlist,
    UsbPolicy Usb,
    WebFilterPolicy WebFilter,
    ExplorerPolicy Explorer,
    PowerPolicy Power,
    UpdatesPolicy Updates,
    AntiCheatPolicy Anticheat,
    KioskPolicy Kiosk)
{
    /// <summary>Top-level policy keys, in wire order; used for <c>policy.changed.changed</c>.</summary>
    public static IReadOnlyList<string> SectionKeys { get; } = new[]
    {
        "shellReplacement", "processAllowlist", "usb", "webFilter", "explorer", "power", "updates", "anticheat", "kiosk",
    };

    /// <summary>Names of the top-level sections whose value differs between this policy and <paramref name="other"/>.</summary>
    public IReadOnlyList<string> DiffSections(Policy other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var changed = new List<string>(SectionKeys.Count);
        if (ShellReplacement != other.ShellReplacement) changed.Add("shellReplacement");
        if (!ProcessAllowlist.Mode.Equals(other.ProcessAllowlist.Mode) || !ProcessAllowlist.Patterns.SequenceEqual(other.ProcessAllowlist.Patterns, StringComparer.Ordinal)) changed.Add("processAllowlist");
        if (Usb != other.Usb) changed.Add("usb");
        if (WebFilter.Enabled != other.WebFilter.Enabled
            || !WebFilter.BlockedDomains.SequenceEqual(other.WebFilter.BlockedDomains, StringComparer.Ordinal)
            || !WebFilter.AllowedDomains.SequenceEqual(other.WebFilter.AllowedDomains, StringComparer.Ordinal)
            || !WebFilter.DnsServers.SequenceEqual(other.WebFilter.DnsServers, StringComparer.Ordinal)) changed.Add("webFilter");
        if (Explorer.DisableTaskManager != other.Explorer.DisableTaskManager
            || Explorer.DisableRun != other.Explorer.DisableRun
            || Explorer.DisableSettings != other.Explorer.DisableSettings
            || Explorer.HideTaskbar != other.Explorer.HideTaskbar
            || Explorer.DisableAltTab != other.Explorer.DisableAltTab
            || Explorer.DisableWinKey != other.Explorer.DisableWinKey
            || !Explorer.BlockedKeyCombos.SequenceEqual(other.Explorer.BlockedKeyCombos, StringComparer.Ordinal)) changed.Add("explorer");
        if (Power != other.Power) changed.Add("power");
        if (Updates != other.Updates) changed.Add("updates");
        if (Anticheat.BlockOnViolation != other.Anticheat.BlockOnViolation || !Anticheat.Required.SequenceEqual(other.Anticheat.Required)) changed.Add("anticheat");
        if (Kiosk != other.Kiosk) changed.Add("kiosk");
        return changed;
    }
}

#endregion

#region Theme

/// <summary>Theme palette; every value is <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
/// <param name="Bg">Page background.</param>
/// <param name="Surface">Card / panel background.</param>
/// <param name="Primary">Primary action colour.</param>
/// <param name="Accent">Accent colour.</param>
/// <param name="Text">Primary text.</param>
/// <param name="Muted">Secondary text.</param>
/// <param name="Danger">Errors / destructive actions.</param>
/// <param name="Success">Success state.</param>
public sealed record ThemeColors(
    string Bg,
    string Surface,
    string Primary,
    string Accent,
    string Text,
    string Muted,
    string Danger,
    string Success);

/// <summary>Shell theme file <c>themes\&lt;name&gt;.json</c> (ARCHITECTURE.md §12.4).</summary>
/// <param name="Version">Schema version (1).</param>
/// <param name="Name">Theme id; must equal the file name.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Colors">Palette.</param>
/// <param name="Radius">Corner radius in px.</param>
/// <param name="Font">Installed font family; falls back to <c>system-ui</c>.</param>
/// <param name="BackgroundVideo">Background video URL/path.</param>
/// <param name="Wallpaper">Wallpaper path (relative to ProgramData root) or absolute URL.</param>
/// <param name="Blur">Backdrop blur in px; 0 = off.</param>
/// <param name="Animations">Enable UI animations.</param>
public sealed record Theme(
    int Version,
    string Name,
    string DisplayName,
    ThemeColors Colors,
    int Radius,
    string Font,
    string? BackgroundVideo,
    string? Wallpaper,
    int Blur,
    bool Animations);

#endregion

#region Agent server config

/// <summary>Theme the Agent must download into <c>themes\</c>.</summary>
/// <param name="Name">Theme id (file name without extension).</param>
/// <param name="Url">Download URL.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file.</param>
public sealed record ThemeRef(
    string Name,
    string Url,
    string Sha256);

/// <summary>Daily local-time window (<c>HH:mm</c>); <paramref name="To"/> &lt; <paramref name="From"/> wraps midnight.</summary>
/// <param name="From">Start.</param>
/// <param name="To">End.</param>
public sealed record TimeWindow(
    TimeOnly From,
    TimeOnly To);

/// <summary>Server override of <c>agent.json → updates</c>.</summary>
/// <param name="Channel">Channel.</param>
/// <param name="CheckIntervalSec">Manifest check interval.</param>
/// <param name="ApplyWindow">Window in which non-mandatory updates may be applied.</param>
public sealed record UpdatesConfigOverride(
    UpdateChannel? Channel = null,
    int? CheckIntervalSec = null,
    TimeWindow? ApplyWindow = null);

/// <summary>A promo banner on the player's home screen, set by the club owner in the admin console.</summary>
/// <param name="Id">Banner id.</param>
/// <param name="Title">Caption.</param>
/// <param name="ImageUrl">Image URL (wide, ~16:5).</param>
public sealed record ClubBanner(string Id, string Title, string ImageUrl);

/// <summary>Club rules in each UI language; a missing language falls back to Russian.</summary>
/// <param name="Ru">Russian.</param>
/// <param name="Uz">Uzbek.</param>
/// <param name="En">English.</param>
public sealed record ClubRules(string? Ru = null, string? Uz = null, string? En = null);

/// <summary>
/// The club's own look on the player screen (<c>shell.json → club</c>), set by the owner in the admin console and
/// pushed with the server config. Every field is optional: without it the Shell keeps its bundled defaults.
/// </summary>
/// <param name="Name">Club name shown on the lock, idle and top bar.</param>
/// <param name="Accent">Accent colour <c>#RRGGBB</c>, overrides the theme's accent.</param>
/// <param name="LogoUrl">Logo image URL.</param>
/// <param name="WallpaperUrl">Wallpaper behind the lock and idle screens.</param>
/// <param name="Banners">Active banners, in display order.</param>
/// <param name="Rules">Club rules shown on the support screen.</param>
public sealed record ShellClub(
    string? Name = null,
    string? Accent = null,
    string? LogoUrl = null,
    string? WallpaperUrl = null,
    IReadOnlyList<ClubBanner>? Banners = null,
    ClubRules? Rules = null);

/// <summary>Server override pushed into <c>shell.json</c>.</summary>
/// <param name="Locale">Default locale.</param>
/// <param name="Theme">Theme id.</param>
/// <param name="Features">Partial <c>shell.json → features</c>.</param>
/// <param name="Ads">Partial <c>shell.json → ads</c>.</param>
/// <param name="Idle">Partial <c>shell.json → idle</c>.</param>
/// <param name="Club">Club branding, banners and rules; replaces <c>shell.json → club</c> as a whole.</param>
public sealed record ShellConfigOverride(
    Locale? Locale = null,
    string? Theme = null,
    JsonElement? Features = null,
    JsonElement? Ads = null,
    JsonElement? Idle = null,
    ShellClub? Club = null);

/// <summary>
/// Server-side configuration overrides (<c>GET /agents/{pcId}/config</c>, SERVER_API.md §4.1), merged over
/// <c>agent.json</c> by the Agent (server wins for keys present). Partial sections mirror the <c>agent.json</c> schema
/// and are carried as raw JSON.
/// </summary>
/// <param name="Version">Config version.</param>
/// <param name="PcName">PC name.</param>
/// <param name="Zone">Zone.</param>
/// <param name="Number">Seat number.</param>
/// <param name="Session">Partial <c>agent.json → session</c>.</param>
/// <param name="Offline">Partial <c>agent.json → offline</c>.</param>
/// <param name="Games">Partial <c>agent.json → games</c> (<c>libraryRoots</c>, <c>accountPool</c>, <c>cloudSave</c>).</param>
/// <param name="Storage">Partial <c>agent.json → storage</c>.</param>
/// <param name="Updates">Update overrides.</param>
/// <param name="Telemetry">Partial <c>agent.json → telemetry</c>.</param>
/// <param name="RemoteAdmin">Partial <c>agent.json → remoteAdmin</c>.</param>
/// <param name="Shell">Shell overrides.</param>
/// <param name="Themes">Themes to download.</param>
/// <param name="WsUrl">WebSocket URL override.</param>
public sealed record AgentServerConfig(
    int Version,
    string PcName,
    string Zone,
    int Number,
    JsonElement? Session = null,
    JsonElement? Offline = null,
    JsonElement? Games = null,
    JsonElement? Storage = null,
    UpdatesConfigOverride? Updates = null,
    JsonElement? Telemetry = null,
    JsonElement? RemoteAdmin = null,
    ShellConfigOverride? Shell = null,
    IReadOnlyList<ThemeRef>? Themes = null,
    string? WsUrl = null);

#endregion
