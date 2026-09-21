//! Named-pipe transport: owns the current `clubshell_winutil::pipe::PipeClient`, re-fans its events
//! into one long-lived broadcast channel (subscribers survive reconnects), performs `auth.hello`
//! (IPC_PROTOCOL.md §3) and maps transport failures to [`ShellError`]. Heartbeats (§4) are handled
//! inside `PipeClient`; reconnecting is the [`super::Supervisor`]'s job.

use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Duration;

use chrono::{DateTime, Utc};
use clubshell_protocol::commands::{
    names, shell_capabilities, AuthHelloRequest, AuthHelloResponse,
};
use clubshell_protocol::ipc::IpcEnvelope;
use clubshell_protocol::PROTOCOL_VERSION;
use clubshell_winutil::pipe::{PipeClient, PipeOptions, EVENT_BUFFER};
use clubshell_winutil::WinUtilError;
use parking_lot::{Mutex, RwLock};
use serde::Serialize;
use serde_json::Value;
use tokio::sync::{broadcast, watch};

use super::{ConnectionState, ConnectivityStatus, HELLO_WAIT};
use crate::config::ShellConfig;
use crate::state::{CmdResult, ShellError};

/// Request/connection counters (`kiosk_state` diagnostics, logs).
#[derive(Serialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MetricsSnapshot {
    pub requests: u64,
    /// Error envelopes + transport failures.
    pub errors: u64,
    pub timeouts: u64,
    /// Successful hellos after the first one.
    pub reconnects: u32,
    pub connected: bool,
    #[serde(with = "clubshell_protocol::wire::ts_opt")]
    pub connected_since: Option<DateTime<Utc>>,
}

#[derive(Default)]
struct Metrics {
    requests: AtomicU64,
    errors: AtomicU64,
    timeouts: AtomicU64,
    hellos: AtomicU32,
    connected_since: RwLock<Option<DateTime<Utc>>>,
}

/// One logical connection to the Agent across any number of physical pipe connections.
pub struct PipeTransport {
    pipe_name: String,
    connect_timeout: Duration,
    options: PipeOptions,
    current: RwLock<Option<Arc<PipeClient>>>,
    forwarder: Mutex<Option<tauri::async_runtime::JoinHandle<()>>>,
    events: broadcast::Sender<IpcEnvelope>,
    state: watch::Sender<ConnectivityStatus>,
    hello: RwLock<Option<AuthHelloResponse>>,
    metrics: Metrics,
}

impl PipeTransport {
    pub fn new(config: &ShellConfig) -> Self {
        let (events, _) = broadcast::channel(EVENT_BUFFER);
        let (state, _) = watch::channel(ConnectivityStatus::new(ConnectionState::Disconnected, 0));
        Self {
            pipe_name: config.ipc.pipe_name.clone(),
            connect_timeout: config.ipc.connect_timeout(),
            options: PipeOptions {
                request_timeout: config.ipc.request_timeout(),
                ..PipeOptions::default()
            },
            current: RwLock::new(None),
            forwarder: Mutex::new(None),
            events,
            state,
            hello: RwLock::new(None),
            metrics: Metrics::default(),
        }
    }

    pub fn pipe_name(&self) -> &str {
        &self.pipe_name
    }

    /// Opens the pipe (retrying busy / not-yet-created for `ipc.connectTimeoutMs`). The connection
    /// is not installed until [`attach`](Self::attach).
    pub async fn connect(&self) -> Result<PipeClient, WinUtilError> {
        PipeClient::connect_with(&self.pipe_name, self.connect_timeout, self.options.clone()).await
    }

    /// Installs `client` as the current connection and forwards its events to
    /// [`subscribe`](Self::subscribe)rs. Requests still wait for [`publish`](Self::publish)`(Connected)`.
    pub fn attach(&self, client: Arc<PipeClient>) {
        self.detach();
        let mut rx = client.subscribe();
        let events = self.events.clone();
        let task = tauri::async_runtime::spawn(async move {
            loop {
                match rx.recv().await {
                    Ok(env) => {
                        let _ = events.send(env);
                    }
                    Err(broadcast::error::RecvError::Lagged(n)) => {
                        tracing::warn!(skipped = n, "agent events lagged")
                    }
                    Err(broadcast::error::RecvError::Closed) => break,
                }
            }
        });
        *self.forwarder.lock() = Some(task);
        *self.current.write() = Some(client);
    }

    /// Closes and drops the current connection (pending requests fail with `agentOffline`) and
    /// forgets the hello. Does not change the published state; callers [`publish`](Self::publish).
    pub fn detach(&self) {
        if let Some(client) = self.current.write().take() {
            client.close();
        }
        if let Some(task) = self.forwarder.lock().take() {
            task.abort();
        }
        *self.hello.write() = None;
        *self.metrics.connected_since.write() = None;
    }

