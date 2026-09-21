//! Shell ↔ Agent client. [`AgentClient`] is either a real named-pipe transport supervised by a
//! reconnect loop ([`pipe_client`], [`reconnect`]) or an in-process [`MockTransport`] that answers
//! every IPC name with canned data so the UI runs without the service (`CLUBSHELL_DEV=1`, or a
//! debug build whose pipe is unreachable at startup). [`events`] fans Agent events out to the webview.

pub mod events;
pub mod pipe_client;
pub mod reconnect;

use std::sync::Arc;
use std::time::Duration;

use chrono::{DateTime, Duration as ChronoDuration, Utc};
use clubshell_protocol::commands::{agent_capabilities, names, AgentCommand, AuthHelloResponse, IpcAuthLevel};
use clubshell_protocol::error::ErrorCode;
use clubshell_protocol::ipc::IpcEnvelope;
use clubshell_protocol::wire::format_ts;
use clubshell_protocol::PROTOCOL_VERSION;
use clubshell_winutil::pipe::PipeClient;
use parking_lot::Mutex;
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tokio::sync::{broadcast, watch};
use uuid::Uuid;

pub use events::EventForwarder;
pub use pipe_client::{MetricsSnapshot, PipeTransport};
pub use reconnect::{StateCallback, Supervisor};

use crate::config::ShellConfig;
use crate::state::{CmdResult, ShellError};

/// How long a request waits for the connection to reach `Connected` (hello done) before failing
/// with `agentOffline`. Covers a reconnect + hello without freezing the UI for the full request
/// timeout while the Agent is down.
pub const HELLO_WAIT: Duration = Duration::from_secs(3);

/// Shell ↔ Agent pipe lifecycle (`kiosk://connectivity.agent`).
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ConnectionState {
    /// Opening the pipe or waiting for the `auth.hello` response.
    Connecting,
    /// Hello succeeded; requests flow.
    Connected,
    /// Pipe lost (or never opened); the supervisor is backing off, or the client was shut down.
    Disconnected,
}

/// Payload of `kiosk://connectivity` (`TAURI_COMMANDS.md` §3.2) and value of [`AgentClient::state`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ConnectivityStatus {
    pub agent: ConnectionState,
    /// Consecutive failed connect/hello attempts in the current outage (0 while connected).
    pub attempts: u32,
    /// When `agent` was entered.
    #[serde(with = "clubshell_protocol::wire::ts")]
    pub since: DateTime<Utc>,
}

impl ConnectivityStatus {
    pub fn new(agent: ConnectionState, attempts: u32) -> Self {
        Self { agent, attempts, since: Utc::now() }
    }

    pub fn is_connected(&self) -> bool {
        self.agent == ConnectionState::Connected
    }
}

/// Real transport plus its supervisor handles.
pub struct PipeAgent {
    transport: Arc<PipeTransport>,
    config: Arc<ShellConfig>,
    shutdown: watch::Sender<bool>,
    /// Connection opened by the startup probe, handed to the supervisor as its first connection.
    initial: Mutex<Option<PipeClient>>,
    task: Mutex<Option<tauri::async_runtime::JoinHandle<()>>>,
}

impl PipeAgent {
    fn new(config: &ShellConfig) -> Self {
        let (shutdown, _) = watch::channel(false);
        Self {
            transport: Arc::new(PipeTransport::new(config)),
            config: Arc::new(config.clone()),
            shutdown,
            initial: Mutex::new(None),
            task: Mutex::new(None),
        }
    }

    pub fn transport(&self) -> &Arc<PipeTransport> {
        &self.transport
    }
}

/// The Agent as seen by commands and background tasks. Cheap to share behind an `Arc`.
#[allow(clippy::large_enum_variant)]
pub enum AgentClient {
    Pipe(PipeAgent),
    Mock(MockTransport),
}

impl AgentClient {
    /// Pipe transport, not yet connected; call [`start`](Self::start) to run the supervisor.
    pub fn pipe(config: &ShellConfig) -> Arc<Self> {
        Arc::new(Self::Pipe(PipeAgent::new(config)))
    }

    /// Mock transport (always connected, canned responses).
    pub fn mock(config: &ShellConfig) -> Arc<Self> {
        Arc::new(Self::Mock(MockTransport::new(config)))
    }

    /// Mock when `CLUBSHELL_DEV=1`; otherwise the pipe transport. In debug builds the pipe is
    /// probed once (`ipc.connectTimeoutMs`) and an unreachable pipe also falls back to the mock so
    /// `tauri dev` works without the service. Never fails; release builds reconnect forever.
    pub async fn connect(config: &ShellConfig) -> Arc<Self> {
        if config.is_dev() {
            tracing::info!("CLUBSHELL_DEV set: using mock agent");
            return Self::mock(config);
        }
        let agent = PipeAgent::new(config);
        if cfg!(debug_assertions) {
            match agent.transport.connect().await {
                Ok(client) => *agent.initial.lock() = Some(client),
                Err(e) => {
                    tracing::warn!(error = %e, pipe = %config.ipc.pipe_name, "pipe unreachable in debug build: using mock agent");
                    return Self::mock(config);
                }
            }
        }
        Arc::new(Self::Pipe(agent))
    }

