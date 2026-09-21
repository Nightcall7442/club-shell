using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Ipc.Handlers;
using ClubShell.Agent.Policy;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

// ---------------------------------------------------------------------------------------------
// MessageDispatcher (pure, no pipe)
// ---------------------------------------------------------------------------------------------

public sealed class MessageDispatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
    private const string CustomName = "test.thing";

    // ---- registration ---------------------------------------------------------------------------

    [Fact]
    public void Register_ExposesNamesAndRequiredAuth()
    {
        MessageDispatcher dispatcher = Build(d =>
        {
            d.Register<SysPingRequest, SysPongResponse>(IpcMessages.Sys.Ping, (_, request, _) => Task.FromResult(Pong(request)));
            d.RegisterNoPayload<OkResponse>(IpcMessages.Session.Pause, (_, _) => Task.FromResult(OkResponse.Instance));
            d.RegisterNoPayload<OkResponse>(CustomName, (_, _) => Task.FromResult(OkResponse.Instance));
            d.RegisterNoPayload<OkResponse>("test.user", (_, _) => Task.FromResult(OkResponse.Instance), IpcAuthLevel.User);
        });

        dispatcher.Names.Should().BeEquivalentTo(IpcMessages.Sys.Ping, IpcMessages.Session.Pause, CustomName, "test.user");
        dispatcher.IsRegistered(IpcMessages.Sys.Ping).Should().BeTrue();
        dispatcher.IsRegistered("nope.nope").Should().BeFalse();
        dispatcher.RequiredAuth(IpcMessages.Sys.Ping).Should().Be(IpcAuthLevel.None, "taken from AgentCommands");
        dispatcher.RequiredAuth(IpcMessages.Session.Pause).Should().Be(IpcAuthLevel.Session, "taken from AgentCommands");
        dispatcher.RequiredAuth(CustomName).Should().Be(IpcAuthLevel.Hello, "unknown names default to Hello");
        dispatcher.RequiredAuth("test.user").Should().Be(IpcAuthLevel.User, "explicit override");
        dispatcher.RequiredAuth("nope.nope").Should().BeNull();
        dispatcher.UnhandledCommands.Should().NotContain(IpcMessages.Sys.Ping).And.Contain(IpcMessages.Auth.Hello);
    }

    [Fact]
    public void Register_RejectsDuplicatesAndInvalidNames()
    {
        Action duplicate = () => Build(d =>
        {
            d.RegisterNoPayload<OkResponse>(CustomName, (_, _) => Task.FromResult(OkResponse.Instance));
            d.RegisterNoPayload<OkResponse>(CustomName, (_, _) => Task.FromResult(OkResponse.Instance));
        });
        Action invalid = () => Build(d => d.RegisterNoPayload<OkResponse>("nodot", (_, _) => Task.FromResult(OkResponse.Instance)));
        Action blank = () => Build(d => d.RegisterNoPayload<OkResponse>(" ", (_, _) => Task.FromResult(OkResponse.Instance)));

        duplicate.Should().Throw<InvalidOperationException>();
        invalid.Should().Throw<ArgumentException>();
        blank.Should().Throw<ArgumentException>();
    }

    // ---- envelope handling ----------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_CorrelatesIdAndName_AndTimestampsWithTheClock()
    {
        MessageDispatcher dispatcher = Build(d => d.Register<SysPingRequest, SysPongResponse>(IpcMessages.Sys.Ping, (_, request, _) => Task.FromResult(Pong(request))));
        IpcEnvelope request = IpcEnvelope.Request(IpcMessages.Sys.Ping, new SysPingRequest(42, T0));

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.None), request, CancellationToken.None);

        reply.Id.Should().Be(request.Id);
        reply.Kind.Should().Be(IpcKind.Response);
        reply.Name.Should().Be(IpcMessages.Sys.Pong, "sys.ping is answered as sys.pong");
        reply.V.Should().Be(IpcEnvelope.CurrentVersion);
        reply.Ts.Should().Be(T0);
        reply.IsError.Should().BeFalse();
        reply.PayloadAs<SysPongResponse>()!.Seq.Should().Be(42);
    }

    [Fact]
    public async Task Dispatch_UnknownName_IsNotFoundWithTheNameInDetails()
    {
        MessageDispatcher dispatcher = Build(_ => { });
        IpcEnvelope request = IpcEnvelope.Request("nope.nothing");

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Session), request, CancellationToken.None);

        reply.Id.Should().Be(request.Id);
        reply.Name.Should().Be("nope.nothing");
        reply.Error.Should().NotBeNull();
        reply.Error!.Code.Should().Be(ErrorCode.NotFound);
        reply.Error.DetailsAs<NameDetails>()!.Name.Should().Be("nope.nothing");
        reply.Payload.Should().BeNull();
    }

    [Fact]
    public async Task Dispatch_NonRequestKind_IsProtocolError()
    {
        MessageDispatcher dispatcher = Build(d => d.RegisterNoPayload<OkResponse>(CustomName, (_, _) => Task.FromResult(OkResponse.Instance)));
        IpcEnvelope evt = IpcEnvelope.Event(CustomName);

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Session), evt, CancellationToken.None);

        reply.Error!.Code.Should().Be(ErrorCode.ProtocolError);
        reply.Id.Should().Be(evt.Id);
    }

    [Fact]
    public async Task Dispatch_HandlerReturningNull_ProducesNullPayloadWithoutError()
    {
        MessageDispatcher dispatcher = Build(d => d.RegisterNoPayload<OkResponse?>(CustomName, (_, _) => Task.FromResult<OkResponse?>(null)));

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), IpcEnvelope.Request(CustomName), CancellationToken.None);

        reply.IsError.Should().BeFalse();
        reply.Payload.Should().BeNull();
    }

    // ---- auth enforcement -----------------------------------------------------------------------

    [Theory]
    [InlineData(IpcAuthLevel.None, IpcAuthLevel.None, true)]
    [InlineData(IpcAuthLevel.None, IpcAuthLevel.Session, true)]
    [InlineData(IpcAuthLevel.Hello, IpcAuthLevel.None, false)]
    [InlineData(IpcAuthLevel.Hello, IpcAuthLevel.Hello, true)]
    [InlineData(IpcAuthLevel.User, IpcAuthLevel.Hello, false)]
    [InlineData(IpcAuthLevel.User, IpcAuthLevel.User, true)]
    [InlineData(IpcAuthLevel.Session, IpcAuthLevel.User, false)]
    [InlineData(IpcAuthLevel.Session, IpcAuthLevel.Session, true)]
    public async Task Dispatch_EnforcesRequiredAuthAgainstConnectionAuth(IpcAuthLevel required, IpcAuthLevel connection, bool allowed)
    {
        bool invoked = false;
        MessageDispatcher dispatcher = Build(d => d.RegisterNoPayload<OkResponse>(CustomName, (_, _) =>
        {
            invoked = true;
            return Task.FromResult(OkResponse.Instance);
        }, required));

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(connection), IpcEnvelope.Request(CustomName), CancellationToken.None);

        invoked.Should().Be(allowed);
        reply.IsError.Should().Be(!allowed);
    }

    [Fact]
    public async Task Dispatch_DeniedRequests_ReportWhyInTheError()
    {
        MessageDispatcher dispatcher = Build(d =>
        {
            d.RegisterNoPayload<OkResponse>("test.hello", (_, _) => Task.FromResult(OkResponse.Instance), IpcAuthLevel.Hello);
            d.RegisterNoPayload<OkResponse>("test.user", (_, _) => Task.FromResult(OkResponse.Instance), IpcAuthLevel.User);
            d.RegisterNoPayload<OkResponse>("test.session", (_, _) => Task.FromResult(OkResponse.Instance), IpcAuthLevel.Session);
        });

        IpcEnvelope noHello = await dispatcher.DispatchAsync(Context(IpcAuthLevel.None), IpcEnvelope.Request("test.hello"), CancellationToken.None);
        IpcEnvelope noUser = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), IpcEnvelope.Request("test.user"), CancellationToken.None);
        IpcEnvelope noSession = await dispatcher.DispatchAsync(Context(IpcAuthLevel.User), IpcEnvelope.Request("test.session"), CancellationToken.None);
        IpcEnvelope noSessionNoUser = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), IpcEnvelope.Request("test.session"), CancellationToken.None);

        noHello.Error!.Code.Should().Be(ErrorCode.Unauthorized);
        noHello.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("helloRequired");
        noUser.Error!.Code.Should().Be(ErrorCode.Unauthorized);
        noUser.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("loginRequired");
        noSession.Error!.Code.Should().Be(ErrorCode.SessionNotActive, "a logged-in user without a session gets sessionNotActive");
        noSessionNoUser.Error!.Code.Should().Be(ErrorCode.Unauthorized, "without a user the login is what is missing");
    }

    // ---- payload validation ---------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_RequiredPayloadMissing_IsValidationError()
    {
        MessageDispatcher dispatcher = Build(d => d.Register<GamesGetRequest, OkResponse>(IpcMessages.Games.Get, (_, _, _) => Task.FromResult(OkResponse.Instance)));

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), IpcEnvelope.Request(IpcMessages.Games.Get), CancellationToken.None);

        reply.Error!.Code.Should().Be(ErrorCode.Validation);
        ValidationDetails details = reply.Error.DetailsAs<ValidationDetails>()!;
        details.Field.Should().Be("payload");
        details.Reason.Should().Be("required");
    }

    [Fact]
    public async Task Dispatch_MalformedPayload_IsValidationErrorNamingTheField()
    {
        MessageDispatcher dispatcher = Build(d => d.Register<GamesGetRequest, OkResponse>(IpcMessages.Games.Get, (_, _, _) => Task.FromResult(OkResponse.Instance)));
        IpcEnvelope request = IpcEnvelope.Request(IpcMessages.Games.Get, Json("""{ "gameId": "not-a-guid" }"""));

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), request, CancellationToken.None);

        reply.Error!.Code.Should().Be(ErrorCode.Validation);
        ValidationDetails details = reply.Error.DetailsAs<ValidationDetails>()!;
        details.Field.Should().Be("gameId");
        details.Reason.Should().Be("format");
    }

    [Fact]
    public async Task Dispatch_OptionalPayload_PassesNullWhenAbsentAndTypedWhenPresent()
    {
        string? seen = "unset";
        MessageDispatcher dispatcher = Build(d => d.RegisterOptional<SessionPauseRequest, OkResponse>(IpcMessages.Session.Pause, (_, request, _) =>
        {
            seen = request?.Reason;
            return Task.FromResult(OkResponse.Instance);
        }));

        IpcEnvelope absent = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Session), IpcEnvelope.Request(IpcMessages.Session.Pause), CancellationToken.None);
        absent.IsError.Should().BeFalse();
        seen.Should().BeNull();

        IpcEnvelope present = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Session), IpcEnvelope.Request(IpcMessages.Session.Pause, new SessionPauseRequest("break")), CancellationToken.None);
        present.IsError.Should().BeFalse();
        seen.Should().Be("break");
    }

    [Fact]
    public async Task Dispatch_NoPayloadHandler_IgnoresAnyPayload()
    {
        MessageDispatcher dispatcher = Build(d => d.RegisterNoPayload<OkResponse>(CustomName, (_, _) => Task.FromResult(OkResponse.Instance)));
        IpcEnvelope request = IpcEnvelope.Request(CustomName, Json("""{ "anything": 1 }"""));

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), request, CancellationToken.None);

        reply.IsError.Should().BeFalse();
        reply.PayloadAs<OkResponse>()!.Ok.Should().BeTrue();
    }

    // ---- exception mapping ----------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_MapsHandlerExceptionsToErrorCodes()
    {
        var cases = new (Exception Thrown, ErrorCode Expected)[]
        {
            (IpcError.Forbidden("nope", "pidMismatch").ToException(), ErrorCode.Forbidden),
            (IpcError.SessionAlreadyActive().ToException(), ErrorCode.SessionAlreadyActive),
            (new ServerApiException(ErrorCode.InsufficientFunds, HttpStatusCode.PaymentRequired, null, "trace-1"), ErrorCode.InsufficientFunds),
            (new ServerApiException(ErrorCode.ServerUnavailable, HttpStatusCode.ServiceUnavailable, null, null), ErrorCode.ServerUnavailable),
            (new HttpRequestException("connection refused"), ErrorCode.AgentOffline),
            (new OperationCanceledException(), ErrorCode.Timeout),
            (new TaskCanceledException(), ErrorCode.Timeout),
            (new TimeoutException("slow"), ErrorCode.Timeout),
            (new JsonException("bad json"), ErrorCode.Validation),
            (new Win32Exception(5), ErrorCode.Internal),
            (new InvalidOperationException("boom"), ErrorCode.Internal),
        };

        foreach ((Exception thrown, ErrorCode expected) in cases)
        {
            MessageDispatcher dispatcher = Build(d => d.RegisterNoPayload<OkResponse>(CustomName, (_, _) => Task.FromException<OkResponse>(thrown)));
            IpcEnvelope request = IpcEnvelope.Request(CustomName);

            IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), request, CancellationToken.None);

            reply.Id.Should().Be(request.Id, "even failures keep the correlation id ({0})", thrown.GetType().Name);
            reply.Error.Should().NotBeNull(thrown.GetType().Name);
            reply.Error!.Code.Should().Be(expected, thrown.GetType().Name);
            reply.Payload.Should().BeNull();
        }
    }

    [Fact]
    public async Task Dispatch_IpcException_ReturnsTheExactError()
    {
        IpcError error = IpcError.PolicyDenied("features.shop", "productId");
        MessageDispatcher dispatcher = Build(d => d.RegisterNoPayload<OkResponse>(CustomName, (_, _) => throw error.ToException()));

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), IpcEnvelope.Request(CustomName), CancellationToken.None);

        reply.Error.Should().BeSameAs(error);
    }

    [Fact]
    public async Task Dispatch_ServerApiException_CarriesTraceIdIntoDetails()
    {
        var upstream = new ServerApiException(ErrorCode.Conflict, HttpStatusCode.Conflict, null, "trace-xyz", "seat taken");
        MessageDispatcher dispatcher = Build(d => d.RegisterNoPayload<OkResponse>(CustomName, (_, _) => throw upstream));

        IpcEnvelope reply = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), IpcEnvelope.Request(CustomName), CancellationToken.None);

        reply.Error!.Code.Should().Be(ErrorCode.Conflict);
        reply.Error.Message.Should().Be("seat taken");
        reply.Error.DetailsAs<TraceDetails>()!.TraceId.Should().Be("trace-xyz");
    }

    [Fact]
    public async Task Dispatch_UnexpectedException_IsInternalWithAFreshTraceId()
    {
        MessageDispatcher dispatcher = Build(d => d.RegisterNoPayload<OkResponse>(CustomName, (_, _) => throw new InvalidOperationException("boom")));

        IpcEnvelope first = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), IpcEnvelope.Request(CustomName), CancellationToken.None);
        IpcEnvelope second = await dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), IpcEnvelope.Request(CustomName), CancellationToken.None);

        first.Error!.Code.Should().Be(ErrorCode.Internal);
        first.Error.Message.Should().NotContain("boom", "internal details never leak to the Shell");
        string trace = first.Error.DetailsAs<TraceDetails>()!.TraceId;
        trace.Should().HaveLength(32);
        second.Error!.DetailsAs<TraceDetails>()!.TraceId.Should().NotBe(trace);
    }

    [Fact]
    public async Task Dispatch_ForwardsTheCancellationTokenToTheHandler()
    {
        using var cts = new CancellationTokenSource();
        MessageDispatcher dispatcher = Build(d => d.RegisterNoPayload<OkResponse>(CustomName, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return OkResponse.Instance;
        }));
        Task<IpcEnvelope> pending = dispatcher.DispatchAsync(Context(IpcAuthLevel.Hello), IpcEnvelope.Request(CustomName), cts.Token);

        await cts.CancelAsync();
        IpcEnvelope reply = await pending;

        reply.Error!.Code.Should().Be(ErrorCode.Timeout);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static MessageDispatcher Build(Action<MessageDispatcher> register) =>
        new([new InlineGroup(register)], new SystemClock(new FakeTimeProvider(T0)), NullLogger<MessageDispatcher>.Instance);

    private static IpcContext Context(IpcAuthLevel auth) =>
        new(Guid.NewGuid(), auth, null, null, Environment.ProcessId, 1, static (_, _, _) => { });

    private static SysPongResponse Pong(SysPingRequest request) =>
        new(request.Seq, request.SentAt, T0, ConnectivityState.Online);

    /// <summary>Raw payload; typed <see cref="Nullable{T}"/> so the non-generic <see cref="IpcEnvelope.Request(string, JsonElement?, DateTimeOffset?)"/> overload binds.</summary>
    private static JsonElement? Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class InlineGroup : IIpcHandlerGroup
    {
        private readonly Action<MessageDispatcher> _register;

        public InlineGroup(Action<MessageDispatcher> register) => _register = register;

        public void Register(MessageDispatcher dispatcher) => _register(dispatcher);
    }
}

