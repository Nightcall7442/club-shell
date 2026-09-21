//! Mirror of `ClubShell.Contracts.Games` (GameInfo.cs, LauncherType.cs, LaunchRequest.cs).

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use uuid::Uuid;

use crate::error::IpcError;

// ───────────────────────────── Launchers / anti-cheat ─────────────────────────────

wire_enum! {
    /// Game launcher / store client used to start a game.
    LauncherType {
        /// Steam (`steam://rungameid/<appid>`).
        Steam = "steam",
        Epic = "epic",
        BattleNet = "battleNet",
        Riot = "riot",
        Ea = "ea",
        Ubisoft = "ubisoft",
        /// Plain executable, no launcher.
        Exe = "exe",
    }
}

wire_enum! {
    /// Anti-cheat subsystem a game depends on.
    AntiCheatKind {
        None = "none",
        /// Easy Anti-Cheat.
        Eac = "eac",
        BattlEye = "battlEye",
        /// Riot Vanguard (kernel driver, Secure Boot / TPM prerequisites).
        Vanguard = "vanguard",
        Faceit = "faceit",
        /// Activision Ricochet.
        Ricochet = "ricochet",
    }
}

wire_enum! {
    /// Severity of an anti-cheat finding.
    AntiCheatSeverity {
        Info = "info",
        /// Soft violation; launch allowed unless policy blocks.
        Warning = "warning",
        Critical = "critical",
    }
}

wire_enum! {
    /// Action the Agent took in response to an anti-cheat finding.
    AntiCheatAction {
        /// Reported only.
        None = "none",
        /// Launch was refused (`antiCheatBlocked`).
        BlockedLaunch = "blockedLaunch",
        KilledGame = "killedGame",
        /// Session was locked pending admin.
        LockedSession = "lockedSession",
    }
}

/// Well-known values of [`AntiCheatReport::check`] (SERVER_API.md §4.14).
pub mod anti_cheat_checks {
    pub const DRIVER_MISSING: &str = "driverMissing";
    pub const SERVICE_STOPPED: &str = "serviceStopped";
    pub const SECURE_BOOT_OFF: &str = "secureBootOff";
    pub const TPM_OFF: &str = "tpmOff";
    pub const HVCI_OFF: &str = "hvciOff";
    pub const TEST_SIGNING_ON: &str = "testSigningOn";
    pub const BLOCKED_PROCESS: &str = "blockedProcess";
    pub const INJECTED_MODULE: &str = "injectedModule";
    pub const VM_DETECTED: &str = "vmDetected";
    pub const DEBUGGER_ATTACHED: &str = "debuggerAttached";
}

/// Result of the pre-launch anti-cheat check, embedded in a [`LaunchReport`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AntiCheatCheckResult {
    pub kind: AntiCheatKind,
    pub ok: bool,
    /// Failed check ([`anti_cheat_checks`]) when `ok` is `false`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

/// Body of `POST /anticheat/report` and payload of the `anticheatViolation` agent event.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AntiCheatReport {
    pub pc_id: Uuid,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session_id: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub user_id: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_id: Option<Uuid>,
    pub kind: AntiCheatKind,
    /// Check identifier ([`anti_cheat_checks`]).
    pub check: String,
    pub severity: AntiCheatSeverity,
    /// Structured evidence (process names, module paths, registry values). Never credentials.
    pub details: Value,
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
    pub action_taken: AntiCheatAction,
}

// ───────────────────────────── Catalogue ─────────────────────────────

wire_enum! {
    /// Sort order for `games.list`.
    GamesSort {
        /// By server popularity rank, descending.
        Popularity = "popularity",
        /// By title, ascending.
        Title = "title",
        /// By last played (current user), most recent first.
        LastPlayed = "lastPlayed",
    }
}

/// Minimum hardware specification of a game.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GameMinSpec {
    pub cpu: String,
    pub gpu: String,
    pub ram_mb: i32,
}