    /// Spawns the reconnect supervisor (pipe only; idempotent). `on_state` is invoked on every
    /// [`ConnectivityStatus`] change in addition to the [`state`](Self::state) watch.
    pub fn start(self: &Arc<Self>, on_state: Option<StateCallback>) {
        let Self::Pipe(agent) = &**self else { return };
        let mut task = agent.task.lock();
        if task.is_some() {
            return;
        }
        let mut supervisor = Supervisor::new(Arc::clone(&agent.transport), Arc::clone(&agent.config), agent.shutdown.subscribe());
        if let Some(initial) = agent.initial.lock().take() {
            supervisor = supervisor.with_initial(initial);
        }
        if let Some(cb) = on_state {
            supervisor = supervisor.on_state(cb);
        }
        *task = Some(supervisor.spawn());
    }

    /// Stops the supervisor and closes the pipe; pending requests fail with `agentOffline`.
    pub fn shutdown(&self) {
        if let Self::Pipe(agent) = self {
            let _ = agent.shutdown.send_replace(true);
            agent.transport.detach();
            agent.transport.publish(ConnectionState::Disconnected, 0);
        }
    }

    pub fn is_mock(&self) -> bool {
        matches!(self, Self::Mock(_))
    }

    /// `shell.json → ipc.requestTimeoutMs`.
    pub fn request_timeout(&self) -> Duration {
        match self {
            Self::Pipe(agent) => agent.config.ipc.request_timeout(),
            Self::Mock(mock) => mock.request_timeout,
        }
    }

    /// Agent → Shell events (`kind = event`), across reconnects.
    pub fn events(&self) -> broadcast::Receiver<IpcEnvelope> {
        match self {
            Self::Pipe(agent) => agent.transport.subscribe(),
            Self::Mock(mock) => mock.events.subscribe(),
        }
    }

    /// Pipe state watch (`Connecting` → `Connected` → `Disconnected` → …).
    pub fn state(&self) -> watch::Receiver<ConnectivityStatus> {
        match self {
            Self::Pipe(agent) => agent.transport.state(),
            Self::Mock(mock) => mock.state.subscribe(),
        }
    }

    pub fn current_state(&self) -> ConnectivityStatus {
        self.state().borrow().clone()
    }

    pub fn is_connected(&self) -> bool {
        self.current_state().is_connected()
    }

    /// `auth.hello` response of the current connection (PC identity, Agent capabilities).
    pub fn pc_info(&self) -> Option<AuthHelloResponse> {
        match self {
            Self::Pipe(agent) => agent.transport.pc_info(),
            Self::Mock(mock) => Some(mock.hello.clone()),
        }
    }

    /// Typed request → typed response with the default timeout. A `()` payload sends `null`.
    pub async fn request<Req, Res>(&self, name: &str, payload: &Req) -> CmdResult<Res>
    where
        Req: Serialize + ?Sized,
        Res: DeserializeOwned,
    {
        self.request_with_timeout(name, payload, self.request_timeout()).await
    }

    /// [`request`](Self::request) with an explicit timeout (`games.launch` uses 120 s).
    pub async fn request_with_timeout<Req, Res>(&self, name: &str, payload: &Req, timeout: Duration) -> CmdResult<Res>
    where
        Req: Serialize + ?Sized,
        Res: DeserializeOwned,
    {
        match self.request_raw_timeout(name, to_payload(payload)?, timeout).await? {
            Some(value) => decode(name, value),
            None => Err(ShellError::protocol(format!("{name}: response payload missing"))),
        }
    }

    /// Like [`request`](Self::request) for responses whose payload may be `null` (`session.get`).
    pub async fn request_optional<Req, Res>(&self, name: &str, payload: &Req) -> CmdResult<Option<Res>>
    where
        Req: Serialize + ?Sized,
        Res: DeserializeOwned,
    {
        match self.request_raw_timeout(name, to_payload(payload)?, self.request_timeout()).await? {
            Some(value) => decode(name, value).map(Some),
            None => Ok(None),
        }
    }

    /// Raw request with the default timeout; `Ok(None)` for a `null` response payload.
    pub async fn request_raw(&self, name: &str, payload: Option<Value>) -> CmdResult<Option<Value>> {
        self.request_raw_timeout(name, payload, self.request_timeout()).await
    }

    /// Raw request with an explicit timeout. Error envelopes become `ShellError { source: ipc }`.
    pub async fn request_raw_timeout(&self, name: &str, payload: Option<Value>, timeout: Duration) -> CmdResult<Option<Value>> {
        self.send(IpcEnvelope::request(name, payload), timeout).await
    }