// ---------------------------------------------------------------------------------------------
// PipeServer end-to-end over a real named pipe (needs Windows + elevation: the pipe DACL and the
// shell token ACL admit SYSTEM/Administrators only, and the client-process check requires either the
// kiosk account or an administrator).
// ---------------------------------------------------------------------------------------------

public sealed class PipeServerEndToEndTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [WindowsAdminFact]
    public async Task HelloThenPing_AnswersPong_AndUnlocksSecuredRequests()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using Harness harness = await Harness.StartAsync(cts.Token);
        using NamedPipeClientStream client = await harness.ConnectAsync(cts.Token);
        string token = (await File.ReadAllTextAsync(harness.Token.TokenPath, cts.Token)).Trim();
        ShellTokenStore.IsHex64(token).Should().BeTrue("the server wrote a 64-hex shell token at start");

        // Before the handshake only auth.hello / sys.ping are allowed.
        IpcEnvelope early = IpcEnvelope.Request(Harness.SecuredName);
        await SendAsync(client, early, cts.Token);
        IpcEnvelope denied = await ReceiveAsync(client, cts.Token);
        denied.Id.Should().Be(early.Id);
        denied.Error!.Code.Should().Be(ErrorCode.Unauthorized);
        denied.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("helloRequired");

        IpcEnvelope hello = IpcEnvelope.Request(IpcMessages.Auth.Hello, new AuthHelloRequest(token, "1.0.0-test", Environment.ProcessId, 1, Locale.En, []));
        await SendAsync(client, hello, cts.Token);
        IpcEnvelope welcome = await ReceiveAsync(client, cts.Token);
        welcome.Id.Should().Be(hello.Id);
        welcome.Name.Should().Be(IpcMessages.Auth.Hello);
        welcome.IsError.Should().BeFalse();
        welcome.PayloadAs<AuthHelloResponse>()!.Protocol.Should().Be(IpcEnvelope.CurrentVersion);
        harness.Server.IsShellConnected.Should().BeTrue();

        IpcEnvelope ping = IpcEnvelope.Request(IpcMessages.Sys.Ping, new SysPingRequest(7, DateTimeOffset.UtcNow));
        await SendAsync(client, ping, cts.Token);
        IpcEnvelope pong = await ReceiveAsync(client, cts.Token);
        pong.Id.Should().Be(ping.Id);
        pong.Name.Should().Be(IpcMessages.Sys.Pong);
        pong.Kind.Should().Be(IpcKind.Response);
        pong.PayloadAs<SysPongResponse>()!.Seq.Should().Be(7);

        IpcEnvelope secured = IpcEnvelope.Request(Harness.SecuredName);
        await SendAsync(client, secured, cts.Token);
        IpcEnvelope ok = await ReceiveAsync(client, cts.Token);
        ok.Id.Should().Be(secured.Id);
        ok.IsError.Should().BeFalse();

        harness.Server.ConnectionCount.Should().Be(1);
        harness.Server.Stats.Requests.Should().BeGreaterThanOrEqualTo(4);
    }

    [WindowsAdminFact]
    public async Task WrongToken_IsUnauthorized_AndTheConnectionIsClosed()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using Harness harness = await Harness.StartAsync(cts.Token);
        using NamedPipeClientStream client = await harness.ConnectAsync(cts.Token);
        string wrong = new('0', ShellTokenStore.TokenBytes * 2);

        IpcEnvelope hello = IpcEnvelope.Request(IpcMessages.Auth.Hello, new AuthHelloRequest(wrong, "1.0.0-test", Environment.ProcessId, 1, Locale.En, []));
        await SendAsync(client, hello, cts.Token);
        IpcEnvelope reply = await ReceiveAsync(client, cts.Token);

        reply.Id.Should().Be(hello.Id);
        reply.Error!.Code.Should().Be(ErrorCode.Unauthorized);
        reply.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("shellToken");
        (await WaitForCloseAsync(client, cts.Token)).Should().BeTrue("a rejected auth.hello closes the connection after the response is flushed");
        harness.Server.IsShellConnected.Should().BeFalse();
    }

    [WindowsAdminFact]
    public async Task OversizedFrame_ClosesTheConnectionWithoutAReply()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using Harness harness = await Harness.StartAsync(cts.Token);
        using NamedPipeClientStream client = await harness.ConnectAsync(cts.Token);

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, harness.Settings.Ipc.MaxMessageBytes + 1);
        await client.WriteAsync(header, cts.Token);
        await client.FlushAsync(cts.Token);

        (await WaitForCloseAsync(client, cts.Token)).Should().BeTrue("a frame above ipc.maxMessageBytes is a protocol error that drops the connection");
        harness.Server.Stats.Accepted.Should().Be(1);
    }

    // ---- framing helpers ([u32 LE length][JSON]) -----------------------------------------------

    private static async Task SendAsync(Stream pipe, IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] json = JsonDefaults.SerializeToUtf8Bytes(envelope);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await pipe.WriteAsync(header, cancellationToken);
        await pipe.WriteAsync(json, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }

    private static async Task<IpcEnvelope> ReceiveAsync(Stream pipe, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        length.Should().BeInRange(1, IpcEnvelope.MaxFrameBytes);
        byte[] body = new byte[length];
        await pipe.ReadExactlyAsync(body, cancellationToken);
        return JsonDefaults.Deserialize<IpcEnvelope>(body) ?? throw new InvalidDataException("The server sent a null envelope.");
    }

    /// <summary><see langword="true"/> when the next read observes end-of-stream or a broken pipe.</summary>
    private static async Task<bool> WaitForCloseAsync(Stream pipe, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        try
        {
            return await pipe.ReadAsync(one, cancellationToken) == 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>A <see cref="PipeServer"/> on a random pipe name with a minimal handler group (auth.hello, sys.ping, one secured request).</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public const string SecuredName = "test.secured";

        private readonly TempDir _dir;

        private Harness(TempDir dir, AgentSettings settings, ShellTokenStore token, PipeServer server)
        {
            _dir = dir;
            Settings = settings;
            Token = token;
            Server = server;
        }

        public AgentSettings Settings { get; }

        public ShellTokenStore Token { get; }

        public PipeServer Server { get; }

        public static async Task<Harness> StartAsync(CancellationToken cancellationToken)
        {
            var dir = new TempDir();
            var settings = new AgentSettings { PcName = "PC-TEST", Zone = "test" };
            settings.Paths.ProgramData = dir.Root;
            settings.Ipc.PipeName = "clubshell-test-" + Guid.NewGuid().ToString("N");
            settings.Ipc.MaxMessageBytes = 64 * 1024;
            var monitor = TestSupport.Monitor(settings);
            IClock clock = SystemClock.Instance;

            var users = new ShellUserContext();
            var shellSettings = new ShellSettingsStore(monitor, NullLogger<ShellSettingsStore>.Instance);
            var bridge = new IpcEventBridge(users, shellSettings, Substitute.For<IServiceProvider>(), clock, NullLogger<IpcEventBridge>.Instance);
            var token = new ShellTokenStore(monitor, NullLogger<ShellTokenStore>.Instance);
            ISessionService sessions = Substitute.For<ISessionService>();
            IKioskCredentials kiosk = Substitute.For<IKioskCredentials>();
            kiosk.UserName.Returns("club");
            kiosk.Password.Returns(string.Empty);
            kiosk.Sid.Returns(string.Empty);
            var dispatcher = new MessageDispatcher([new StubHandlers(token, settings)], clock, NullLogger<MessageDispatcher>.Instance);
            var server = new PipeServer(dispatcher, bridge, users, token, sessions, kiosk, monitor, clock, NullLogger<PipeServer>.Instance)
            {
                ClientValidation = ClientValidationMode.None,
                KillShellOnHeartbeatLoss = false,
            };

            await server.StartAsync(cancellationToken);
            return new Harness(dir, settings, token, server);
        }

        public async Task<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
        {
            var client = new NamedPipeClientStream(".", Settings.Ipc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(10_000, cancellationToken);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Server.StopAsync(stop.Token);
            Server.Dispose();
            _dir.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Stand-in for the production handler groups: verifies the shell token and elevates the connection.</summary>
    private sealed class StubHandlers : IIpcHandlerGroup
    {
        private readonly ShellTokenStore _token;
        private readonly AgentSettings _settings;

        public StubHandlers(ShellTokenStore token, AgentSettings settings)
        {
            _token = token;
            _settings = settings;
        }

        public void Register(MessageDispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            dispatcher.Register<AuthHelloRequest, AuthHelloResponse>(IpcMessages.Auth.Hello, (context, request, _) =>
            {
                if (!_token.Verify(request.ShellToken))
                {
                    throw IpcError.Unauthorized("Shell token mismatch", "shellToken").ToException();
                }

                context.Elevate(IpcAuthLevel.Hello, null, null);
                var response = new AuthHelloResponse(
                    "1.0.0-test",
                    IpcEnvelope.CurrentVersion,
                    _settings.PcId ?? Guid.Empty,
                    _settings.PcName ?? Environment.MachineName,
                    _settings.Zone ?? string.Empty,
                    false,
                    0,
                    DateTimeOffset.UtcNow,
                    [],
                    "club");
                return Task.FromResult(response);
            });
            dispatcher.Register<SysPingRequest, SysPongResponse>(IpcMessages.Sys.Ping, (_, request, _) =>
                Task.FromResult(new SysPongResponse(request.Seq, request.SentAt, DateTimeOffset.UtcNow, ConnectivityState.Offline)));
            dispatcher.RegisterNoPayload<OkResponse>(Harness.SecuredName, (_, _) => Task.FromResult(OkResponse.Instance), IpcAuthLevel.Hello);
        }
    }
}
