//! Integration tests for `clubshell_winutil::pipe::PipeClient` over a real Windows named pipe.
//!
//! Every test spins up an in-process server (`tokio::net::windows::named_pipe::ServerOptions`) on a
//! unique name (`clubshell-test-<pid>-<n>`) that speaks the IPC_PROTOCOL.md §1 framing
//! (`[u32 length LE][UTF-8 JSON]`, encoded/decoded with `clubshell_protocol::ipc`), answers
//! `auth.hello` / `sys.ping`, and lets the test inject raw outgoing frames (events, garbage).
//!
//! The pipe itself only exists on Windows; elsewhere the single test asserts the documented
//! `Unsupported` stub so the target still builds and passes on Linux CI.

#[cfg(not(windows))]
#[tokio::test]
async fn connect_is_unsupported_off_windows() {
    use clubshell_winutil::{PipeClient, WinUtilError};
    let err = PipeClient::connect("clubshell-test", std::time::Duration::from_millis(10)).await.err().expect("must not connect");
    assert!(matches!(err, WinUtilError::Unsupported), "{err}");
}

#[cfg(windows)]
mod windows_pipe {
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;
    use std::time::Duration;

    use anyhow::Context;
    use clubshell_protocol::commands::{names, AuthHelloRequest, AuthHelloResponse, SysPingRequest};
    use clubshell_protocol::ipc::{decode_frame, encode_frame, FrameReader, IpcEnvelope, IpcKind};
    use clubshell_protocol::user::Locale;
    use clubshell_protocol::MAX_FRAME_BYTES;
    use clubshell_winutil::pipe::{drain_frames, full_pipe_path, PipeClient, PipeOptions};
    use clubshell_winutil::{CloseReason, ConnectionState, WinUtilError};
    use serde_json::{json, Value};
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
    use tokio::sync::mpsc;
    use tokio::task::JoinHandle;

    static NEXT_PIPE: AtomicUsize = AtomicUsize::new(0);

    /// Unique short pipe name per test (several test binaries may run in parallel).
    fn pipe_name() -> String {
        format!("clubshell-test-{}-{}", std::process::id(), NEXT_PIPE.fetch_add(1, Ordering::Relaxed))
    }

    fn opts(request_timeout_ms: u64, heartbeat: bool) -> PipeOptions {
        PipeOptions {
            request_timeout: Duration::from_millis(request_timeout_ms),
            heartbeat_interval: Duration::from_millis(60),
            heartbeat_max_misses: 3,
            heartbeat,
            event_buffer: 16,
        }
    }

    const CONNECT_TIMEOUT: Duration = Duration::from_secs(3);
    const WAIT: Duration = Duration::from_secs(5);

    // ───────────────────────────── Test server ─────────────────────────────

    /// Decides what the server does with one request; `out` carries replies (possibly delayed, from a
    /// spawned task) to the connection's writer.
    type Handler = Arc<dyn Fn(IpcEnvelope, mpsc::Sender<IpcEnvelope>) + Send + Sync>;

    fn hello_payload() -> Value {
        json!({
            "agentVersion": "1.0.0-test",
            "protocol": IpcEnvelope::CURRENT_VERSION,
            "pcId": "6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11",
            "pcName": "PC-TEST",
            "zone": "Standard",
            "serverOnline": true,
            "policyVersion": 3,
            "serverTime": "2026-09-21T10:00:00.000Z",
            "capabilities": ["accountPool"],
            "kioskUser": "club"
        })
    }

    /// Reply of the reference server: `auth.hello` → hello response, `sys.ping` → `sys.pong` that mirrors
    /// `seq`/`sentAt`, everything else → the request payload echoed back (`None` for a withheld reply).
    fn default_reply(req: &IpcEnvelope) -> Option<IpcEnvelope> {
        match req.name.as_str() {
            names::auth::HELLO => Some(IpcEnvelope::reply_to(req, Some(hello_payload()))),
            names::sys::PING => {
                let ping: SysPingRequest = req.require_payload().ok()?;
                let sent_at = req.payload.as_ref().and_then(|p| p.get("sentAt").cloned()).unwrap_or(Value::Null);
                Some(IpcEnvelope::reply_to(
                    req,
                    Some(json!({ "seq": ping.seq, "sentAt": sent_at, "receivedAt": sent_at, "connectivity": "online" })),
                ))
            }
            _ => Some(IpcEnvelope::reply_to(req, req.payload.clone())),
        }
    }