    /// Publishes a state change; `false` when nothing changed. `since` is kept while `agent` is
    /// unchanged, so it marks when the current state was entered.
    pub fn publish(&self, agent: ConnectionState, attempts: u32) -> bool {
        let (changed, since) = {
            let cur = self.state.borrow();
            (
                cur.agent != agent || cur.attempts != attempts,
                if cur.agent == agent {
                    cur.since
                } else {
                    Utc::now()
                },
            )
        };
        if !changed {
            return false;
        }
        if agent == ConnectionState::Connected {
            *self.metrics.connected_since.write() = Some(since);
        }
        let _ = self.state.send_replace(ConnectivityStatus {
            agent,
            attempts,
            since,
        });
        true
    }

    /// `auth.hello` on `client`: reads the token file (regenerated at every Agent start, so never
    /// cached), sends `AuthHelloRequest` and stores the response for [`pc_info`](Self::pc_info).
    pub async fn hello(
        &self,
        client: &PipeClient,
        config: &ShellConfig,
    ) -> CmdResult<AuthHelloResponse> {
        let shell_token = config
            .load_shell_token()
            .map_err(|e| ShellError::unauthorized(format!("shell token unavailable: {e}")))?;
        let mut capabilities = vec![
            shell_capabilities::MULTI_MONITOR.to_owned(),
            shell_capabilities::OVERLAY.to_owned(),
        ];
        if config.gamepad.enabled {
            capabilities.push(shell_capabilities::GAMEPAD.to_owned());
        }
        if config.kiosk.allow_virtual_keyboard {
            capabilities.push(shell_capabilities::VIRTUAL_KEYBOARD.to_owned());
        }
        let request = AuthHelloRequest {
            shell_token,
            shell_version: env!("CARGO_PKG_VERSION").to_owned(),
            pid: std::process::id() as i32,
            wts_session_id: wts_session_id(),
            locale: config.locale,
            capabilities,
        };
        let envelope = IpcEnvelope::request_with(names::auth::HELLO, &request)?;
        let response = client
            .request_timeout(envelope, self.options.request_timeout)
            .await?;
        if let Some(err) = response.error {
            return Err(err.into());
        }
        let hello: AuthHelloResponse = response.require_payload()?;
        if hello.protocol != PROTOCOL_VERSION {
            tracing::warn!(
                agent_protocol = hello.protocol,
                shell_protocol = PROTOCOL_VERSION,
                "IPC protocol major differs"
            );
        }
        self.metrics.hellos.fetch_add(1, Ordering::Relaxed);
        *self.hello.write() = Some(hello.clone());
        Ok(hello)
    }

    /// Sends a request on the current connection, waiting up to `min(timeout, HELLO_WAIT)` for the
    /// hello to complete first. Error envelopes become `ShellError { source: ipc }`; `Ok(None)` is a
    /// `null` response payload.
    pub async fn send(&self, envelope: IpcEnvelope, timeout: Duration) -> CmdResult<Option<Value>> {
        let client = self.wait_ready(timeout.min(HELLO_WAIT)).await?;
        self.metrics.requests.fetch_add(1, Ordering::Relaxed);
        let name = envelope.name.clone();
        let response = match client.request_timeout(envelope, timeout).await {
            Ok(response) => response,
            Err(e) => {
                self.metrics.errors.fetch_add(1, Ordering::Relaxed);
                if matches!(e, WinUtilError::Timeout) {
                    self.metrics.timeouts.fetch_add(1, Ordering::Relaxed);
                }
                tracing::warn!(name = %name, error = %e, "IPC request failed");
                return Err(e.into());
            }
        };
        match response.error {
            Some(err) => {
                self.metrics.errors.fetch_add(1, Ordering::Relaxed);
                tracing::debug!(name = %name, code = %err.code, message = %err.message, "IPC error envelope");
                Err(err.into())
            }
            None => Ok(response.payload.filter(|v| !v.is_null())),
        }
    }

    async fn wait_ready(&self, max: Duration) -> CmdResult<Arc<PipeClient>> {
        if let Some(client) = self.ready_client() {
            return Ok(client);
        }
        let mut rx = self.state.subscribe();
        // The `Ref` returned by `wait_for` holds the watch's read lock; it must be released (end of
        // this statement) before `ready_client` takes the same lock again on this thread.
        let connected = matches!(
            tokio::time::timeout(max, rx.wait_for(ConnectivityStatus::is_connected)).await,
            Ok(Ok(_))
        );
        if !connected {
            return Err(ShellError::agent_offline());
        }
        self.ready_client().ok_or_else(ShellError::agent_offline)
    }

    fn ready_client(&self) -> Option<Arc<PipeClient>> {
        if !self.state.borrow().is_connected() {
            return None;
        }
        self.current
            .read()
            .as_ref()
            .filter(|c| c.is_connected())
            .cloned()
    }

    /// Current physical connection, hello or not.
    pub fn client(&self) -> Option<Arc<PipeClient>> {
        self.current.read().clone()
    }

    /// Agent → Shell events across reconnects.
    pub fn subscribe(&self) -> broadcast::Receiver<IpcEnvelope> {
        self.events.subscribe()
    }

    pub fn state(&self) -> watch::Receiver<ConnectivityStatus> {
        self.state.subscribe()
    }

    pub fn current_status(&self) -> ConnectivityStatus {
        self.state.borrow().clone()
    }

