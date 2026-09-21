using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;

namespace ClubShell.Contracts.Games;

/// <summary>Sort order for <c>games.list</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<GamesSort>))]
public enum GamesSort
{
    /// <summary>By server popularity rank, descending.</summary>
    Popularity,

    /// <summary>By title, ascending.</summary>
    Title,

    /// <summary>By last played (current user), most recent first.</summary>
    LastPlayed,
}

/// <summary>Minimum hardware specification of a game.</summary>
/// <param name="Cpu">CPU description.</param>
/// <param name="Gpu">GPU description.</param>
/// <param name="RamMb">RAM in MiB.</param>
public sealed record GameMinSpec(
    string Cpu,
    string Gpu,
    int RamMb);

/// <summary>Catalogue game (IPC_PROTOCOL.md §6.8). Local-only fields (<paramref name="Installed"/>, <paramref name="InstallPath"/>) are filled by the Agent's scan.</summary>
/// <param name="Id">Game id.</param>
/// <param name="Title">Localized title.</param>
/// <param name="Launcher">Launcher used to start it.</param>
/// <param name="LauncherAppId">Steam appid, Epic namespace:item, Battle.net code, etc.</param>
/// <param name="ExePath">Executable; required for <see cref="LauncherType.Exe"/>; may be relative to <paramref name="InstallPath"/>.</param>
/// <param name="Args">Extra command line.</param>
/// <param name="InstallPath">Resolved local install directory.</param>
/// <param name="Installed">Local scan result.</param>
/// <param name="Category">Categories.</param>
/// <param name="Tags">Tags.</param>
/// <param name="CoverUrl">Cover image (2:3).</param>
/// <param name="HeroUrl">Wide hero image.</param>
/// <param name="VideoUrl">Trailer / background video.</param>
/// <param name="Description">Localized description; may be empty.</param>
/// <param name="AgeRating">Minimum age; 0 = none.</param>
/// <param name="Popularity">Server rank score.</param>
/// <param name="LastPlayedAt">Last launch by the current user.</param>
/// <param name="RequiresAccount">Needs an account-pool lease.</param>
/// <param name="AntiCheat">Anti-cheat subsystem.</param>
/// <param name="MinSpec">Minimum spec.</param>
/// <param name="SizeGb">Install size in GB (1 fraction digit).</param>
/// <param name="Version">Installed/catalogue version.</param>
public sealed record Game(
    Guid Id,
    string Title,
    LauncherType Launcher,
    string? LauncherAppId,
    string? ExePath,
    string? Args,
    string? InstallPath,
    bool Installed,
    IReadOnlyList<string> Category,
    IReadOnlyList<string> Tags,
    string CoverUrl,
    string? HeroUrl,
    string? VideoUrl,
    string Description,
    int AgeRating,
    int Popularity,
    DateTimeOffset? LastPlayedAt,
    bool RequiresAccount,
    AntiCheatKind AntiCheat,
    GameMinSpec? MinSpec,
    double SizeGb,
    string? Version = null);

/// <summary>Response of <c>games.installStatus</c>.</summary>
/// <param name="GameId">Game id.</param>
/// <param name="Installed">Install present and verified.</param>
/// <param name="InstallPath">Resolved install directory.</param>
/// <param name="SizeGb">Size on disk in GB.</param>
/// <param name="Version">Detected version.</param>
/// <param name="VerifiedAt">Last successful verification.</param>
/// <param name="LauncherReady">Launcher client present and logged in (where detectable).</param>
public sealed record GameInstallStatus(
    Guid GameId,
    bool Installed,
    string? InstallPath,
    double SizeGb,
    string? Version,
    DateTimeOffset? VerifiedAt,
    bool LauncherReady);

/// <summary>Well-known values of <see cref="App.Category"/>.</summary>
public static class AppCategories
{
    /// <summary>Web browser.</summary>
    public const string Browser = "browser";

    /// <summary>Voice chat client.</summary>
    public const string Voice = "voice";

    /// <summary>Media player.</summary>
    public const string Media = "media";

    /// <summary>Utility.</summary>
    public const string Tool = "tool";
}

/// <summary>Non-game application allowed on the kiosk (IPC_PROTOCOL.md §6.10).</summary>
/// <param name="Id">App id.</param>
/// <param name="Title">Title.</param>
/// <param name="ExePath">Executable path (allow-listed by the Agent).</param>
/// <param name="IconUrl">Icon URL.</param>
/// <param name="Category">Category (<see cref="AppCategories"/>).</param>
/// <param name="Allowed">Allowed after policy evaluation.</param>
public sealed record App(
    Guid Id,
    string Title,
    string ExePath,
    string IconUrl,
    string Category,
    bool Allowed);
