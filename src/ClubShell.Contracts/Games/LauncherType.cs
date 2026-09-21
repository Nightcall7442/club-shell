using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;

namespace ClubShell.Contracts.Games;

/// <summary>Game launcher / store client used to start a game.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<LauncherType>))]
public enum LauncherType
{
    /// <summary>Steam (<c>steam://rungameid/&lt;appid&gt;</c>).</summary>
    Steam,

    /// <summary>Epic Games Launcher.</summary>
    Epic,

    /// <summary>Battle.net.</summary>
    BattleNet,

    /// <summary>Riot Client.</summary>
    Riot,

    /// <summary>EA app.</summary>
    Ea,

    /// <summary>Ubisoft Connect.</summary>
    Ubisoft,

    /// <summary>Plain executable, no launcher.</summary>
    Exe,
}

/// <summary>Anti-cheat subsystem a game depends on.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AntiCheatKind>))]
public enum AntiCheatKind
{
    /// <summary>No anti-cheat.</summary>
    None,

    /// <summary>Easy Anti-Cheat.</summary>
    Eac,

    /// <summary>BattlEye.</summary>
    BattlEye,

    /// <summary>Riot Vanguard (kernel driver, Secure Boot / TPM prerequisites).</summary>
    Vanguard,

    /// <summary>FACEIT client.</summary>
    Faceit,

    /// <summary>Activision Ricochet.</summary>
    Ricochet,
}

/// <summary>Severity of an anti-cheat finding.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AntiCheatSeverity>))]
public enum AntiCheatSeverity
{
    /// <summary>Informational.</summary>
    Info,

    /// <summary>Soft violation; launch allowed unless policy blocks.</summary>
    Warning,

    /// <summary>Hard violation.</summary>
    Critical,
}

/// <summary>Action the Agent took in response to an anti-cheat finding.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AntiCheatAction>))]
public enum AntiCheatAction
{
    /// <summary>Reported only.</summary>
    None,

    /// <summary>Launch was refused (<c>antiCheatBlocked</c>).</summary>
    BlockedLaunch,

    /// <summary>Running game was killed.</summary>
    KilledGame,

    /// <summary>Session was locked pending admin.</summary>
    LockedSession,
}

/// <summary>Well-known values of <see cref="AntiCheatReport.Check"/> (SERVER_API.md §4.14).</summary>
public static class AntiCheatChecks
{
    /// <summary>Required anti-cheat driver is not installed.</summary>
    public const string DriverMissing = "driverMissing";

    /// <summary>Anti-cheat service is stopped.</summary>
    public const string ServiceStopped = "serviceStopped";

    /// <summary>UEFI Secure Boot is off.</summary>
    public const string SecureBootOff = "secureBootOff";

    /// <summary>TPM is absent or disabled.</summary>
    public const string TpmOff = "tpmOff";

    /// <summary>Hypervisor-enforced code integrity is off.</summary>
    public const string HvciOff = "hvciOff";

    /// <summary>Windows test-signing mode is on.</summary>
    public const string TestSigningOn = "testSigningOn";

    /// <summary>A blocked process was found running.</summary>
    public const string BlockedProcess = "blockedProcess";

    /// <summary>A foreign module was injected into a game process.</summary>
    public const string InjectedModule = "injectedModule";

    /// <summary>Virtual machine detected.</summary>
    public const string VmDetected = "vmDetected";

    /// <summary>Debugger attached to a game process.</summary>
    public const string DebuggerAttached = "debuggerAttached";
}

/// <summary>Result of the pre-launch anti-cheat check, embedded in a <see cref="LaunchReport"/>.</summary>
/// <param name="Kind">Subsystem checked.</param>
/// <param name="Ok">Whether prerequisites were satisfied.</param>
/// <param name="Reason">Failed check (<see cref="AntiCheatChecks"/>) when <paramref name="Ok"/> is <see langword="false"/>.</param>
public sealed record AntiCheatCheckResult(
    AntiCheatKind Kind,
    bool Ok,
    string? Reason = null);

/// <summary>Body of <c>POST /anticheat/report</c> and payload of the <c>anticheatViolation</c> agent event (SERVER_API.md §4.14).</summary>
/// <param name="PcId">Reporting PC.</param>
/// <param name="SessionId">Session during which the finding occurred, when any.</param>
/// <param name="UserId">Logged-in user, when any.</param>
/// <param name="GameId">Game concerned, when any.</param>
/// <param name="Kind">Subsystem.</param>
/// <param name="Check">Check identifier (<see cref="AntiCheatChecks"/>).</param>
/// <param name="Severity">Severity.</param>
/// <param name="Details">Structured evidence (process names, module paths, registry values). Never contains credentials.</param>
/// <param name="At">Time of the finding.</param>
/// <param name="ActionTaken">Action the Agent took.</param>
public sealed record AntiCheatReport(
    Guid PcId,
    Guid? SessionId,
    Guid? UserId,
    Guid? GameId,
    AntiCheatKind Kind,
    string Check,
    AntiCheatSeverity Severity,
    JsonElement Details,
    DateTimeOffset At,
    AntiCheatAction ActionTaken);
