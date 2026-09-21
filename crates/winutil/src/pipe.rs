//! Async client for the Agent's named pipe `\\.\pipe\clubshell-agent` (IPC_PROTOCOL.md §1–§4).
//!
//! Framing is `[u32 length LE][UTF-8 JSON]` (max 4 MiB) via `clubshell_protocol::ipc::FrameReader`,
//! so the client works over any byte stream; the pipe is opened in byte mode and message mode is not
//! needed. The connection is split into a reader task (frames → pending requests / event broadcast),
//! a writer task (`mpsc<Bytes>` → pipe) and a heartbeat task (`sys.ping` every 5 s; three misses close
//! the connection, IPC_PROTOCOL.md §4). Requests are correlated by envelope id and each has its own
//! timeout. Reconnection with backoff is the caller's job (`shell.ipc.reconnectMinMs..MaxMs`).
//!
//! The core ([`PipeClient::from_stream`]) is platform-independent and tested over
//! `tokio::io::duplex`; only [`PipeClient::connect`] and `test_server::PipeServer` need Windows.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::Arc;
use std::time::Duration;

use bytes::Bytes;
use chrono::Utc;
use clubshell_protocol::commands::{names, SysPingRequest};
use clubshell_protocol::error::ProtocolError;
use clubshell_protocol::ipc::{encode_frame, FrameReader, IpcEnvelope, IpcKind};
use parking_lot::Mutex;
use serde::de::DeserializeOwned;
use serde::Serialize;
use serde_json::Value;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::sync::{broadcast, mpsc, oneshot, watch};
use tokio::task::JoinHandle;
use uuid::Uuid;

use crate::{Result, WinUtilError};

/// `shell.json → ipc.requestTimeoutMs` default.
pub const DEFAULT_REQUEST_TIMEOUT: Duration = Duration::from_millis(15_000);
/// `agent.json → ipc.heartbeatIntervalSec` default (IPC_PROTOCOL.md §4).
pub const HEARTBEAT_INTERVAL: Duration = Duration::from_secs(5);
/// Consecutive missed pongs that close the connection.
pub const HEARTBEAT_MAX_MISSES: u32 = 3;
/// Capacity of the event broadcast channel (a slow subscriber sees `Lagged`, never blocks the pipe).
pub const EVENT_BUFFER: usize = 256;

const WRITE_QUEUE: usize = 64;
const READ_CHUNK: usize = 64 * 1024;
#[cfg(windows)]
const CONNECT_RETRY: Duration = Duration::from_millis(50);

/// Why the connection ended.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CloseReason {
    /// [`PipeClient::close`] or drop.
    Shutdown,
    /// The Agent closed the pipe (read returned EOF).
    PeerClosed,
    /// [`HEARTBEAT_MAX_MISSES`] consecutive `sys.ping` without `sys.pong`.
    HeartbeatTimeout,
    /// Read/write failure.
    Io(String),
    /// Unrecoverable framing error (oversize frame).
    Protocol(String),
}

/// Connection lifecycle as published through [`PipeClient::state`].
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ConnectionState {
    Connected,
    Closed(CloseReason),
}

/// Client tuning; `Default` matches the documented protocol values.
#[derive(Clone, Debug)]
pub struct PipeOptions {
    /// Per-request response timeout (`shell.ipc.requestTimeoutMs`).
    pub request_timeout: Duration,
    pub heartbeat_interval: Duration,
    pub heartbeat_max_misses: u32,
    /// `false` disables the heartbeat task (diagnostic clients).
    pub heartbeat: bool,
    pub event_buffer: usize,
}

impl Default for PipeOptions {
    fn default() -> Self {
        Self {
            request_timeout: DEFAULT_REQUEST_TIMEOUT,
            heartbeat_interval: HEARTBEAT_INTERVAL,
            heartbeat_max_misses: HEARTBEAT_MAX_MISSES,
            heartbeat: true,
            event_buffer: EVENT_BUFFER,
        }
    }
}