/// Catalogue game (IPC_PROTOCOL.md §6.8). Local-only fields (`installed`, `install_path`) are filled
/// by the Agent's scan.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Game {
    pub id: Uuid,
    pub title: String,
    pub launcher: LauncherType,
    /// Steam appid, Epic namespace:item, Battle.net code, etc.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub launcher_app_id: Option<String>,
    /// Required for `exe`; may be relative to `install_path`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exe_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub args: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub install_path: Option<String>,
    pub installed: bool,
    pub category: Vec<String>,
    pub tags: Vec<String>,
    pub cover_url: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hero_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub video_url: Option<String>,
    /// Localized description; may be empty.
    pub description: String,
    /// Minimum age; 0 = none.
    pub age_rating: i32,
    pub popularity: i32,
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub last_played_at: Option<DateTime<Utc>>,
    pub requires_account: bool,
    pub anti_cheat: AntiCheatKind,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub min_spec: Option<GameMinSpec>,
    /// Install size in GB (1 fraction digit).
    #[serde(with = "crate::wire::num")]
    pub size_gb: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
}

/// Response of `games.installStatus`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GameInstallStatus {
    pub game_id: Uuid,
    pub installed: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub install_path: Option<String>,
    #[serde(with = "crate::wire::num")]
    pub size_gb: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub verified_at: Option<DateTime<Utc>>,
    /// Launcher client present and logged in (where detectable).
    pub launcher_ready: bool,
}

/// Well-known values of [`App::category`].
pub mod app_categories {
    pub const BROWSER: &str = "browser";
    pub const VOICE: &str = "voice";
    pub const MEDIA: &str = "media";
    pub const TOOL: &str = "tool";
}

/// Non-game application allowed on the kiosk (IPC_PROTOCOL.md §6.10).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct App {
    pub id: Uuid,
    pub title: String,
    pub exe_path: String,
    pub icon_url: String,
    /// Category ([`app_categories`]).
    pub category: String,
    /// Allowed after policy evaluation.
    pub allowed: bool,
}

// ───────────────────────────── Launch ─────────────────────────────

wire_enum! {
    /// Lifecycle state of a launched game process.
    GameState {
        /// Launcher invoked; waiting for the game window/process.
        Launching = "launching",
        Running = "running",
        Exited = "exited",
        Failed = "failed",
        /// Killed by the Agent (session end, admin, policy).
        Killed = "killed",
    }
}

// ---- BEGIN MANUAL ----
impl GameState {
    /// `true` for terminal states (`exited`, `failed`, `killed`).
    pub const fn is_terminal(self) -> bool {
        matches!(self, GameState::Exited | GameState::Failed | GameState::Killed)
    }
}
// ---- END MANUAL ----

/// Screen resolution requested for a launch.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[serde(rename_all = "camelCase")]
pub struct Resolution {
    pub width: i32,
    pub height: i32,
}

/// Full launch request as executed by the Agent (IPC_PROTOCOL.md §6.9); also the payload of the
/// `launchGame` server command.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LaunchRequest {
    pub game_id: Uuid,
    pub session_id: Uuid,
    pub user_id: Uuid,
    /// Lease a pooled account and inject credentials.
    pub use_account_pool: bool,
    /// Reuse an existing lease.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub account_lease_id: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub extra_args: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub resolution: Option<Resolution>,
    /// Seconds to wait for the game process/window before failing.
    pub launch_timeout_sec: i32,
}

/// Outcome of a launch (IPC_PROTOCOL.md §6.9). Over IPC failures are returned as errors, so `ok` is
/// always `true` there.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LaunchResult {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pid: Option<i32>,
    #[serde(with = "crate::wire::ts")]
    pub started_at: DateTime<Utc>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub account_lease_id: Option<Uuid>,
    /// Failure details when `ok` is `false`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<IpcError>,
}