    /// Sends a prepared request envelope (custom `id`, e.g. the frontend `traceId`).
    pub async fn send(&self, env: IpcEnvelope, timeout: Duration) -> CmdResult<Option<Value>> {
        match self {
            Self::Pipe(agent) => agent.transport.send(env, timeout).await,
            Self::Mock(mock) => mock.request(&env.name, env.payload),
        }
    }
}

fn to_payload<Req: Serialize + ?Sized>(payload: &Req) -> CmdResult<Option<Value>> {
    let value = serde_json::to_value(payload)?;
    Ok(if value.is_null() { None } else { Some(value) })
}

fn decode<Res: DeserializeOwned>(name: &str, value: Value) -> CmdResult<Res> {
    serde_json::from_value(value).map_err(|e| ShellError::protocol(format!("{name}: invalid response payload: {e}")))
}

// ───────────────────────────── Mock ─────────────────────────────

/// In-process Agent stand-in: answers every IPC name with canned data, keeps a tiny amount of state
/// (logged-in user, session, settings, running game) and emits the matching events.
pub struct MockTransport {
    events: broadcast::Sender<IpcEnvelope>,
    state: watch::Sender<ConnectivityStatus>,
    hello: AuthHelloResponse,
    request_timeout: Duration,
    db: Mutex<MockDb>,
}

struct MockDb {
    user: Option<Value>,
    session: Option<Value>,
    settings: Value,
    running_game: Option<Value>,
}

const MOCK_PC: Uuid = Uuid::from_u128(0x01);
const MOCK_USER: Uuid = Uuid::from_u128(0x11);
const MOCK_TARIFF_STD: Uuid = Uuid::from_u128(0x21);
const MOCK_TARIFF_VIP: Uuid = Uuid::from_u128(0x22);
const MOCK_GAME_CS2: Uuid = Uuid::from_u128(0x31);
const MOCK_GAME_DOTA: Uuid = Uuid::from_u128(0x32);
const MOCK_APP: Uuid = Uuid::from_u128(0x41);
const MOCK_ADMIN_PIN: &str = "0000";
const MOCK_GAME_PID: i64 = 4242;

fn money(amount: i64) -> Value {
    json!({ "amount": amount, "currency": "UZS" })
}

fn ts(at: DateTime<Utc>) -> Value {
    Value::String(format_ts(&at))
}

fn now() -> Value {
    ts(Utc::now())
}

fn in_secs(secs: i64) -> Value {
    ts(Utc::now() + ChronoDuration::seconds(secs))
}

fn mock_err(code: ErrorCode, message: &str) -> ShellError {
    ShellError::mock(code, message)
}

fn mock_user() -> Value {
    json!({
        "id": MOCK_USER, "username": "demo", "displayName": "Demo Player", "role": "member",
        "balance": money(50_000), "loyaltyLevel": 1, "loyaltyPoints": 120,
        "createdAt": "2026-01-01T00:00:00.000Z", "locale": "ru", "flags": []
    })
}

fn mock_tariffs() -> Value {
    json!([
        { "id": MOCK_TARIFF_STD, "name": "Standard", "pricePerHour": money(15_000), "minMinutes": 30,
          "zones": ["main"], "timeWindows": [], "isPackage": false },
        { "id": MOCK_TARIFF_VIP, "name": "VIP", "pricePerHour": money(25_000), "minMinutes": 60, "maxMinutes": 600,
          "zones": ["vip"], "timeWindows": [], "isPackage": false }
    ])
}

fn mock_game(id: Uuid, title: &str, app_id: &str, tag: &str) -> Value {
    json!({
        "id": id, "title": title, "launcher": "steam", "launcherAppId": app_id, "installed": true,
        "installPath": format!("C:\\Games\\{app_id}"), "category": [tag], "tags": [tag], "coverUrl": "",
        "description": format!("{title} (mock)"), "ageRating": 16, "popularity": 100, "requiresAccount": true,
        "antiCheat": "none", "sizeGb": 35, "version": "1.0"
    })
}

fn mock_games() -> Vec<Value> {
    vec![mock_game(MOCK_GAME_CS2, "Counter-Strike 2", "730", "shooter"), mock_game(MOCK_GAME_DOTA, "Dota 2", "570", "moba")]
}

fn mock_pc(shell_version: &str) -> Value {
    json!({
        "id": MOCK_PC, "name": "PC-01", "zone": "main", "number": 1, "ipAddress": "127.0.0.1", "status": "free",
        "agentVersion": "mock", "shellVersion": shell_version, "lastHeartbeatAt": now()
    })
}