    /// Normalizes an envelope to its wire form (timestamps carry 3 fractional digits on the wire, so an
    /// envelope built with `Utc::now()` only compares equal to what the peer receives after a round trip).
    fn wire(env: IpcEnvelope) -> IpcEnvelope {
        decode_frame(&encode_frame(&env).expect("encode")).expect("decode")
    }

    /// Handler that answers immediately with [`default_reply`].
    fn answer_all() -> Handler {
        Arc::new(|req: IpcEnvelope, out: mpsc::Sender<IpcEnvelope>| {
            if let Some(reply) = default_reply(&req) {
                let _ = out.try_send(reply);
            }
        })
    }

    /// One accepted connection: reads frames, hands requests to `handler`, writes whatever arrives on the
    /// reply channel or on `raw` (pre-encoded bytes injected by the test). Returns when the client hangs up.
    async fn serve_connection(
        stream: NamedPipeServer,
        handler: &Handler,
        raw: &mut mpsc::Receiver<Vec<u8>>,
    ) -> anyhow::Result<()> {
        let (mut rd, mut wr) = tokio::io::split(stream);
        let (out_tx, mut out_rx) = mpsc::channel::<IpcEnvelope>(64);
        let mut reader = FrameReader::new();
        let mut buf = vec![0u8; 64 * 1024];
        loop {
            tokio::select! {
                res = rd.read(&mut buf) => {
                    let n = res.context("server read")?;
                    if n == 0 {
                        return Ok(());
                    }
                    for env in drain_frames(&mut reader, &buf[..n]).context("server framing")? {
                        if env.kind == IpcKind::Request {
                            handler(env, out_tx.clone());
                        }
                    }
                }
                Some(env) = out_rx.recv() => {
                    wr.write_all(&encode_frame(&env)?).await.context("server write")?;
                }
                Some(bytes) = raw.recv() => {
                    wr.write_all(&bytes).await.context("server raw write")?;
                }
            }
        }
    }

    /// In-process pipe server bound to a unique name. One instance at a time: while a client is being
    /// served no free instance exists, so a second `CreateFile` gets `ERROR_PIPE_BUSY` until the first
    /// client disconnects (the retry path of `PipeClient::connect_with`).
    struct TestServer {
        name: String,
        task: JoinHandle<()>,
        /// Raw frames pushed to the currently served connection (events, deliberately broken frames).
        raw: mpsc::Sender<Vec<u8>>,
        /// Connections accepted so far.
        accepted: Arc<AtomicUsize>,
    }

    impl TestServer {
        fn start(handler: Handler) -> anyhow::Result<Self> {
            Self::start_named(pipe_name(), handler)
        }

        fn start_named(name: String, handler: Handler) -> anyhow::Result<Self> {
            let path = full_pipe_path(&name);
            let first = ServerOptions::new().first_pipe_instance(true).create(&path).context("create first instance")?;
            let (raw_tx, mut raw_rx) = mpsc::channel::<Vec<u8>>(16);
            let accepted = Arc::new(AtomicUsize::new(0));
            let counter = Arc::clone(&accepted);
            let task = tokio::spawn(async move {
                let mut next = Some(first);
                loop {
                    let instance = match next.take() {
                        Some(i) => i,
                        None => match ServerOptions::new().create(&path) {
                            Ok(i) => i,
                            Err(_) => return,
                        },
                    };
                    if instance.connect().await.is_err() {
                        return;
                    }
                    counter.fetch_add(1, Ordering::SeqCst);
                    // A client hanging up is the normal end of a connection; keep accepting.
                    let _ = serve_connection(instance, &handler, &mut raw_rx).await;
                }
            });
            Ok(Self { name, task, raw: raw_tx, accepted })
        }

        fn name(&self) -> &str {
            &self.name
        }

