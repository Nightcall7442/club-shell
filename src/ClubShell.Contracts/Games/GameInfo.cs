using System.Text.Json;
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
/// <param name="SettingsPaths">
/// Where the game keeps a player's own settings (binds, sensitivity, graphics): file or directory templates with
/// <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c>, <c>%USERPROFILE%</c>, <c>{installPath}</c>. The Agent carries them from PC
/// to PC per player (<see cref="PlayerSettingsBundle"/>); <see langword="null"/> or empty = not carried.
/// </param>
/// <param name="MissingDrive">
/// Set by the Agent: the drive (<c>H:</c>) the game's catalogue path is on, which this PC does not have — the game is
/// unavailable here rather than not installed (a PC without the offline games disk, SHELL_CHANGES 74).
/// </param>
/// <param name="RegistryValues">
/// Registry values the club sets in the player's HKCU before every launch (SHELL_CHANGES 82); <see langword="null"/> or
/// empty = none.
/// </param>
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
    string? Version = null,
    IReadOnlyList<string>? SettingsPaths = null,
    string? MissingDrive = null,
    IReadOnlyList<GameRegistryValue>? RegistryValues = null);

/// <summary>
/// A value the Agent writes into the player's HKCU before every launch of the game (SHELL_CHANGES 82), after the player's
/// own settings are restored, so the club's value wins: a game that keeps its display mode in the registry (GoldSrc's
/// <c>ScreenWindowed</c>, a crash flag) starts the way the club set it however a previous run left it.
/// </summary>
/// <param name="Key">Key under <c>HKCU\Software\</c> (the <c>HKCU\</c> prefix optional); never the policy keys.</param>
/// <param name="Name">Value name.</param>
/// <param name="Type"><c>dword</c> or <c>string</c>.</param>
/// <param name="Value">A number (0..4294967295) for <c>dword</c>, a string for <c>string</c>.</param>
public sealed record GameRegistryValue(string Key, string Name, string Type, JsonElement Value);

/// <summary>Response of <c>games.installStatus</c>.</summary>
/// <param name="GameId">Game id.</param>
/// <param name="Installed">Install present and verified.</param>
/// <param name="InstallPath">Resolved install directory.</param>
/// <param name="SizeGb">Size on disk in GB.</param>
/// <param name="Version">Detected version.</param>
/// <param name="VerifiedAt">Last successful verification.</param>
/// <param name="LauncherReady">Launcher client present and logged in (where detectable).</param>
/// <param name="MissingDrive">Not installed because the drive of the game's path (<c>H:</c>) is not on this PC (SHELL_CHANGES 74).</param>
public sealed record GameInstallStatus(
    Guid GameId,
    bool Installed,
    string? InstallPath,
    double SizeGb,
    string? Version,
    DateTimeOffset? VerifiedAt,
    bool LauncherReady,
    string? MissingDrive = null);

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
