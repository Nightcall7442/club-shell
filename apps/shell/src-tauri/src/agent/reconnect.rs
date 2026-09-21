//! Reconnect supervisor (IPC_PROTOCOL.md §4/§9.1, ARCHITECTURE.md §6.2 step 4): connect → hello →
//! run until the pipe closes → exponential backoff with jitter (`ipc.reconnectMinMs..MaxMs`) → again,
//! forever, until the shutdown flag flips. Every state transition goes to the transport's watch (and
//! the optional callback), which the event forwarder turns into `kiosk://connectivity`.

use std::sync::Arc;
use std::time::Duration;

use clubshell_winutil::pipe::PipeClient;
use tokio::sync::watch;
use uuid::Uuid;

use super::pipe_client::PipeTransport;
use super::{ConnectionState, ConnectivityStatus};
use crate::config::ShellConfig;

/// Invoked after every published state change (in addition to the watch channel).
pub type StateCallback = Arc<dyn Fn(&ConnectivityStatus) + Send + Sync>;

/// Exponential backoff with ±25 % jitter.
#[derive(Clone, Debug)]
pub struct Backoff {
    min: Duration,
    max: Duration,
    current: Duration,
}

impl Backoff {
    pub fn new(min: Duration, max: Duration) -> Self {
        let min = min.max(Duration::from_millis(1));
        Self { min, max: max.max(min), current: min }
    }

    pub fn reset(&mut self) {
        self.current = self.min;
    }

    /// Current delay (jittered), then doubles the next one up to `max`.
    pub fn next_delay(&mut self) -> Duration {
        let delay = jitter(self.current);
        self.current = (self.current * 2).min(self.max);
        delay
    }
}

fn jitter(d: Duration) -> Duration {
    // ponytail: uuid v4 bytes as the entropy source; good enough for jitter, no `rand` dependency.
    let unit = (Uuid::new_v4().as_u128() % 1000) as f64 / 1000.0;
    d.mul_f64(0.75 + unit * 0.5)
}

/// Resolves when `shutdown` becomes `true` (or its sender is gone).
async fn wait_shutdown(shutdown: &mut watch::Receiver<bool>) {
    while !*shutdown.borrow_and_update() {
        if shutdown.changed().await.is_err() {
            return;
        }
    }
}

/// Sleeps `delay`; `false` when shutdown interrupted the sleep.
async fn sleep_or_shutdown(delay: Duration, shutdown: &mut watch::Receiver<bool>) -> bool {
    tokio::select! {
        _ = tokio::time::sleep(delay) => true,
        _ = wait_shutdown(shutdown) => false,
    }
}

/// Connection supervisor for one [`PipeTransport`].
pub struct Supervisor {
    transport: Arc<PipeTransport>,
    config: Arc<ShellConfig>,
    shutdown: watch::Receiver<bool>,
    on_state: Option<StateCallback>,
    initial: Option<PipeClient>,
}

impl Supervisor {
    pub fn new(transport: Arc<PipeTransport>, config: Arc<ShellConfig>, shutdown: watch::Receiver<bool>) -> Self {
        Self { transport, config, shutdown, on_state: None, initial: None }
    }

    /// Uses an already open connection for the first cycle instead of connecting.
    pub fn with_initial(mut self, client: PipeClient) -> Self {
        self.initial = Some(client);
        self
    }

    pub fn on_state(mut self, callback: StateCallback) -> Self {
        self.on_state = Some(callback);
        self
    }

    /// Runs [`run`](Self::run) on the Tauri async runtime.
    pub fn spawn(self) -> tauri::async_runtime::JoinHandle<()> {
        tauri::async_runtime::spawn(self.run())
    }

    fn publish(&self, agent: ConnectionState, attempts: u32) {
        if !self.transport.publish(agent, attempts) {
            return;
        }
        if let Some(cb) = &self.on_state {
            cb(&self.transport.current_status());
        }
    }