        async fn send_event(&self, env: &IpcEnvelope) -> anyhow::Result<()> {
            self.raw.send(encode_frame(env)?).await.context("server gone")
        }

        fn accepted(&self) -> usize {
            self.accepted.load(Ordering::SeqCst)
        }

        /// Drops every pipe instance: the served client sees EOF / broken pipe.
        fn shutdown(self) {
            self.task.abort();
        }
    }

    impl Drop for TestServer {
        fn drop(&mut self) {
            self.task.abort();
        }
    }

    // ───────────────────────────── Client helpers ─────────────────────────────

    fn hello_request() -> AuthHelloRequest {
        AuthHelloRequest {
            shell_token: "9f".repeat(32),
            shell_version: "1.0.0-test".into(),
            pid: std::process::id() as i32,
            wts_session_id: 1,
            locale: Locale::En,
            capabilities: vec!["gamepad".into()],
        }
    }

    async fn connect(server: &TestServer, options: PipeOptions) -> anyhow::Result<PipeClient> {
        PipeClient::connect_with(server.name(), CONNECT_TIMEOUT, options).await.context("connect")
    }

    async fn hello(client: &PipeClient) -> anyhow::Result<AuthHelloResponse> {
        client.call::<_, AuthHelloResponse>(names::auth::HELLO, &hello_request()).await.context("auth.hello")
    }

    // ───────────────────────────── Tests ─────────────────────────────

    #[tokio::test]
    async fn connect_hello_and_heartbeat_over_a_real_pipe() -> anyhow::Result<()> {
        let pings = Arc::new(AtomicUsize::new(0));
        let seen = Arc::clone(&pings);
        let server = TestServer::start(Arc::new(move |req: IpcEnvelope, out: mpsc::Sender<IpcEnvelope>| {
            if req.name == names::sys::PING {
                seen.fetch_add(1, Ordering::SeqCst);
            }
            if let Some(reply) = default_reply(&req) {
                let _ = out.try_send(reply);
            }
        }))?;

        let client = connect(&server, opts(1_000, true)).await?;
        assert!(client.is_connected());
        assert_eq!(*client.state().borrow(), ConnectionState::Connected);

        let res = hello(&client).await?;
        assert_eq!(server.accepted(), 1);
        assert_eq!(res.pc_name, "PC-TEST");
        assert_eq!(res.protocol, IpcEnvelope::CURRENT_VERSION);
        assert_eq!(res.kiosk_user, "club");
        assert_eq!(res.capabilities, vec!["accountPool".to_owned()]);

        // The raw response keeps the request id and name (correlation contract of IPC_PROTOCOL.md §2).
        let req = IpcEnvelope::request(names::auth::STATUS, None);
        let id = req.id;
        let resp = client.request(req).await?;
        assert_eq!(resp.id, id);
        assert_eq!(resp.kind, IpcKind::Response);
        assert_eq!(resp.name, names::auth::STATUS);
        assert_eq!(resp.payload, None);
        assert!(!resp.is_error());

        // Heartbeat: the server answers `sys.ping` with `sys.pong`, so the connection stays up.
        tokio::time::sleep(Duration::from_millis(300)).await;
        assert!(client.is_connected(), "pongs keep the connection alive");
        assert!(pings.load(Ordering::SeqCst) >= 2, "expected several pings at a 60 ms interval");

        client.close();
        assert_eq!(tokio::time::timeout(WAIT, client.closed()).await?, CloseReason::Shutdown);
        assert_eq!(*client.state().borrow(), ConnectionState::Closed(CloseReason::Shutdown));
        assert!(matches!(client.request(IpcEnvelope::request(names::auth::STATUS, None)).await, Err(WinUtilError::Closed)));
        Ok(())
    }