    pub fn pc_info(&self) -> Option<AuthHelloResponse> {
        self.hello.read().clone()
    }

    pub fn metrics(&self) -> MetricsSnapshot {
        MetricsSnapshot {
            requests: self.metrics.requests.load(Ordering::Relaxed),
            errors: self.metrics.errors.load(Ordering::Relaxed),
            timeouts: self.metrics.timeouts.load(Ordering::Relaxed),
            reconnects: self
                .metrics
                .hellos
                .load(Ordering::Relaxed)
                .saturating_sub(1),
            connected: self.state.borrow().is_connected(),
            connected_since: *self.metrics.connected_since.read(),
        }
    }
}

impl Drop for PipeTransport {
    fn drop(&mut self) {
        self.detach();
    }
}

/// Windows session of this process (`AuthHelloRequest::wts_session_id`); `-1` when unknown.
#[cfg(windows)]
#[allow(unsafe_code)]
fn wts_session_id() -> i32 {
    use windows::Win32::System::RemoteDesktop::{
        ProcessIdToSessionId, WTSGetActiveConsoleSessionId,
    };
    let mut session = u32::MAX;
    // SAFETY: `session` is a live, writable u32 for the duration of the call; the pid is our own.
    let _ = unsafe { ProcessIdToSessionId(std::process::id(), &mut session) };
    if session == u32::MAX {
        // SAFETY: no arguments and no preconditions; returns 0xFFFFFFFF when there is no console session.
        session = unsafe { WTSGetActiveConsoleSessionId() };
    }
    session as i32
}

#[cfg(not(windows))]
fn wts_session_id() -> i32 {
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    use clubshell_protocol::error::ErrorCode;
    use serde_json::json;

    #[tokio::test]
    async fn requests_wait_for_hello_then_fail_fast_when_offline() {
        let cfg = ShellConfig::defaults();
        let transport = PipeTransport::new(&cfg);
        assert_eq!(
            transport.current_status().agent,
            ConnectionState::Disconnected
        );
        let started = std::time::Instant::now();
        let err = transport
            .send(
                IpcEnvelope::request(names::auth::STATUS, None),
                Duration::from_millis(200),
            )
            .await
            .unwrap_err();
        assert_eq!(err.code, ErrorCode::AgentOffline);
        assert!(started.elapsed() < Duration::from_secs(2));
        assert_eq!(transport.metrics().requests, 0);

        // Publishing keeps `since` while the state is unchanged and updates it on transitions.
        transport.publish(ConnectionState::Connecting, 1);
        let first = transport.current_status();
        transport.publish(ConnectionState::Connecting, 2);
        let second = transport.current_status();
        assert_eq!((second.attempts, second.since), (2, first.since));
        transport.publish(ConnectionState::Connected, 0);
        assert!(transport.current_status().is_connected());
        assert!(transport.metrics().connected_since.is_some());
        // Connected state without a live client still fails closed.
        let err = transport
            .send(
                IpcEnvelope::request(names::auth::STATUS, None),
                Duration::from_millis(50),
            )
            .await
            .unwrap_err();
        assert_eq!(err.code, ErrorCode::AgentOffline);
    }

    #[tokio::test]
    async fn send_over_duplex_maps_errors_and_forwards_events() {
        use clubshell_winutil::pipe::test_server::serve_echo;
        let cfg = ShellConfig::defaults();
        let transport = PipeTransport::new(&cfg);
        let (a, b) = tokio::io::duplex(64 * 1024);
        let (ev_tx, ev_rx) = tokio::sync::mpsc::channel(4);
        let server = tokio::spawn(serve_echo(b, Some(ev_rx)));
        let client = Arc::new(PipeClient::from_stream(
            a,
            PipeOptions {
                heartbeat: false,
                ..PipeOptions::default()
            },
        ));
        let mut events = transport.subscribe();
        transport.attach(Arc::clone(&client));
        transport.publish(ConnectionState::Connected, 0);

        let echoed = transport
            .send(
                IpcEnvelope::request(names::games::RUNNING, Some(json!({ "x": 1 }))),
                Duration::from_secs(1),
            )
            .await
            .unwrap();
        assert_eq!(echoed, Some(json!({ "x": 1 })));
        let none = transport
            .send(
                IpcEnvelope::request(names::session::GET, None),
                Duration::from_secs(1),
            )
            .await
            .unwrap();
        assert_eq!(none, None);

        ev_tx
            .send(IpcEnvelope::event(
                names::events::WALLET_UPDATED,
                Some(json!({ "amount": 1 })),
            ))
            .await
            .unwrap();
        let ev = tokio::time::timeout(Duration::from_secs(2), events.recv())
            .await
            .unwrap()
            .unwrap();
        assert_eq!(ev.name, names::events::WALLET_UPDATED);

        transport.detach();
        assert!(transport.client().is_none());
        assert!(transport.pc_info().is_none());
        drop(client);
        let _ = tokio::time::timeout(Duration::from_secs(2), server).await;
        assert_eq!(transport.metrics().requests, 2);
    }
}