fn mock_policy() -> Value {
    json!({
        "version": 1, "updatedAt": "2026-01-01T00:00:00.000Z",
        "shellReplacement": { "enabled": true, "shellExe": "C:\\Program Files\\ClubShell\\Shell\\clubshell-shell.exe" },
        "processAllowlist": { "mode": "deny", "patterns": [] },
        "usb": { "allowStorage": false, "allowHid": true },
        "webFilter": { "enabled": false, "blockedDomains": [], "allowedDomains": [], "dnsServers": [] },
        "explorer": { "disableTaskManager": true, "disableRun": true, "disableSettings": true, "hideTaskbar": true,
                      "disableAltTab": true, "disableWinKey": true, "blockedKeyCombos": [] },
        "power": {},
        "updates": { "channel": "stable", "autoInstall": true },
        "anticheat": { "required": [], "blockOnViolation": true },
        "kiosk": { "idleTimeoutSec": 300, "adsIntervalSec": 900, "allowVirtualKeyboard": true }
    })
}

fn mock_settings(config: &ShellConfig) -> Value {
    json!({
        "locale": config.locale, "theme": config.theme, "availableThemes": [config.theme],
        "volume": config.sound.default_volume, "muted": false, "idleTimeoutSec": config.idle.timeout_sec,
        "showMetricsOverlay": config.ui.show_metrics_overlay, "allowVirtualKeyboard": config.kiosk.allow_virtual_keyboard,
        "uiSounds": config.sound.ui_sounds, "features": config.features
    })
}

/// Recomputes `secondsLeft` / `secondsUsed` of an active mock session from its timestamps.
fn refresh_session(session: &mut Value) {
    if session["state"] != "active" {
        return;
    }
    let parse = |v: &Value| v.as_str().and_then(clubshell_protocol::wire::parse_ts);
    let now = Utc::now();
    if let Some(started) = parse(&session["startedAt"]) {
        session["secondsUsed"] = json!((now - started).num_seconds().max(0));
    }
    if let Some(ends) = parse(&session["endsAt"]) {
        let left = (ends - now).num_seconds();
        session["secondsLeft"] = json!(left.max(0));
        if left <= 0 {
            session["state"] = json!("ending");
        }
    }
}

fn session_state(session: Option<&Value>) -> &str {
    session.and_then(|s| s["state"].as_str()).unwrap_or("idle")
}

impl MockTransport {
    pub fn new(config: &ShellConfig) -> Self {
        let (events, _) = broadcast::channel(64);
        let (state, _) = watch::channel(ConnectivityStatus::new(ConnectionState::Connected, 0));
        let hello = AuthHelloResponse {
            agent_version: "mock".into(),
            protocol: PROTOCOL_VERSION,
            pc_id: MOCK_PC,
            pc_name: "PC-01".into(),
            zone: "main".into(),
            server_online: true,
            policy_version: 1,
            server_time: Utc::now(),
            capabilities: [
                agent_capabilities::ACCOUNT_POOL,
                agent_capabilities::CLOUD_SAVE,
                agent_capabilities::VIRTUAL_KEYBOARD,
                agent_capabilities::OFFLINE,
                agent_capabilities::SHOP,
                agent_capabilities::CHAT,
                agent_capabilities::BOOKING,
                agent_capabilities::TOURNAMENTS,
            ]
            .iter()
            .map(|s| (*s).to_owned())
            .collect(),
            kiosk_user: "club".into(),
        };
        Self {
            events,
            state,
            hello,
            request_timeout: config.ipc.request_timeout(),
            db: Mutex::new(MockDb { user: None, session: None, settings: mock_settings(config), running_game: None }),
        }
    }

    fn emit(&self, name: &str, payload: Value) {
        let _ = self.events.send(IpcEnvelope::event(name, Some(payload)));
    }