/// Feeds `bytes` to `reader` and drains every complete envelope. A malformed JSON frame is logged and
/// skipped (the length prefix still delimits it); an oversize frame is returned as an error and the
/// connection must be closed.
pub fn drain_frames(reader: &mut FrameReader, bytes: &[u8]) -> Result<Vec<IpcEnvelope>> {
    reader.push(bytes);
    let mut out = Vec::new();
    loop {
        match reader.next_frame() {
            Ok(Some(env)) => out.push(env),
            Ok(None) => return Ok(out),
            Err(ProtocolError::Json(e)) => tracing::warn!(error = %e, "malformed IPC frame skipped"),
            Err(e) => return Err(e.into()),
        }
    }
}

/// Full pipe path for a short (`clubshell-agent`) or already full (`\\.\pipe\…`) name.
pub fn full_pipe_path(name: &str) -> String {
    if name.starts_with(r"\\") {
        name.to_owned()
    } else {
        clubshell_protocol::pipe_path(name)
    }
}

struct Shared {
    writer: mpsc::Sender<Bytes>,
    pending: Mutex<HashMap<Uuid, oneshot::Sender<IpcEnvelope>>>,
    events: broadcast::Sender<IpcEnvelope>,
    state: watch::Sender<ConnectionState>,
    closing: AtomicBool,
    ping_seq: AtomicI64,
    options: PipeOptions,
}

impl Shared {
    fn is_closed(&self) -> bool {
        self.closing.load(Ordering::Acquire)
    }

    /// First close wins; drops every pending sender so awaiting requests observe `Closed`.
    fn close(&self, reason: CloseReason) {
        if self.closing.swap(true, Ordering::AcqRel) {
            return;
        }
        tracing::info!(?reason, "pipe connection closed");
        let pending = std::mem::take(&mut *self.pending.lock());
        drop(pending);
        let _ = self.state.send_replace(ConnectionState::Closed(reason));
    }

    fn dispatch(&self, env: IpcEnvelope) {
        match env.kind {
            IpcKind::Response => {
                let waiter = self.pending.lock().remove(&env.id);
                match waiter {
                    Some(tx) => {
                        let _ = tx.send(env);
                    }
                    None => tracing::debug!(id = %env.id, name = %env.name, "response without a pending request (late or unknown)"),
                }
            }
            IpcKind::Event => {
                // No subscribers is not an error: the shell may not have wired listeners yet.
                let _ = self.events.send(env);
            }
            IpcKind::Request => tracing::warn!(name = %env.name, "agent sent a request over the shell pipe; ignored"),
        }
    }

    async fn request(&self, env: IpcEnvelope, timeout: Duration) -> Result<IpcEnvelope> {
        if self.is_closed() {
            return Err(WinUtilError::Closed);
        }
        let id = env.id;
        let bytes = Bytes::from(encode_frame(&env)?);
        let (tx, rx) = oneshot::channel();
        self.pending.lock().insert(id, tx);
        // A close racing with the insert above would leave the sender in the map forever.
        if self.is_closed() {
            self.pending.lock().remove(&id);
            return Err(WinUtilError::Closed);
        }
        if self.writer.send(bytes).await.is_err() {
            self.pending.lock().remove(&id);
            return Err(WinUtilError::Closed);
        }
        match tokio::time::timeout(timeout, rx).await {
            Ok(Ok(resp)) => Ok(resp),
            Ok(Err(_)) => Err(WinUtilError::Closed),
            Err(_) => {
                self.pending.lock().remove(&id);
                Err(WinUtilError::Timeout)
            }
        }
    }
}

async fn wait_closed(rx: &mut watch::Receiver<ConnectionState>) -> CloseReason {
    loop {
        let closed = match &*rx.borrow_and_update() {
            ConnectionState::Closed(r) => Some(r.clone()),
            ConnectionState::Connected => None,
        };
        if let Some(reason) = closed {
            return reason;
        }
        if rx.changed().await.is_err() {
            return CloseReason::Shutdown;
        }
    }
}