    #[tokio::test]
    async fn concurrent_requests_are_correlated_by_id() -> anyhow::Result<()> {
        // Replies are delayed by a per-request amount so they come back out of order.
        let server = TestServer::start(Arc::new(|req: IpcEnvelope, out: mpsc::Sender<IpcEnvelope>| {
            let n = req.payload.as_ref().and_then(|p| p.get("n")).and_then(Value::as_u64).unwrap_or(0);
            let reply = IpcEnvelope::reply_to(&req, Some(json!({ "n": n, "echo": true })));
            tokio::spawn(async move {
                tokio::time::sleep(Duration::from_millis((20 - n % 20) * 3)).await;
                let _ = out.send(reply).await;
            });
        }))?;
        let client = Arc::new(connect(&server, opts(2_000, false)).await?);

        let mut tasks = Vec::new();
        for n in 0..20u64 {
            let c = Arc::clone(&client);
            tasks.push(tokio::spawn(async move {
                let req = IpcEnvelope::request(names::games::LIST, Some(json!({ "n": n })));
                let id = req.id;
                let resp = c.request(req).await?;
                anyhow::ensure!(resp.id == id, "response id must match request id");
                anyhow::ensure!(resp.name == names::games::LIST);
                let got = resp.payload.as_ref().and_then(|p| p.get("n")).and_then(Value::as_u64);
                anyhow::ensure!(got == Some(n), "request {n} got payload {:?}", resp.payload);
                Ok::<u64, anyhow::Error>(n)
            }));
        }
        let mut done = Vec::new();
        for t in tasks {
            done.push(tokio::time::timeout(WAIT, t).await??.context("request task")?);
        }
        done.sort_unstable();
        assert_eq!(done, (0..20).collect::<Vec<u64>>());
        assert!(client.is_connected());
        Ok(())
    }

    #[tokio::test]
    async fn events_reach_every_subscriber() -> anyhow::Result<()> {
        let server = TestServer::start(answer_all())?;
        let client = connect(&server, opts(1_000, false)).await?;
        let mut a = client.subscribe();
        let mut b = client.subscribe();
        hello(&client).await?;

        let event = wire(IpcEnvelope::event(names::events::SESSION_UPDATED, Some(json!({ "state": "active", "secondsLeft": 120 }))));
        server.send_event(&event).await?;
        let got_a = tokio::time::timeout(WAIT, a.recv()).await??;
        let got_b = tokio::time::timeout(WAIT, b.recv()).await??;
        assert_eq!(got_a, event);
        assert_eq!(got_b, event);

        // A response envelope is never broadcast as an event, and a late subscriber only sees new events.
        let mut late = client.subscribe();
        client.request(IpcEnvelope::request(names::auth::STATUS, None)).await?;
        let second = wire(IpcEnvelope::event(names::events::WALLET_UPDATED, Some(json!({ "amount": 1 }))));
        server.send_event(&second).await?;
        assert_eq!(tokio::time::timeout(WAIT, late.recv()).await??, second);
        assert_eq!(tokio::time::timeout(WAIT, a.recv()).await??, second);
        Ok(())
    }

    #[tokio::test]
    async fn request_times_out_when_the_server_withholds_the_response() -> anyhow::Result<()> {
        let server = TestServer::start(Arc::new(|req: IpcEnvelope, out: mpsc::Sender<IpcEnvelope>| {
            if req.name == names::games::LIST {
                return; // swallowed on purpose
            }
            if let Some(reply) = default_reply(&req) {
                let _ = out.try_send(reply);
            }
        }))?;
        let client = connect(&server, opts(200, false)).await?;
        hello(&client).await?;

        let started = tokio::time::Instant::now();
        let err = client.request(IpcEnvelope::request(names::games::LIST, None)).await.unwrap_err();
        assert!(matches!(err, WinUtilError::Timeout), "{err}");
        let elapsed = started.elapsed();
        assert!(elapsed >= Duration::from_millis(180) && elapsed < Duration::from_secs(2), "elapsed {elapsed:?}");

        // An explicit per-request timeout wins over the default one.
        let err = client.request_timeout(IpcEnvelope::request(names::games::LIST, None), Duration::from_millis(50)).await.unwrap_err();
        assert!(matches!(err, WinUtilError::Timeout), "{err}");

        // The connection survives a timed-out request and keeps serving answered ones.
        assert!(client.is_connected());
        assert_eq!(hello(&client).await?.pc_name, "PC-TEST");
        Ok(())
    }