    /// Canned answer for `name`; enforces the IPC auth level like the real Agent.
    pub fn request(&self, name: &str, payload: Option<Value>) -> CmdResult<Option<Value>> {
        let Some(cmd) = AgentCommand::parse(name) else {
            return Err(mock_err(ErrorCode::NotFound, &format!("Unknown message '{name}'")).with_details(json!({ "name": name })));
        };
        let p = payload.unwrap_or(Value::Null);
        let mut db = self.db.lock();
        if let Some(s) = db.session.as_mut() {
            refresh_session(s);
        }
        match cmd.required_auth() {
            IpcAuthLevel::User if db.user.is_none() => return Err(mock_err(ErrorCode::Unauthorized, "Not logged in")),
            IpcAuthLevel::Session if !matches!(session_state(db.session.as_ref()), "active" | "paused") => {
                return Err(mock_err(ErrorCode::SessionNotActive, "No active session"))
            }
            _ => {}
        }
        let shell_version = env!("CARGO_PKG_VERSION");
        let user_id = db.user.as_ref().map(|u| u["id"].clone()).unwrap_or(Value::Null);
        let mut event: Option<(&str, Value)> = None;

        let result = match cmd {
            AgentCommand::AuthHello => Some(serde_json::to_value(&self.hello)?),
            AgentCommand::AuthLogin => {
                let kind = p["kind"].as_str().unwrap_or("password");
                if kind == "password" && p["password"].as_str().unwrap_or("").is_empty() {
                    return Err(mock_err(ErrorCode::Validation, "password required").with_details(json!({ "field": "password", "reason": "required" })));
                }
                let mut user = mock_user();
                if kind == "guest" {
                    user["role"] = json!("guest");
                    user["username"] = json!("guest");
                    user["displayName"] = json!("Guest");
                }
                if let Some(name) = p["username"].as_str().filter(|s| !s.is_empty()) {
                    user["username"] = json!(name);
                    user["displayName"] = json!(name);
                }
                db.user = Some(user.clone());
                Some(json!({ "user": user, "session": db.session, "expiresAt": in_secs(8 * 3600), "mode": "online" }))
            }
            AgentCommand::AuthLogout => {
                let session = db.session.take();
                db.user = None;
                db.running_game = None;
                if let Some(mut s) = session.clone() {
                    s["state"] = json!("ended");
                    event = Some((names::events::SESSION_ENDED, json!({ "session": s, "reason": "user", "charged": s["cost"].clone() })));
                }
                Some(json!({ "ok": true, "sessionEnded": session.is_some(), "session": session }))
            }
            AgentCommand::AuthStatus => Some(json!({
                "authenticated": db.user.is_some(), "mode": "online", "user": db.user, "session": db.session,
                "expiresAt": db.user.as_ref().map(|_| in_secs(8 * 3600))
            })),
            AgentCommand::AuthQrStart => Some(json!({
                "qrToken": "mock-qr-token", "qrUrl": "https://example.invalid/qr/mock", "expiresAt": in_secs(120), "pollIntervalSec": 2
            })),
            AgentCommand::SessionGet => Some(db.session.clone().unwrap_or(Value::Null)),
            AgentCommand::SessionStart => {
                if matches!(session_state(db.session.as_ref()), "active" | "paused" | "locked" | "ending") {
                    return Err(mock_err(ErrorCode::SessionAlreadyActive, "A session is already active"));
                }
                let minutes = p["minutes"].as_i64().unwrap_or(60).max(1);
                let prepaid = p["prepaid"].as_bool().unwrap_or(true);
                let tariff = p["tariffId"].as_str().and_then(|s| Uuid::parse_str(s).ok()).unwrap_or(MOCK_TARIFF_STD);
                let session = json!({
                    "id": Uuid::new_v4(), "userId": user_id, "pcId": MOCK_PC, "state": "active", "startedAt": now(),
                    "endsAt": in_secs(minutes * 60), "tariffId": tariff, "secondsLeft": minutes * 60, "secondsUsed": 0,
                    "cost": money(if prepaid { minutes * 250 } else { 0 }), "isPrepaid": prepaid, "warningsSent": []
                });
                db.session = Some(session.clone());
                event = Some((names::events::SESSION_UPDATED, session.clone()));
                Some(session)
            }
            AgentCommand::SessionPause | AgentCommand::SessionResume | AgentCommand::SessionLock | AgentCommand::SessionUnlock => {
                let session = db.session.as_mut().ok_or_else(|| mock_err(ErrorCode::SessionNotActive, "No active session"))?;
                match cmd {
                    AgentCommand::SessionPause => {
                        session["state"] = json!("paused");
                        session["pausedAt"] = now();
                    }
                    AgentCommand::SessionLock => {
                        session["state"] = json!("locked");
                        session["pausedAt"] = now();
                    }
                    _ => {
                        if let Some(paused) = session["pausedAt"].as_str().and_then(clubshell_protocol::wire::parse_ts) {
                            let gap = Utc::now() - paused;
                            if let Some(ends) = session["endsAt"].as_str().and_then(clubshell_protocol::wire::parse_ts) {
                                session["endsAt"] = ts(ends + gap);
                            }
                        }
                        session["state"] = json!("active");
                        if let Some(obj) = session.as_object_mut() {
                            obj.remove("pausedAt");
                        }
                    }
                }
                let session = session.clone();
                event = Some((names::events::SESSION_UPDATED, session.clone()));
                Some(session)
            }
            AgentCommand::SessionEnd => {
                let mut session = db.session.take().ok_or_else(|| mock_err(ErrorCode::SessionNotActive, "No active session"))?;
                db.running_game = None;
                session["state"] = json!("ended");
                let charged = session["cost"].clone();
                let reason = p["reason"].as_str().unwrap_or("user");
                event = Some((names::events::SESSION_ENDED, json!({ "session": session, "reason": reason, "charged": charged })));
                Some(json!({ "session": session, "charged": charged, "refunded": money(0) }))
            }
            AgentCommand::SessionExtend => {
                let session = db.session.as_mut().ok_or_else(|| mock_err(ErrorCode::SessionNotActive, "No active session"))?;
                let minutes = p["minutes"].as_i64().unwrap_or(30).max(1);
                if let Some(ends) = session["endsAt"].as_str().and_then(clubshell_protocol::wire::parse_ts) {
                    session["endsAt"] = ts(ends + ChronoDuration::minutes(minutes));
                }
                refresh_session(session);
                let session = session.clone();
                event = Some((names::events::SESSION_UPDATED, session.clone()));
                Some(session)
            }
            AgentCommand::SessionTimeLeft => {
                let s = db.session.as_ref();
                Some(json!({
                    "state": session_state(s), "secondsLeft": s.map(|s| s["secondsLeft"].clone()).unwrap_or(json!(0)),
                    "secondsUsed": s.map(|s| s["secondsUsed"].clone()).unwrap_or(json!(0)), "serverTime": now(),
                    "sessionId": s.map(|s| s["id"].clone()), "endsAt": s.map(|s| s["endsAt"].clone())
                }))
            }
            AgentCommand::GamesList => {
                let games = mock_games();
                let total = games.len();
                Some(json!({ "items": games, "total": total, "page": 1, "pageSize": 100, "catalogVersion": "mock-1" }))
            }
            AgentCommand::GamesGet | AgentCommand::GamesInstallStatus => {
                let id = p["gameId"].as_str().and_then(|s| Uuid::parse_str(s).ok());
                let game = mock_games().into_iter().find(|g| g["id"].as_str().and_then(|s| Uuid::parse_str(s).ok()) == id);
                let game = game.ok_or_else(|| mock_err(ErrorCode::NotFound, "Game not found"))?;
                if cmd == AgentCommand::GamesGet {
                    Some(game)
                } else {
                    Some(json!({ "gameId": game["id"], "installed": true, "installPath": game["installPath"], "sizeGb": game["sizeGb"], "version": game["version"], "verifiedAt": now(), "launcherReady": true }))
                }
            }
            AgentCommand::GamesLaunch => {
                let id = p["gameId"].as_str().and_then(|s| Uuid::parse_str(s).ok());
                let game = mock_games().into_iter().find(|g| g["id"].as_str().and_then(|s| Uuid::parse_str(s).ok()) == id);
                let game = game.ok_or_else(|| mock_err(ErrorCode::GameNotInstalled, "Game not found"))?;
                let running = json!({ "gameId": game["id"], "title": game["title"], "pid": MOCK_GAME_PID, "startedAt": now(), "state": "running" });
                db.running_game = Some(running.clone());
                event = Some((names::events::GAME_STATE_CHANGED, json!({ "gameId": game["id"], "title": game["title"], "pid": MOCK_GAME_PID, "state": "running", "at": now() })));
                Some(json!({ "ok": true, "pid": MOCK_GAME_PID, "startedAt": now() }))
            }
            AgentCommand::GamesKill => match db.running_game.take() {
                Some(game) => {
                    event = Some((names::events::GAME_STATE_CHANGED, json!({ "gameId": game["gameId"], "title": game["title"], "pid": MOCK_GAME_PID, "state": "killed", "at": now() })));
                    Some(json!({ "killed": 1, "pids": [MOCK_GAME_PID] }))
                }
                None => Some(json!({ "killed": 0, "pids": [] })),
            },
            AgentCommand::GamesRunning => Some(json!({ "items": db.running_game.iter().collect::<Vec<_>>() })),
            AgentCommand::AppsList => Some(json!({ "items": [
                { "id": MOCK_APP, "title": "Discord", "exePath": "C:\\Program Files\\Discord\\Discord.exe", "iconUrl": "", "category": "chat", "allowed": true }
            ] })),
            AgentCommand::AppsLaunch => Some(json!({ "ok": true, "pid": 4343, "startedAt": now() })),
            AgentCommand::WalletBalance => Some(json!({ "userId": user_id, "amount": money(50_000), "bonus": money(0), "currency": "UZS", "updatedAt": now() })),
            AgentCommand::WalletTariffs => Some(json!({ "items": mock_tariffs(), "zone": p["zone"].as_str().unwrap_or("main"), "serverTime": now() })),
            AgentCommand::WalletHistory => Some(json!({ "items": [], "total": 0, "page": 1, "pageSize": 20 })),
            AgentCommand::WalletTopupIntent => {
                let provider = p["provider"].as_str().unwrap_or("payme");
                let amount = p["amount"].as_object().map(|_| p["amount"].clone()).unwrap_or_else(|| money(10_000));
                Some(json!({
                    "id": Uuid::new_v4(), "provider": provider, "amount": amount, "status": "pending",
                    "paymentUrl": "https://example.invalid/pay/mock", "expiresAt": in_secs(900), "createdAt": now()
                }))
            }
            AgentCommand::ShopProducts => Some(json!({ "items": [] })),
            AgentCommand::ShopOrder | AgentCommand::BookingReserve => {
                return Err(mock_err(ErrorCode::ServerUnavailable, "Not available in mock mode"));
            }
            AgentCommand::ShopOrderStatus | AgentCommand::BookingCancel | AgentCommand::TournamentsJoin => {
                return Err(mock_err(ErrorCode::NotFound, "Not found in mock mode"));
            }
            AgentCommand::ShopOrders => Some(json!({ "items": [], "total": 0 })),
            AgentCommand::ChatHistory => Some(json!({ "roomId": p["roomId"].as_str().unwrap_or("club"), "items": [], "hasMore": false, "unread": 0 })),
            AgentCommand::ChatSend => {
                let user = db.user.as_ref().ok_or_else(|| mock_err(ErrorCode::Unauthorized, "Not logged in"))?;
                let message = json!({
                    "id": Uuid::new_v4(), "roomId": p["roomId"].as_str().unwrap_or("club"), "senderId": user["id"],
                    "senderName": user["displayName"], "senderRole": user["role"], "text": p["text"].as_str().unwrap_or(""),
                    "createdAt": now(), "kind": "text"
                });
                event = Some((names::events::CHAT_MESSAGE, message.clone()));
                Some(message)
            }
            AgentCommand::ChatMarkRead => Some(json!({ "roomId": p["roomId"].as_str().unwrap_or("club"), "unread": 0 })),
            AgentCommand::BookingSeats => Some(json!({
                "date": p["date"].as_str().map(str::to_owned).unwrap_or_else(|| Utc::now().format("%Y-%m-%d").to_string()),
                "seats": [], "bookings": [], "slotMinutes": 30
            })),
            AgentCommand::TournamentsList => Some(json!({ "items": [] })),
            AgentCommand::TournamentsLeaderboard => Some(json!({
                "tournamentId": p["tournamentId"].as_str().and_then(|s| Uuid::parse_str(s).ok()).unwrap_or(Uuid::nil()),
                "entries": [], "updatedAt": now()
            })),
            AgentCommand::ProfileGet => Some(db.user.clone().unwrap_or(Value::Null)),
            AgentCommand::ProfileUpdate => {
                let user = db.user.as_mut().ok_or_else(|| mock_err(ErrorCode::Unauthorized, "Not logged in"))?;
                for key in ["displayName", "avatarUrl", "locale"] {
                    if !p[key].is_null() {
                        user[key] = p[key].clone();
                    }
                }
                Some(user.clone())
            }
            AgentCommand::ProfileStats => Some(json!({ "totalHours": 12.5, "sessionsCount": 8, "favoriteGames": [], "spent": money(120_000), "rank": 42 })),
            AgentCommand::ProfileAchievements => Some(json!({ "items": [] })),
            AgentCommand::ProfileLoyalty => Some(json!({ "level": 1, "points": 120, "nextLevelAt": 500, "perks": [] })),
            AgentCommand::SettingsGet => Some(db.settings.clone()),
            AgentCommand::SettingsSet => {
                if let Some(patch) = p.as_object() {
                    for (key, value) in patch {
                        if !value.is_null() && db.settings.get(key).is_some() {
                            db.settings[key] = value.clone();
                        }
                    }
                }
                Some(db.settings.clone())
            }
            AgentCommand::SysPing => Some(json!({ "seq": p["seq"], "sentAt": p["sentAt"], "receivedAt": now(), "connectivity": "online" })),
            AgentCommand::SysPcInfo => Some(json!({
                "pc": mock_pc(shell_version), "agentVersion": "mock", "shellVersion": shell_version, "protocolVersion": PROTOCOL_VERSION,
                "uptimeSec": 3600, "kioskUser": "club", "connectivity": "online", "serverTime": now(), "policyVersion": 1
            })),
            AgentCommand::SysHardware => Some(json!({
                "cpu": { "model": "Mock CPU", "cores": 8, "threads": 16 },
                "gpu": [{ "model": "Mock GPU", "vramMb": 8192, "driver": "1.0" }],
                "ramMb": 16384,
                "disks": [{ "mount": "C:", "totalGb": 512, "freeGb": 200, "type": "nvme" }],
                "monitors": [{ "index": 0, "width": 1920, "height": 1080, "hz": 144, "primary": true }],
                "network": { "mac": "00:00:00:00:00:00", "ip": "127.0.0.1", "adapter": "Mock" },
                "os": { "version": "Windows 11", "build": "26200" },
                "peripherals": []
            })),
            AgentCommand::SysMetrics => Some(json!({
                "cpuPct": 12, "gpuPct": 5, "ramUsedMb": 4096, "temps": { "cpu": 45, "gpu": 40 },
                "netMbps": { "up": 0.5, "down": 2.5 }, "uptimeSec": 3600, "at": now()
            })),
            AgentCommand::SysCallAdmin => Some(json!({ "ticketId": Uuid::new_v4(), "createdAt": now(), "queuePosition": 1 })),
            AgentCommand::SysReboot | AgentCommand::SysShutdown => Some(json!({ "scheduledAt": in_secs(p["delaySec"].as_i64().unwrap_or(0)) })),
            AgentCommand::SysLockScreen | AgentCommand::SysAckAdminMessage => Some(json!({ "ok": true })),
            AgentCommand::SysSetVolume => {
                let level = p["level"].as_i64().unwrap_or(50).clamp(0, 100);
                let muted = p["muted"].as_bool().unwrap_or(false);
                db.settings["volume"] = json!(level);
                db.settings["muted"] = json!(muted);
                Some(json!({ "level": level, "muted": muted }))
            }
            AgentCommand::SysSetLocale => {
                let locale = p["locale"].clone();
                if locale.is_null() {
                    return Err(mock_err(ErrorCode::Validation, "locale required"));
                }
                db.settings["locale"] = locale.clone();
                Some(json!({ "locale": locale }))
            }
            AgentCommand::SysUnlockAdmin => {
                if p["pin"].as_str() != Some(MOCK_ADMIN_PIN) {
                    return Err(mock_err(ErrorCode::Unauthorized, "Wrong PIN"));
                }
                Some(json!({ "ok": true, "adminToken": format!("mock-admin-{}", Uuid::new_v4().simple()), "expiresAt": in_secs(600) }))
            }
            AgentCommand::SysLogClientError => {
                tracing::warn!(target: "webview", level = %p["level"], message = %p["message"], route = %p["route"], "client error (mock agent)");
                None
            }
            AgentCommand::PolicyGet => Some(mock_policy()),
            AgentCommand::PolicyReload => Some(json!({ "policy": mock_policy(), "source": "cache", "applied": true, "changed": [] })),
            AgentCommand::UpdateCheck => Some(json!({ "current": { "agent": "mock", "shell": shell_version } })),
            AgentCommand::UpdateApply => Some(json!({ "scheduled": false })),
        };
        drop(db);
        if let Some((name, payload)) = event {
            self.emit(name, payload);
        }
        Ok(result.filter(|v| !v.is_null()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clubshell_protocol::commands::{AuthLoginResponse, GamesListResponse, SessionTimeLeftResponse};
    use clubshell_protocol::session::{Session, SessionState};
    use clubshell_protocol::user::User;

    #[tokio::test]
    async fn mock_covers_login_session_flow_with_typed_responses() {
        let cfg = ShellConfig::defaults();
        let agent = AgentClient::mock(&cfg);
        assert!(agent.is_mock());
        assert!(agent.is_connected());
        assert_eq!(agent.pc_info().unwrap().pc_id, MOCK_PC);
        let mut events = agent.events();

        // Auth gate like the real Agent.
        let err = agent.request_optional::<_, Session>(names::session::GET, &()).await.unwrap_err();
        assert_eq!(err.code, ErrorCode::Unauthorized);

        let login: AuthLoginResponse = agent.request(names::auth::LOGIN, &json!({ "kind": "password", "username": "bob", "password": "x" })).await.unwrap();
        assert_eq!(login.user.username, "bob");
        let none: Option<Session> = agent.request_optional(names::session::GET, &()).await.unwrap();
        assert!(none.is_none());

        let session: Session = agent.request(names::session::START, &json!({ "tariffId": MOCK_TARIFF_STD, "prepaid": true, "minutes": 30 })).await.unwrap();
        assert_eq!(session.state, SessionState::Active);
        assert_eq!(session.seconds_left, 1800);
        let ev = events.recv().await.unwrap();
        assert_eq!(ev.name, names::events::SESSION_UPDATED);

        let paused: Session = agent.request(names::session::PAUSE, &()).await.unwrap();
        assert_eq!(paused.state, SessionState::Paused);
        let left: SessionTimeLeftResponse = agent.request(names::session::TIME_LEFT, &()).await.unwrap();
        assert_eq!(left.state, SessionState::Paused);

        let games: GamesListResponse = agent.request(names::games::LIST, &()).await.unwrap();
        assert_eq!(games.items.len(), 2);
        let user: User = agent.request(names::profile::GET, &()).await.unwrap();
        assert_eq!(user.id, MOCK_USER);

        // Every command has an answer (or a deliberate error), never a protocol failure.
        for cmd in AgentCommand::ALL {
            let res = agent.request_raw(cmd.name(), Some(json!({ "gameId": MOCK_GAME_CS2, "tournamentId": MOCK_GAME_CS2, "pin": "0000", "locale": "en", "text": "hi", "level": "warn", "message": "m" }))).await;
            if let Err(e) = res {
                assert_eq!(e.source, crate::state::ErrorSource::Mock, "{}: {e}", cmd.name());
            }
        }
        let unknown = agent.request_raw("nope.nope", None).await.unwrap_err();
        assert_eq!(unknown.code, ErrorCode::NotFound);
    }
}