async fn reader_loop<R: AsyncRead + Unpin>(mut rd: R, shared: Arc<Shared>) -> CloseReason {
    let mut reader = FrameReader::new();
    let mut buf = vec![0u8; READ_CHUNK];
    let mut state = shared.state.subscribe();
    loop {
        let n = tokio::select! {
            res = rd.read(&mut buf) => match res {
                Ok(0) => return CloseReason::PeerClosed,
                Ok(n) => n,
                Err(e) => return CloseReason::Io(e.to_string()),
            },
            reason = wait_closed(&mut state) => return reason,
        };
        match drain_frames(&mut reader, &buf[..n]) {
            Ok(frames) => {
                for env in frames {
                    shared.dispatch(env);
                }
            }
            Err(e) => return CloseReason::Protocol(e.to_string()),
        }
    }
}

async fn writer_loop<W: AsyncWrite + Unpin>(mut wr: W, mut queue: mpsc::Receiver<Bytes>, shared: Arc<Shared>) -> CloseReason {
    let mut state = shared.state.subscribe();
    loop {
        tokio::select! {
            next = queue.recv() => match next {
                Some(bytes) => {
                    if let Err(e) = wr.write_all(&bytes).await {
                        return CloseReason::Io(e.to_string());
                    }
                }
                None => return CloseReason::Shutdown,
            },
            reason = wait_closed(&mut state) => {
                let _ = wr.shutdown().await;
                return reason;
            }
        }
    }
}

async fn heartbeat_loop(shared: Arc<Shared>) {
    let interval = shared.options.heartbeat_interval;
    let mut state = shared.state.subscribe();
    // First ping one interval after connect, so `auth.hello` normally precedes it.
    let mut ticker = tokio::time::interval_at(tokio::time::Instant::now() + interval, interval);
    ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    let mut misses = 0u32;
    loop {
        tokio::select! {
            _ = ticker.tick() => {}
            _ = wait_closed(&mut state) => return,
        }
        let seq = shared.ping_seq.fetch_add(1, Ordering::Relaxed) + 1;
        let ping = match IpcEnvelope::request_with(names::sys::PING, &SysPingRequest { seq, sent_at: Utc::now() }) {
            Ok(env) => env,
            Err(e) => {
                tracing::error!(error = %e, "cannot encode sys.ping");
                return;
            }
        };
        // Any answer, even an error envelope (e.g. `unauthorized` before hello), proves liveness.
        match shared.request(ping, interval).await {
            Ok(_) => misses = 0,
            Err(WinUtilError::Closed) => return,
            Err(e) => {
                misses += 1;
                tracing::warn!(seq, misses, error = %e, "sys.pong missed");
                if misses >= shared.options.heartbeat_max_misses {
                    shared.close(CloseReason::HeartbeatTimeout);
                    return;
                }
            }
        }
    }
}

/// Async client for one pipe connection. Dropping it closes the connection.
pub struct PipeClient {
    shared: Arc<Shared>,
    tasks: Vec<JoinHandle<()>>,
}

impl PipeClient {
    /// Wraps an already connected duplex stream (a `NamedPipeClient`, a `tokio::io::duplex` half in
    /// tests, …) and spawns the reader / writer / heartbeat tasks on the current Tokio runtime.
    pub fn from_stream<S>(stream: S, options: PipeOptions) -> Self
    where
        S: AsyncRead + AsyncWrite + Send + Unpin + 'static,
    {
        let (writer_tx, writer_rx) = mpsc::channel::<Bytes>(WRITE_QUEUE);
        let (events, _) = broadcast::channel(options.event_buffer.max(1));
        let (state, _) = watch::channel(ConnectionState::Connected);
        let heartbeat = options.heartbeat;
        let shared = Arc::new(Shared {
            writer: writer_tx,
            pending: Mutex::new(HashMap::new()),
            events,
            state,
            closing: AtomicBool::new(false),
            ping_seq: AtomicI64::new(0),
            options,
        });
        let (rd, wr) = tokio::io::split(stream);
        let reader = {
            let s = Arc::clone(&shared);
            tokio::spawn(async move {
                let reason = reader_loop(rd, Arc::clone(&s)).await;
                s.close(reason);
            })
        };
        let writer = {
            let s = Arc::clone(&shared);
            tokio::spawn(async move {
                let reason = writer_loop(wr, writer_rx, Arc::clone(&s)).await;
                s.close(reason);
            })
        };
        let mut tasks = vec![reader, writer];
        if heartbeat {
            tasks.push(tokio::spawn(heartbeat_loop(Arc::clone(&shared))));
        }
        Self { shared, tasks }
    }