// ---- BEGIN MANUAL ----
impl LaunchResult {
    /// Successful result.
    pub fn success(pid: i32, started_at: DateTime<Utc>, account_lease_id: Option<Uuid>) -> Self {
        Self { ok: true, pid: Some(pid), started_at, account_lease_id, error: None }
    }

    /// Failed result.
    pub fn failure(error: IpcError, started_at: DateTime<Utc>, account_lease_id: Option<Uuid>) -> Self {
        Self { ok: false, pid: None, started_at, account_lease_id, error: Some(error) }
    }
}
// ---- END MANUAL ----

/// Game process tracked by the Agent (IPC_PROTOCOL.md §6.9).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct RunningGame {
    pub game_id: Uuid,
    pub title: String,
    pub pid: i32,
    #[serde(with = "crate::wire::ts")]
    pub started_at: DateTime<Utc>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub account_lease_id: Option<Uuid>,
    pub state: GameState,
}

/// Pre-signed download of a user's cloud-save bundle.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CloudSaveDownload {
    pub url: String,
    /// Lower-case hex SHA-256 of the bundle.
    pub sha256: String,
    pub size_bytes: i64,
}

/// Account-pool lease (`GET /games/{id}/accounts/lease`, SERVER_API.md §4.6). `secret` is AES-256-GCM
/// encrypted with a per-agent key derived from the signing secret (HKDF-SHA256, info `account-pool`),
/// base64 `nonce||ciphertext||tag`. Never logged.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AccountLease {
    pub lease_id: Uuid,
    pub launcher: LauncherType,
    pub username: String,
    /// Encrypted password or token.
    pub secret: String,
    /// Launcher-specific extras (`steamGuardSecret`, `authenticatorSeed`, `region`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub extra: Option<Value>,
    #[serde(with = "crate::wire::ts")]
    pub expires_at: DateTime<Utc>,
    /// Save bundle to restore before launch, when any.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cloud_save: Option<CloudSaveDownload>,
}

wire_enum! {
    /// Why an account-pool lease is released.
    AccountLeaseReleaseReason {
        Exit = "exit",
        SessionEnd = "sessionEnd",
        LaunchFailed = "launchFailed",
        /// Manual / admin release.
        Manual = "manual",
    }
}

/// Cloud-save bundle the Agent uploaded before releasing a lease.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CloudSaveUpload {
    /// Pre-signed URL the bundle was `PUT` to (from [`SaveUploadTarget`]).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub upload_url: Option<String>,
    pub sha256: String,
    pub size_bytes: i64,
}

/// Body of `POST /games/{id}/accounts/{leaseId}/release`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AccountLeaseRelease {
    pub reason: AccountLeaseReleaseReason,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cloud_save: Option<CloudSaveUpload>,
}

/// Response of `GET /games/{id}/accounts/{leaseId}/save-upload`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SaveUploadTarget {
    pub upload_url: String,
    #[serde(with = "crate::wire::ts")]
    pub expires_at: DateTime<Utc>,
    pub max_bytes: i64,
}

wire_enum! {
    /// Which lifecycle point a [`LaunchReport`] describes.
    LaunchReportPhase {
        /// Reported right after launch (success or failure).
        Launch = "launch",
        /// Reported when the game exits.
        Exit = "exit",
    }
}

/// Body of `POST /games/{id}/launch-report` (SERVER_API.md §4.6).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LaunchReport {
    pub session_id: Uuid,
    pub user_id: Uuid,
    pub result: LaunchResult,
    /// Launch latency in milliseconds.
    pub duration_ms: i32,
    pub launcher: LauncherType,
    pub anti_cheat: AntiCheatCheckResult,
    pub phase: LaunchReportPhase,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exit_code: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub played_sec: Option<i32>,
}