    /// The supervision loop; returns only after shutdown.
    pub async fn run(mut self) {
        let mut shutdown = self.shutdown.clone();
        let mut backoff = Backoff::new(self.config.ipc.reconnect_min(), self.config.ipc.reconnect_max());
        let mut attempts = 0u32;
        tracing::info!(pipe = %self.transport.pipe_name(), "agent supervisor started");

        while !*shutdown.borrow_and_update() {
            attempts = attempts.saturating_add(1);
            self.publish(ConnectionState::Connecting, attempts);

            let client = match self.initial.take() {
                Some(client) => Ok(client),
                None => self.transport.connect().await,
            };
            let client = match client {
                Ok(client) => Arc::new(client),
                Err(e) => {
                    tracing::warn!(attempt = attempts, error = %e, "pipe connect failed");
                    if !sleep_or_shutdown(backoff.next_delay(), &mut shutdown).await {
                        break;
                    }
                    continue;
                }
            };

            self.transport.attach(Arc::clone(&client));
            match self.transport.hello(&client, &self.config).await {
                Ok(hello) => {
                    tracing::info!(
                        agent_version = %hello.agent_version,
                        pc_id = %hello.pc_id,
                        pc_name = %hello.pc_name,
                        zone = %hello.zone,
                        server_online = hello.server_online,
                        policy_version = hello.policy_version,
                        "auth.hello accepted"
                    );
                    backoff.reset();
                    attempts = 0;
                    self.publish(ConnectionState::Connected, 0);
                }
                Err(e) => {
                    tracing::error!(attempt = attempts, code = %e.code, error = %e.message, "auth.hello failed");
                    self.transport.detach();
                    if !sleep_or_shutdown(backoff.next_delay(), &mut shutdown).await {
                        break;
                    }
                    continue;
                }
            }

            let reason = tokio::select! {
                reason = client.closed() => Some(reason),
                _ = wait_shutdown(&mut shutdown) => None,
            };
            self.transport.detach();
            let Some(reason) = reason else { break };
            tracing::warn!(?reason, "pipe closed; reconnecting");
            self.publish(ConnectionState::Disconnected, 0);
            if !sleep_or_shutdown(backoff.next_delay(), &mut shutdown).await {
                break;
            }
        }

        self.transport.detach();
        self.publish(ConnectionState::Disconnected, 0);
        tracing::info!("agent supervisor stopped");
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::Utc;
    use clubshell_protocol::commands::{names, AuthHelloRequest, AuthHelloResponse, SysPingRequest, SysPongResponse};
    use clubshell_protocol::ipc::{encode_frame, FrameReader, IpcEnvelope, IpcKind};
    use clubshell_protocol::pc::ConnectivityState;
    use clubshell_protocol::PROTOCOL_VERSION;
    use clubshell_winutil::pipe::{drain_frames, PipeOptions};
    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    #[test]
    fn backoff_doubles_with_jitter_and_caps() {
        let mut b = Backoff::new(Duration::from_millis(500), Duration::from_millis(5000));
        let d1 = b.next_delay();
        assert!((375..=625).contains(&d1.as_millis()), "{d1:?}");
        let d2 = b.next_delay();
        assert!((750..=1250).contains(&d2.as_millis()), "{d2:?}");
        for _ in 0..10 {
            b.next_delay();
        }
        let capped = b.next_delay();
        assert!(capped <= Duration::from_millis(6250) && capped >= Duration::from_millis(3750), "{capped:?}");
        b.reset();
        assert!(b.next_delay() <= Duration::from_millis(625));
        // Degenerate configuration is clamped instead of panicking.
        let mut z = Backoff::new(Duration::ZERO, Duration::ZERO);
        assert!(z.next_delay() <= Duration::from_millis(2));
    }

    /// Minimal Agent: answers `auth.hello` with a real response (checking the token) and pings with pongs.
    async fn fake_agent<S>(mut stream: S, token: String)
    where
        S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin,
    {
        let mut reader = FrameReader::new();
        let mut buf = vec![0u8; 16 * 1024];
        loop {
            let n = match stream.read(&mut buf).await {
                Ok(0) | Err(_) => return,
                Ok(n) => n,
            };
            for req in drain_frames(&mut reader, &buf[..n]).unwrap() {
                if req.kind != IpcKind::Request {
                    continue;
                }
                let reply = if req.name == names::auth::HELLO {
                    let hello: AuthHelloRequest = req.require_payload().unwrap();
                    assert_eq!(hello.shell_token, token);
                    assert_eq!(hello.shell_version, env!("CARGO_PKG_VERSION"));
                    let resp = AuthHelloResponse {
                        agent_version: "1.0.0".into(),
                        protocol: PROTOCOL_VERSION,
                        pc_id: Uuid::from_u128(7),
                        pc_name: "PC-07".into(),
                        zone: "main".into(),
                        server_online: true,
                        policy_version: 3,
                        server_time: Utc::now(),
                        capabilities: vec![],
                        kiosk_user: "club".into(),
                    };
                    IpcEnvelope::reply_to_with(&req, &resp).unwrap()
                } else if req.name == names::sys::PING {
                    let ping: SysPingRequest = req.require_payload().unwrap();
                    let pong = SysPongResponse { seq: ping.seq, sent_at: ping.sent_at, received_at: Utc::now(), connectivity: ConnectivityState::Online };
                    IpcEnvelope::reply_to_with(&req, &pong).unwrap()
                } else {
                    IpcEnvelope::reply_to(&req, req.payload.clone())
                };
                stream.write_all(&encode_frame(&reply).unwrap()).await.unwrap();
            }
        }
    }

    #[tokio::test]
    async fn supervisor_hellos_then_reconnects_after_peer_close_and_stops_on_shutdown() {
        let dir = std::env::temp_dir().join(format!("clubshell-sup-{}", Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let token = "0123456789abcdef".repeat(4);
        std::fs::write(dir.join("shell.token"), &token).unwrap();
        let mut cfg = ShellConfig::defaults();
        cfg.runtime.token_path = dir.join("shell.token");
        cfg.ipc.reconnect_min_ms = 20;
        cfg.ipc.reconnect_max_ms = 40;
        cfg.ipc.connect_timeout_ms = 10;
        let cfg = Arc::new(cfg);

        let transport = Arc::new(PipeTransport::new(&cfg));
        let (a, b) = tokio::io::duplex(64 * 1024);
        let agent = tokio::spawn(fake_agent(b, token));
        let client = PipeClient::from_stream(a, PipeOptions { heartbeat: false, ..PipeOptions::default() });
        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        let seen: Arc<parking_lot::Mutex<Vec<ConnectionState>>> = Arc::default();
        let cb_seen = Arc::clone(&seen);
        let supervisor = Supervisor::new(Arc::clone(&transport), Arc::clone(&cfg), shutdown_rx)
            .with_initial(client)
            .on_state(Arc::new(move |s: &ConnectivityStatus| cb_seen.lock().push(s.agent)));
        let mut state = transport.state();
        let task = tokio::spawn(supervisor.run());

        tokio::time::timeout(Duration::from_secs(5), state.wait_for(ConnectivityStatus::is_connected)).await.unwrap().unwrap();
        let hello = transport.pc_info().unwrap();
        assert_eq!((hello.pc_name.as_str(), hello.policy_version), ("PC-07", 3));
        assert_eq!(transport.current_status().attempts, 0);

        // Requests flow through the supervised connection.
        let echoed = transport.send(IpcEnvelope::request(names::games::RUNNING, Some(serde_json::json!({ "ok": 1 }))), Duration::from_secs(1)).await.unwrap();
        assert_eq!(echoed, Some(serde_json::json!({ "ok": 1 })));

        // Peer goes away → Disconnected → reconnect attempts (no pipe here, so they keep failing).
        agent.abort();
        tokio::time::timeout(Duration::from_secs(5), state.wait_for(|s| s.agent != ConnectionState::Connected)).await.unwrap().unwrap();
        tokio::time::timeout(Duration::from_secs(5), state.wait_for(|s| s.agent == ConnectionState::Connecting && s.attempts >= 2)).await.unwrap().unwrap();
        assert!(transport.pc_info().is_none());
        let err = transport.send(IpcEnvelope::request(names::auth::STATUS, None), Duration::from_millis(100)).await.unwrap_err();
        assert_eq!(err.code, clubshell_protocol::error::ErrorCode::AgentOffline);

        let _ = shutdown_tx.send_replace(true);
        tokio::time::timeout(Duration::from_secs(5), task).await.unwrap().unwrap();
        assert_eq!(transport.current_status().agent, ConnectionState::Disconnected);
        let seen = seen.lock();
        assert_eq!(seen[0], ConnectionState::Connecting);
        assert_eq!(seen[1], ConnectionState::Connected);
        assert_eq!(seen[2], ConnectionState::Disconnected);
        assert_eq!(*seen.last().unwrap(), ConnectionState::Disconnected);
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