    /// Opens `\\.\pipe\<name>` (a short name is expanded) with default options.
    pub async fn connect(name: &str, timeout: Duration) -> Result<Self> {
        Self::connect_with(name, timeout, PipeOptions::default()).await
    }

    /// Opens the pipe, retrying every 50 ms while every instance is busy (`ERROR_PIPE_BUSY`, the
    /// `WaitNamedPipe` case) or the Agent has not created it yet (`ERROR_FILE_NOT_FOUND`), until
    /// `timeout` elapses ([`WinUtilError::Timeout`]). Other errors are returned immediately.
    #[cfg(windows)]
    pub async fn connect_with(name: &str, timeout: Duration, options: PipeOptions) -> Result<Self> {
        use tokio::net::windows::named_pipe::ClientOptions;
        use windows::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_PIPE_BUSY};

        let path = full_pipe_path(name);
        let deadline = tokio::time::Instant::now() + timeout;
        loop {
            match ClientOptions::new().open(&path) {
                Ok(pipe) => {
                    tracing::info!(path = %path, "pipe connected");
                    return Ok(Self::from_stream(pipe, options));
                }
                Err(e) => {
                    let code = e.raw_os_error();
                    let retryable = code == Some(ERROR_PIPE_BUSY.0 as i32) || code == Some(ERROR_FILE_NOT_FOUND.0 as i32);
                    if !retryable {
                        return Err(WinUtilError::Io(e));
                    }
                    if tokio::time::Instant::now() >= deadline {
                        tracing::warn!(path = %path, error = %e, "pipe connect timed out");
                        return Err(WinUtilError::Timeout);
                    }
                    tokio::time::sleep(CONNECT_RETRY).await;
                }
            }
        }
    }

    /// Non-Windows stub: always [`WinUtilError::Unsupported`].
    #[cfg(not(windows))]
    pub async fn connect_with(_name: &str, _timeout: Duration, _options: PipeOptions) -> Result<Self> {
        Err(WinUtilError::Unsupported)
    }

    pub fn options(&self) -> &PipeOptions {
        &self.shared.options
    }

    /// Sends a request envelope and awaits the response with the same id (default timeout). The
    /// response is returned as-is: an error envelope is `Ok(env)` with `env.error.is_some()`.
    pub async fn request(&self, env: IpcEnvelope) -> Result<IpcEnvelope> {
        self.shared.request(env, self.shared.options.request_timeout).await
    }

    /// [`request`](Self::request) with an explicit timeout (`games.launch` uses 120 s).
    pub async fn request_timeout(&self, env: IpcEnvelope, timeout: Duration) -> Result<IpcEnvelope> {
        self.shared.request(env, timeout).await
    }

    fn check(mut resp: IpcEnvelope) -> Result<IpcEnvelope> {
        match resp.error.take() {
            Some(err) => Err(err.into()),
            None => Ok(resp),
        }
    }

    /// Typed request → typed response; an error envelope becomes [`WinUtilError::Ipc`] and a missing
    /// payload a `validation` error.
    pub async fn call<Req, Resp>(&self, name: &str, payload: &Req) -> Result<Resp>
    where
        Req: Serialize,
        Resp: DeserializeOwned,
    {
        let resp = Self::check(self.request(IpcEnvelope::request_with(name, payload)?).await?)?;
        resp.require_payload::<Resp>().map_err(WinUtilError::from)
    }

    /// Like [`call`](Self::call) for responses whose payload may be `null` (`session.get`).
    pub async fn call_optional<Req, Resp>(&self, name: &str, payload: &Req) -> Result<Option<Resp>>
    where
        Req: Serialize,
        Resp: DeserializeOwned,
    {
        let resp = Self::check(self.request(IpcEnvelope::request_with(name, payload)?).await?)?;
        resp.payload_as::<Resp>().map_err(WinUtilError::from)
    }

    /// Request with a raw (or absent) JSON payload; error envelopes become [`WinUtilError::Ipc`].
    pub async fn call_raw(&self, name: &str, payload: Option<Value>) -> Result<IpcEnvelope> {
        Self::check(self.request(IpcEnvelope::request(name, payload)).await?)
    }

    /// Agent → Shell events (`kind = event`). Each receiver gets every event from subscription on.
    pub fn subscribe(&self) -> broadcast::Receiver<IpcEnvelope> {
        self.shared.events.subscribe()
    }

    /// Connection state watch (`Connected` until closed).
    pub fn state(&self) -> watch::Receiver<ConnectionState> {
        self.shared.state.subscribe()
    }

    pub fn is_connected(&self) -> bool {
        !self.shared.is_closed()
    }

    /// Resolves once the connection is closed, with the reason.
    pub async fn closed(&self) -> CloseReason {
        let mut rx = self.shared.state.subscribe();
        wait_closed(&mut rx).await
    }

    /// Graceful shutdown: fails pending requests with [`WinUtilError::Closed`], flushes the writer
    /// and closes the pipe. Idempotent.
    pub fn close(&self) {
        self.shared.close(CloseReason::Shutdown);
    }
}