// ---- BEGIN MANUAL ----
#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::assert_wire;
    use chrono::TimeZone;

    #[test]
    fn enum_wire_values() {
        assert_wire(LauncherType::ALL, &["steam", "epic", "battleNet", "riot", "ea", "ubisoft", "exe"]);
        assert_wire(AntiCheatKind::ALL, &["none", "eac", "battlEye", "vanguard", "faceit", "ricochet"]);
        assert_wire(AntiCheatSeverity::ALL, &["info", "warning", "critical"]);
        assert_wire(AntiCheatAction::ALL, &["none", "blockedLaunch", "killedGame", "lockedSession"]);
        assert_wire(GamesSort::ALL, &["popularity", "title", "lastPlayed"]);
        assert_wire(GameState::ALL, &["launching", "running", "exited", "failed", "killed"]);
        assert_wire(AccountLeaseReleaseReason::ALL, &["exit", "sessionEnd", "launchFailed", "manual"]);
        assert_wire(LaunchReportPhase::ALL, &["launch", "exit"]);
        assert!(GameState::Killed.is_terminal());
        assert!(!GameState::Launching.is_terminal());
    }

    #[test]
    fn game_json() {
        let g = Game {
            id: Uuid::nil(),
            title: "CS2".into(),
            launcher: LauncherType::Steam,
            launcher_app_id: Some("730".into()),
            exe_path: None,
            args: None,
            install_path: Some(r"D:\Games\cs2".into()),
            installed: true,
            category: vec!["fps".into()],
            tags: vec![],
            cover_url: "https://c".into(),
            hero_url: None,
            video_url: None,
            description: String::new(),
            age_rating: 16,
            popularity: 100,
            last_played_at: None,
            requires_account: true,
            anti_cheat: AntiCheatKind::None,
            min_spec: Some(GameMinSpec { cpu: "i5".into(), gpu: "GTX 1060".into(), ram_mb: 8192 }),
            size_gb: 35.5,
            version: None,
        };
        let json = serde_json::to_string(&g).unwrap();
        assert_eq!(
            json,
            r#"{"id":"00000000-0000-0000-0000-000000000000","title":"CS2","launcher":"steam","launcherAppId":"730","installPath":"D:\\Games\\cs2","installed":true,"category":["fps"],"tags":[],"coverUrl":"https://c","description":"","ageRating":16,"popularity":100,"requiresAccount":true,"antiCheat":"none","minSpec":{"cpu":"i5","gpu":"GTX 1060","ramMb":8192},"sizeGb":35.5}"#
        );
        assert_eq!(serde_json::from_str::<Game>(&json).unwrap(), g);
        // Explicit nulls from the server are accepted.
        let with_nulls = json.replace(r#""launcherAppId":"730","#, r#""launcherAppId":null,"exePath":null,"lastPlayedAt":null,"#);
        let back: Game = serde_json::from_str(&with_nulls).unwrap();
        assert_eq!(back.launcher_app_id, None);
    }

    #[test]
    fn launch_types_json() {
        let at = Utc.with_ymd_and_hms(2026, 9, 21, 10, 21, 0).unwrap();
        let ok = LaunchResult::success(7788, at, None);
        assert_eq!(serde_json::to_string(&ok).unwrap(), r#"{"ok":true,"pid":7788,"startedAt":"2026-09-21T10:21:00.000Z"}"#);
        let failed = LaunchResult::failure(IpcError::account_pool_exhausted(), at, None);
        assert_eq!(
            serde_json::to_string(&failed).unwrap(),
            r#"{"ok":false,"startedAt":"2026-09-21T10:21:00.000Z","error":{"code":"accountPoolExhausted","message":"No free pooled account","details":null}}"#
        );
        assert_eq!(serde_json::from_str::<LaunchResult>(&serde_json::to_string(&failed).unwrap()).unwrap(), failed);

        let req = LaunchRequest {
            game_id: Uuid::nil(),
            session_id: Uuid::nil(),
            user_id: Uuid::nil(),
            use_account_pool: true,
            account_lease_id: None,
            extra_args: None,
            resolution: Some(Resolution { width: 1920, height: 1080 }),
            launch_timeout_sec: 90,
        };
        assert_eq!(
            serde_json::to_string(&req).unwrap(),
            r#"{"gameId":"00000000-0000-0000-0000-000000000000","sessionId":"00000000-0000-0000-0000-000000000000","userId":"00000000-0000-0000-0000-000000000000","useAccountPool":true,"resolution":{"width":1920,"height":1080},"launchTimeoutSec":90}"#
        );
        let running = RunningGame { game_id: Uuid::nil(), title: "CS2".into(), pid: 1, started_at: at, account_lease_id: None, state: GameState::Running };
        assert_eq!(
            serde_json::to_string(&running).unwrap(),
            r#"{"gameId":"00000000-0000-0000-0000-000000000000","title":"CS2","pid":1,"startedAt":"2026-09-21T10:21:00.000Z","state":"running"}"#
        );
        let report = LaunchReport {
            session_id: Uuid::nil(),
            user_id: Uuid::nil(),
            result: ok,
            duration_ms: 1500,
            launcher: LauncherType::Steam,
            anti_cheat: AntiCheatCheckResult { kind: AntiCheatKind::Vanguard, ok: false, reason: Some(anti_cheat_checks::SECURE_BOOT_OFF.into()) },
            phase: LaunchReportPhase::Exit,
            exit_code: Some(0),
            played_sec: Some(3600),
        };
        let json = serde_json::to_string(&report).unwrap();
        assert!(json.ends_with(r#""durationMs":1500,"launcher":"steam","antiCheat":{"kind":"vanguard","ok":false,"reason":"secureBootOff"},"phase":"exit","exitCode":0,"playedSec":3600}"#));
        assert_eq!(serde_json::from_str::<LaunchReport>(&json).unwrap(), report);
    }

    #[test]
    fn lease_and_report_json() {
        let at = Utc.with_ymd_and_hms(2026, 9, 21, 14, 0, 0).unwrap();
        let lease = AccountLease {
            lease_id: Uuid::nil(),
            launcher: LauncherType::Steam,
            username: "pool01".into(),
            secret: "enc".into(),
            extra: Some(serde_json::json!({ "region": "eu" })),
            expires_at: at,
            cloud_save: None,
        };
        let json = serde_json::to_string(&lease).unwrap();
        assert_eq!(
            json,
            r#"{"leaseId":"00000000-0000-0000-0000-000000000000","launcher":"steam","username":"pool01","secret":"enc","extra":{"region":"eu"},"expiresAt":"2026-09-21T14:00:00.000Z"}"#
        );
        assert_eq!(serde_json::from_str::<AccountLease>(&json).unwrap(), lease);
        let release = AccountLeaseRelease { reason: AccountLeaseReleaseReason::SessionEnd, cloud_save: Some(CloudSaveUpload { upload_url: None, sha256: "ab".into(), size_bytes: 10 }) };
        assert_eq!(serde_json::to_string(&release).unwrap(), r#"{"reason":"sessionEnd","cloudSave":{"sha256":"ab","sizeBytes":10}}"#);
        let report = AntiCheatReport {
            pc_id: Uuid::nil(),
            session_id: None,
            user_id: None,
            game_id: None,
            kind: AntiCheatKind::Eac,
            check: anti_cheat_checks::DRIVER_MISSING.into(),
            severity: AntiCheatSeverity::Critical,
            details: serde_json::json!({}),
            at,
            action_taken: AntiCheatAction::BlockedLaunch,
        };
        assert_eq!(
            serde_json::to_string(&report).unwrap(),
            r#"{"pcId":"00000000-0000-0000-0000-000000000000","kind":"eac","check":"driverMissing","severity":"critical","details":{},"at":"2026-09-21T14:00:00.000Z","actionTaken":"blockedLaunch"}"#
        );
    }
}
// ---- END MANUAL ----