    #[tokio::test]
    async fn server_drop_closes_the_connection_and_fails_pending_requests() -> anyhow::Result<()> {
        let server = TestServer::start(Arc::new(|req: IpcEnvelope, out: mpsc::Sender<IpcEnvelope>| {
            if req.name == names::games::LIST {
                return; // stays pending until the server goes away
            }
            if let Some(reply) = default_reply(&req) {
                let _ = out.try_send(reply);
            }
        }))?;
        let client = Arc::new(connect(&server, opts(10_000, false)).await?);
        let mut state = client.state();
        assert_eq!(*state.borrow_and_update(), ConnectionState::Connected);
        hello(&client).await?;

        let pending = {
            let c = Arc::clone(&client);
            tokio::spawn(async move { c.request(IpcEnvelope::request(names::games::LIST, None)).await })
        };
        tokio::time::sleep(Duration::from_millis(50)).await;
        assert!(client.is_connected());

        server.shutdown();

        let reason = tokio::time::timeout(WAIT, client.closed()).await?;
        // mio reports a peer that closed its end as EOF (`ERROR_BROKEN_PIPE` → 0 bytes); any other
        // surfacing of the broken pipe is an I/O close. Both mean "the Agent went away".
        assert!(matches!(reason, CloseReason::PeerClosed | CloseReason::Io(_)), "{reason:?}");
        assert!(!client.is_connected());
        tokio::time::timeout(WAIT, state.changed()).await??;
        assert_eq!(*state.borrow_and_update(), ConnectionState::Closed(reason.clone()));
        assert_eq!(*client.state().borrow(), ConnectionState::Closed(reason));

        let outcome = tokio::time::timeout(WAIT, pending).await??;
        assert!(matches!(outcome, Err(WinUtilError::Closed)), "{outcome:?}");
        assert!(matches!(client.request(IpcEnvelope::request(names::auth::STATUS, None)).await, Err(WinUtilError::Closed)));
        // Closing again is a no-op that keeps the first reason.
        client.close();
        assert!(matches!(&*client.state().borrow(), ConnectionState::Closed(r) if *r != CloseReason::Shutdown));
        Ok(())
    }

    #[tokio::test]
    async fn busy_pipe_is_retried_until_an_instance_frees_up() -> anyhow::Result<()> {
        let server = TestServer::start(answer_all())?;
        let first = connect(&server, opts(1_000, false)).await?;
        hello(&first).await?;
        assert_eq!(server.accepted(), 1);

        // The single instance is taken: a short connect attempt keeps hitting ERROR_PIPE_BUSY and gives up.
        let err = PipeClient::connect_with(server.name(), Duration::from_millis(150), opts(1_000, false)).await.err().expect("busy");
        assert!(matches!(err, WinUtilError::Timeout), "{err}");

        // A patient attempt stays in the retry loop …
        let second = PipeClient::connect_with(server.name(), CONNECT_TIMEOUT, opts(1_000, false));
        tokio::pin!(second);
        assert!(tokio::time::timeout(Duration::from_millis(300), &mut second).await.is_err(), "must still be waiting while busy");
        assert_eq!(server.accepted(), 1);

        // … and succeeds once the first client hangs up and the server creates a fresh instance.
        drop(first);
        let second = tokio::time::timeout(WAIT, second).await??;
        assert_eq!(hello(&second).await?.pc_name, "PC-TEST");
        assert_eq!(server.accepted(), 2);
        Ok(())
    }