impl Drop for PipeClient {
    fn drop(&mut self) {
        self.close();
        for task in &self.tasks {
            task.abort();
        }
    }
}

/// In-process echo server used by unit tests and `tests/shell-rs` (feature `test-server`).
#[cfg(feature = "test-server")]
pub mod test_server {
    use super::*;
    use clubshell_protocol::commands::SysPongResponse;
    use clubshell_protocol::pc::ConnectivityState;

    /// Response the echo server gives to `req`: `sys.ping` → a well-formed `sys.pong`, anything else
    /// → its own payload echoed back.
    pub fn echo_reply(req: &IpcEnvelope) -> Result<IpcEnvelope> {
        if req.name == names::sys::PING {
            let ping: SysPingRequest = req.require_payload()?;
            let pong = SysPongResponse { seq: ping.seq, sent_at: ping.sent_at, received_at: Utc::now(), connectivity: ConnectivityState::Online };
            return IpcEnvelope::reply_to_with(req, &pong).map_err(WinUtilError::from);
        }
        Ok(IpcEnvelope::reply_to(req, req.payload.clone()))
    }

    async fn next_event(events: &mut Option<mpsc::Receiver<IpcEnvelope>>) -> Option<IpcEnvelope> {
        match events {
            Some(rx) => rx.recv().await,
            None => std::future::pending().await,
        }
    }

    /// Serves one connection until the peer closes it: answers every request with [`echo_reply`] and
    /// forwards envelopes received on `events` (closing that channel just stops event injection).
    pub async fn serve_echo<S>(stream: S, mut events: Option<mpsc::Receiver<IpcEnvelope>>) -> Result<()>
    where
        S: AsyncRead + AsyncWrite + Unpin,
    {
        let (mut rd, mut wr) = tokio::io::split(stream);
        let mut reader = FrameReader::new();
        let mut buf = vec![0u8; READ_CHUNK];
        loop {
            tokio::select! {
                res = rd.read(&mut buf) => {
                    let n = res?;
                    if n == 0 {
                        return Ok(());
                    }
                    for env in drain_frames(&mut reader, &buf[..n])? {
                        if env.kind != IpcKind::Request {
                            continue;
                        }
                        let reply = echo_reply(&env)?;
                        wr.write_all(&encode_frame(&reply)?).await?;
                    }
                }
                ev = next_event(&mut events) => match ev {
                    Some(env) => wr.write_all(&encode_frame(&env)?).await?,
                    None => events = None,
                },
            }
        }
    }

    /// Minimal named-pipe listener for integration tests (byte mode, local clients only).
    #[cfg(windows)]
    pub struct PipeServer {
        path: String,
        next: Option<tokio::net::windows::named_pipe::NamedPipeServer>,
    }

    #[cfg(windows)]
    impl PipeServer {
        /// Creates the first pipe instance at `\\.\pipe\<name>`.
        pub fn bind(name: &str) -> Result<Self> {
            use tokio::net::windows::named_pipe::ServerOptions;
            let path = full_pipe_path(name);
            let next = ServerOptions::new().first_pipe_instance(true).create(&path)?;
            Ok(Self { path, next: Some(next) })
        }

        pub fn path(&self) -> &str {
            &self.path
        }

        /// Waits for the next client and returns its connected stream (pass it to [`serve_echo`]).
        pub async fn accept(&mut self) -> Result<tokio::net::windows::named_pipe::NamedPipeServer> {
            use tokio::net::windows::named_pipe::ServerOptions;
            let server = match self.next.take() {
                Some(s) => s,
                None => ServerOptions::new().create(&self.path)?,
            };
            server.connect().await?;
            self.next = ServerOptions::new().create(&self.path).ok();
            Ok(server)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn opts(heartbeat: bool) -> PipeOptions {
        PipeOptions {
            request_timeout: Duration::from_millis(200),
            heartbeat_interval: Duration::from_millis(30),
            heartbeat_max_misses: 2,
            heartbeat,
            event_buffer: 8,
        }
    }

    #[test]
    fn drain_frames_reassembles_partial_chunks() {
        let a = IpcEnvelope::request(names::auth::STATUS, None);
        let b = IpcEnvelope::event(names::events::SESSION_UPDATED, Some(json!({ "a": 1 })));
        let mut stream = encode_frame(&a).unwrap();
        stream.extend(encode_frame(&b).unwrap());
        let mut reader = FrameReader::new();
        let mut out = Vec::new();
        for chunk in stream.chunks(3) {
            out.extend(drain_frames(&mut reader, chunk).unwrap());
        }
        assert_eq!(out, vec![a, b]);
        assert_eq!(reader.pending(), 0);

        // Oversize prefix is fatal.
        let mut reader = FrameReader::new();
        let err = drain_frames(&mut reader, &(IpcEnvelope::MAX_FRAME_BYTES as u32 + 1).to_le_bytes()).unwrap_err();
        assert!(matches!(err, WinUtilError::Frame(ProtocolError::FrameTooLarge { .. })));
    }

    #[test]
    fn pipe_path_expansion() {
        assert_eq!(full_pipe_path("clubshell-agent"), r"\\.\pipe\clubshell-agent");
        assert_eq!(full_pipe_path(r"\\.\pipe\custom"), r"\\.\pipe\custom");
    }

    #[cfg(feature = "test-server")]
    #[tokio::test]
    async fn request_response_typed_calls_and_events() {
        let (a, b) = tokio::io::duplex(64 * 1024);
        let (ev_tx, ev_rx) = mpsc::channel(8);
        let server = tokio::spawn(test_server::serve_echo(b, Some(ev_rx)));
        let client = PipeClient::from_stream(a, opts(true));
        let mut events = client.subscribe();

        let resp = client.request(IpcEnvelope::request(names::games::RUNNING, Some(json!({ "x": 1 })))).await.unwrap();
        assert_eq!(resp.kind, IpcKind::Response);
        assert_eq!(resp.name, names::games::RUNNING);
        assert_eq!(resp.payload, Some(json!({ "x": 1 })));

        #[derive(Serialize, serde::Deserialize, PartialEq, Debug)]
        struct P {
            x: i32,
        }
        let p: P = client.call(names::games::RUNNING, &P { x: 7 }).await.unwrap();
        assert_eq!(p, P { x: 7 });
        let none: Option<P> = client.call_optional(names::session::GET, &()).await.unwrap();
        assert_eq!(none, None);
        let raw = client.call_raw(names::auth::STATUS, None).await.unwrap();
        assert_eq!(raw.payload, None);

        // The echo server answers pings, so the heartbeat keeps the connection alive.
        tokio::time::sleep(Duration::from_millis(150)).await;
        assert!(client.is_connected());
        assert!(client.shared.ping_seq.load(Ordering::Relaxed) >= 1);

        ev_tx.send(IpcEnvelope::event(names::events::SESSION_UPDATED, Some(json!({ "a": 1 })))).await.unwrap();
        let ev = tokio::time::timeout(Duration::from_secs(2), events.recv()).await.unwrap().unwrap();
        assert_eq!(ev.kind, IpcKind::Event);
        assert_eq!(ev.name, names::events::SESSION_UPDATED);

        client.close();
        assert_eq!(client.closed().await, CloseReason::Shutdown);
        assert!(matches!(client.request(IpcEnvelope::request(names::auth::STATUS, None)).await, Err(WinUtilError::Closed)));
        drop(client);
        tokio::time::timeout(Duration::from_secs(2), server).await.unwrap().unwrap().unwrap();
    }

    #[tokio::test]
    async fn request_times_out_without_answer() {
        let (a, mut b) = tokio::io::duplex(4096);
        let sink = tokio::spawn(async move {
            let mut buf = [0u8; 1024];
            while let Ok(n) = b.read(&mut buf).await {
                if n == 0 {
                    break;
                }
            }
        });
        let client = PipeClient::from_stream(a, opts(false));
        let err = client.request(IpcEnvelope::request(names::auth::STATUS, None)).await.unwrap_err();
        assert!(matches!(err, WinUtilError::Timeout));
        assert!(client.is_connected(), "a timed-out request does not close the connection");
        assert!(client.shared.pending.lock().is_empty(), "timed-out waiter is removed");
        drop(client);
        let _ = tokio::time::timeout(Duration::from_secs(2), sink).await;
    }

    #[tokio::test]
    async fn heartbeat_misses_close_connection() {
        let (a, mut b) = tokio::io::duplex(4096);
        let sink = tokio::spawn(async move {
            let mut buf = [0u8; 1024];
            while let Ok(n) = b.read(&mut buf).await {
                if n == 0 {
                    break;
                }
            }
        });
        let client = PipeClient::from_stream(a, opts(true));
        let reason = tokio::time::timeout(Duration::from_secs(3), client.closed()).await.unwrap();
        assert_eq!(reason, CloseReason::HeartbeatTimeout);
        assert!(!client.is_connected());
        assert_eq!(*client.state().borrow(), ConnectionState::Closed(CloseReason::HeartbeatTimeout));
        drop(client);
        let _ = tokio::time::timeout(Duration::from_secs(2), sink).await;
    }

    #[tokio::test]
    async fn peer_close_fails_pending_requests() {
        let (a, b) = tokio::io::duplex(4096);
        let client = PipeClient::from_stream(a, opts(false));
        let pending = tokio::spawn({
            let shared = Arc::clone(&client.shared);
            async move { shared.request(IpcEnvelope::request(names::auth::STATUS, None), Duration::from_secs(5)).await }
        });
        tokio::time::sleep(Duration::from_millis(20)).await;
        drop(b);
        assert!(matches!(pending.await.unwrap(), Err(WinUtilError::Closed)));
        assert_eq!(client.closed().await, CloseReason::PeerClosed);
    }
}