    #[tokio::test]
    async fn connect_waits_for_the_server_to_appear() -> anyhow::Result<()> {
        // No server yet: ERROR_FILE_NOT_FOUND is retried until the deadline …
        let missing = pipe_name();
        let started = tokio::time::Instant::now();
        let err = PipeClient::connect_with(&missing, Duration::from_millis(120), opts(1_000, false)).await.err().expect("no server");
        assert!(matches!(err, WinUtilError::Timeout), "{err}");
        assert!(started.elapsed() >= Duration::from_millis(100));

        // … and a server that shows up during the retry window is connected to.
        let name = pipe_name();
        let server_name = name.clone();
        let handler = answer_all();
        let starter = tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(200)).await;
            TestServer::start_named(server_name, handler)
        });
        let client = PipeClient::connect_with(&name, CONNECT_TIMEOUT, opts(1_000, false)).await?;
        let server = starter.await??;
        assert_eq!(hello(&client).await?.zone, "Standard");
        assert_eq!(server.accepted(), 1);
        Ok(())
    }

    #[tokio::test]
    async fn oversized_frames_are_rejected() -> anyhow::Result<()> {
        let server = TestServer::start(answer_all())?;
        let client = connect(&server, opts(1_000, false)).await?;
        hello(&client).await?;

        // Outbound: a request that would exceed the limit never hits the wire and does not close anything.
        let huge = IpcEnvelope::request(names::chat::SEND, Some(json!({ "text": "a".repeat(MAX_FRAME_BYTES) })));
        let err = client.request(huge).await.unwrap_err();
        assert!(matches!(err, WinUtilError::Frame(clubshell_protocol::error::ProtocolError::FrameTooLarge { .. })), "{err}");
        assert!(client.is_connected());
        assert_eq!(hello(&client).await?.pc_name, "PC-TEST");

        // Inbound: a length prefix beyond MAX_FRAME_BYTES is unrecoverable (IPC_PROTOCOL.md §1).
        let mut garbage = (MAX_FRAME_BYTES as u32 + 1).to_le_bytes().to_vec();
        garbage.extend_from_slice(b"{}");
        server.raw.send(garbage).await?;
        let reason = tokio::time::timeout(WAIT, client.closed()).await?;
        match &reason {
            CloseReason::Protocol(msg) => assert!(msg.contains("exceeds"), "{msg}"),
            other => panic!("expected a protocol close, got {other:?}"),
        }
        assert!(!client.is_connected());
        assert_eq!(*client.state().borrow(), ConnectionState::Closed(reason));
        assert!(matches!(client.request(IpcEnvelope::request(names::auth::STATUS, None)).await, Err(WinUtilError::Closed)));
        Ok(())
    }

    #[tokio::test]
    async fn malformed_json_frame_is_skipped_not_fatal() -> anyhow::Result<()> {
        let server = TestServer::start(answer_all())?;
        let client = connect(&server, opts(1_000, false)).await?;
        let mut events = client.subscribe();
        hello(&client).await?;

        // A well-delimited frame with broken JSON is logged and skipped; the next frame still arrives.
        let body = b"{not json";
        let mut bad = (body.len() as u32).to_le_bytes().to_vec();
        bad.extend_from_slice(body);
        server.raw.send(bad).await?;
        let event = wire(IpcEnvelope::event(names::events::POLICY_CHANGED, Some(json!({ "version": 4 }))));
        server.send_event(&event).await?;
        assert_eq!(tokio::time::timeout(WAIT, events.recv()).await??, event);
        assert!(client.is_connected());
        Ok(())
    }

    #[tokio::test]
    async fn typed_error_envelopes_surface_as_ipc_errors() -> anyhow::Result<()> {
        use clubshell_protocol::error::{ErrorCode, IpcError};

        let server = TestServer::start(Arc::new(|req: IpcEnvelope, out: mpsc::Sender<IpcEnvelope>| {
            let reply = if req.name == names::session::START {
                IpcEnvelope::fail_for(&req, IpcError::session_already_active())
            } else {
                default_reply(&req).expect("reply")
            };
            let _ = out.try_send(reply);
        }))?;
        let client = connect(&server, opts(1_000, false)).await?;

        // `request` hands the error envelope back as-is …
        let raw = client.request(IpcEnvelope::request(names::session::START, Some(json!({ "tariffId": "x" })))).await?;
        assert!(raw.is_error());
        assert_eq!(raw.error.as_ref().map(|e| e.code), Some(ErrorCode::SessionAlreadyActive));

        // … while the typed helpers convert it.
        let err = client.call_raw(names::session::START, None).await.unwrap_err();
        assert!(matches!(err, WinUtilError::Ipc(ref e) if e.code == ErrorCode::SessionAlreadyActive), "{err}");
        let none: Option<Value> = client.call_optional(names::session::GET, &()).await?;
        assert_eq!(none, None);
        Ok(())
    }
}
