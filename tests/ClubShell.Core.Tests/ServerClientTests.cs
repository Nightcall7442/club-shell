using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Wallet;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace ClubShell.Core.Tests;

/// <summary>
/// <see cref="ServerClient"/> against a capturing <see cref="HttpMessageHandler"/>: request signing (SERVER_API.md §2.2),
/// error envelopes (§3), single-flight token refresh, clock-skew correction, <c>204</c>/<c>304</c> handling,
/// idempotency keys, route formatting and the retry classifier.
/// </summary>
public sealed class ServerClientTests : IDisposable
{
    private const string BaseUrl = "https://club.test/api/v1";
    private const string PcText = "7d2f1c3a-1111-4222-8333-444455556666";
    private const string PcPath = "/api/v1/pcs/" + PcText;

    private static readonly Guid PcId = Guid.Parse(PcText);
    private static readonly Guid UserId = Guid.Parse("3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b");
    private static readonly Guid TariffId = Guid.Parse("b1a5b6b4-3f6e-4b7f-8c26-1d0e2e5c9a10");
    private static readonly Guid SessionId = Guid.Parse("9c1e0000-0000-4000-8000-000000000001");
    private static readonly Guid IdempotencyKey = Guid.Parse("0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly string[] HardwareComponents = { "board:X1", "cpu:Y2" };

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "clubshell-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 21, 10, 15, 30, TimeSpan.Zero));
    private readonly FakeHttpHandler _handler = new();
    private readonly AgentSettings _settings = new() { Server = { BaseUrl = BaseUrl } };
    private readonly TokenStore _tokens;
    private readonly ServerClient _client;

    public ServerClientTests()
    {
        _tokens = new TokenStore(Path.Combine(_tempDir, "agent.tokens"), new NullTokenProtector(), _clock);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(RetryPolicy.HttpClientName).Returns(new HttpClient(_handler));
        var hardware = Substitute.For<IHardwareIdSource>();
        hardware.GetComponentsAsync(Arg.Any<CancellationToken>()).Returns(HardwareComponents);
        var options = Substitute.For<IOptionsMonitor<AgentSettings>>();
        options.CurrentValue.Returns(_settings);

        _client = new ServerClient(
            factory,
            _tokens,
            new Hwid(hardware, Path.Combine(_tempDir, "hwid.fallback")),
            _clock,
            options,
            NullLogger<ServerClient>.Instance);
    }

    public static TheoryData<string, string> Routes => new()
    {
        { Endpoints.AgentsRegister, "agents/register" },
        { Endpoints.AgentsRefresh, "agents/refresh" },
        { Endpoints.Pcs, "pcs" },
        { Endpoints.AuthLogin, "auth/login" },
        { Endpoints.AuthQrStart, "auth/qr/start" },
        { Endpoints.AuthGuest, "auth/guest" },
        { Endpoints.AuthLogout, "auth/logout" },
        { Endpoints.Sessions, "sessions" },
        { Endpoints.SessionsCurrent, "sessions/current" },
        { Endpoints.Games, "games" },
        { Endpoints.Apps, "apps" },
        { Endpoints.Tariffs, "tariffs" },
        { Endpoints.ShopProducts, "shop/products" },
        { Endpoints.ShopOrders, "shop/orders" },
        { Endpoints.BookingSeats, "booking/seats" },
        { Endpoints.BookingReserve, "booking/reserve" },
        { Endpoints.Tournaments, "tournaments" },
        { Endpoints.SupportCallAdmin, "support/call-admin" },
        { Endpoints.AnticheatReport, "anticheat/report" },
        { Endpoints.AgentHeartbeat(PcId), "agents/" + PcText + "/heartbeat" },
        { Endpoints.AgentTelemetry(PcId), "agents/" + PcText + "/telemetry" },
        { Endpoints.AgentConfig(PcId), "agents/" + PcText + "/config" },
        { Endpoints.AgentPolicies(PcId), "agents/" + PcText + "/policies" },
        { Endpoints.AgentCommands(PcId), "agents/" + PcText + "/commands" },
        { Endpoints.AgentCommandAck(PcId, IdempotencyKey), "agents/" + PcText + "/commands/0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d/ack" },
        { Endpoints.Pc(PcId), "pcs/" + PcText },
        { Endpoints.AuthQr("tok/en"), "auth/qr/tok%2Fen" },
        { Endpoints.User(UserId), "users/3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b" },
        { Endpoints.UserStats(UserId), "users/3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b/stats" },
        { Endpoints.UserAchievements(UserId), "users/3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b/achievements" },
        { Endpoints.UserLoyalty(UserId), "users/3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b/loyalty" },
        { Endpoints.SessionPause(SessionId), "sessions/9c1e0000-0000-4000-8000-000000000001/pause" },
        { Endpoints.SessionResume(SessionId), "sessions/9c1e0000-0000-4000-8000-000000000001/resume" },
        { Endpoints.SessionEnd(SessionId), "sessions/9c1e0000-0000-4000-8000-000000000001/end" },
        { Endpoints.SessionExtend(SessionId), "sessions/9c1e0000-0000-4000-8000-000000000001/extend" },
        { Endpoints.SessionEvents(SessionId), "sessions/9c1e0000-0000-4000-8000-000000000001/events" },
        { Endpoints.Game(TariffId), "games/b1a5b6b4-3f6e-4b7f-8c26-1d0e2e5c9a10" },
        { Endpoints.GameAccountLease(TariffId), "games/b1a5b6b4-3f6e-4b7f-8c26-1d0e2e5c9a10/accounts/lease" },
        { Endpoints.GameAccountLeaseRelease(TariffId, IdempotencyKey), "games/b1a5b6b4-3f6e-4b7f-8c26-1d0e2e5c9a10/accounts/0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d/release" },
        { Endpoints.GameAccountSaveUpload(TariffId, IdempotencyKey), "games/b1a5b6b4-3f6e-4b7f-8c26-1d0e2e5c9a10/accounts/0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d/save-upload" },
        { Endpoints.GameLaunchReport(TariffId), "games/b1a5b6b4-3f6e-4b7f-8c26-1d0e2e5c9a10/launch-report" },
        { Endpoints.WalletBalance(UserId), "wallet/3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b/balance" },
        { Endpoints.WalletTransactions(UserId), "wallet/3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b/transactions" },
        { Endpoints.WalletTopupIntent(UserId), "wallet/3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b/topup-intent" },
        { Endpoints.WalletTopupIntentStatus(UserId, IdempotencyKey), "wallet/3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b/topup-intent/0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d" },
        { Endpoints.ShopOrder(IdempotencyKey), "shop/orders/0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d" },
        { Endpoints.ShopOrderCancel(IdempotencyKey), "shop/orders/0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d/cancel" },
        { Endpoints.ChatMessages("club"), "chat/club/messages" },
        { Endpoints.ChatMessages("zone:VIP room"), "chat/zone%3AVIP%20room/messages" },
        { Endpoints.ChatRead("pc:" + PcText), "chat/pc%3A" + PcText + "/read" },
        { Endpoints.Booking(IdempotencyKey), "booking/0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d" },
        { Endpoints.TournamentJoin(IdempotencyKey), "tournaments/0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d/join" },
        { Endpoints.TournamentLeaderboard(IdempotencyKey), "tournaments/0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d/leaderboard" },
        { Endpoints.UpdateManifest(UpdateChannel.Stable), "updates/stable/manifest" },
        { Endpoints.UpdateManifest(UpdateChannel.Beta), "updates/beta/manifest" },
    };

    private long UnixNow => _clock.UtcNow.ToUnixTimeSeconds();

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
        _tokens.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region Signing

    [Fact]
    public async Task Get_request_carries_bearer_token_timestamp_and_hmac_signature()
    {
        await RegisterAsync();
        _client.AcceptLanguage = "ru";
        _client.ShellVersion = "1.4.2";
        _handler.Responder = _ => Ok(SamplePc());

        var pc = await _client.GetPcAsync(PcId, CancellationToken.None);

        pc.Id.Should().Be(PcId);
        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be(new Uri("https://club.test" + PcPath));
        request.Body.Should().BeNull();
        request.Header("Authorization").Should().Be("Bearer access-1");
        request.Header(Signing.TimestampHeader).Should().Be(Unix(UnixNow));
        request.Header(Signing.SignatureHeader).Should().Be(Signing.Sign(Secret, UnixNow, "GET", PcPath, Signing.EmptyBodySha256));
        request.Header("X-Trace-Id").Should().NotBeNullOrEmpty();
        request.Header("X-Agent-Version").Should().Be(_client.AgentVersion);
        request.Header("X-Shell-Version").Should().Be("1.4.2");
        request.Header("Accept-Language").Should().Be("ru");
        request.Header("User-Agent").Should().StartWith("ClubShellAgent/");
        request.Header("X-User-Token").Should().BeNull("agent-scoped calls never carry the user token");
        request.Header(RetryPolicy.IdempotencyKeyHeader).Should().BeNull();
    }

    [Fact]
    public async Task Post_body_hash_is_signed_and_the_server_time_offset_is_learned()
    {
        await RegisterAsync();
        var serverTime = _clock.UtcNow.AddSeconds(30);
        _handler.Responder = _ => Ok(new HeartbeatResponse(serverTime, PcStatus.Free, 12, 3, "cat-1", 0));
        var heartbeat = new HeartbeatRequest(PcStatus.Busy, SessionId, "1.4.2", "1.4.2", 8123, "10.0.1.12", 12, Array.Empty<HeartbeatRunningGame>(), 0, true);
        var sentAt = UnixNow;

        var response = await _client.HeartbeatAsync(PcId, heartbeat, CancellationToken.None);

        response.ServerTime.Should().Be(serverTime);
        _client.ServerTimeOffset.Should().Be(TimeSpan.FromSeconds(30));
        _client.ServerNow.Should().Be(serverTime);

        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.PathAndQuery.Should().Be("/api/v1/agents/" + PcText + "/heartbeat");
        request.Header("Content-Type").Should().Be("application/json; charset=utf-8");
        request.Body.Should().NotBeNull();
        var body = JsonDefaults.Deserialize<HeartbeatRequest>(request.Body!)!;
        body.Status.Should().Be(PcStatus.Busy);
        body.UptimeSec.Should().Be(8123);
        request.Header(Signing.TimestampHeader).Should().Be(Unix(sentAt));
        request.Header(Signing.SignatureHeader).Should().Be(Signing.Sign(Secret, sentAt, "POST", "/api/v1/agents/" + PcText + "/heartbeat", Signing.Sha256Hex(request.Body!)));

        _handler.Responder = _ => Ok(SamplePc());
        await _client.GetPcAsync(PcId, CancellationToken.None);
        _handler.Requests[1].Header(Signing.TimestampHeader).Should().Be(Unix(sentAt + 30), "later requests are stamped with the corrected server clock");
    }

    [Fact]
    public async Task Unregistered_client_refuses_authenticated_calls()
    {
        _handler.Responder = _ => Ok(SamplePc());

        Func<Task> act = () => _client.GetPcAsync(PcId, CancellationToken.None);

        (await act.Should().ThrowAsync<ServerApiException>()).Which.Code.Should().Be(ErrorCode.Unauthorized);
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Signing_string_is_timestamp_method_path_and_body_hash_without_separators()
    {
        Signing.BuildSigningString(1789992930, "post", "/api/v1/agents/" + PcText + "/heartbeat", "abc")
            .Should().Be("1789992930POST/api/v1/agents/" + PcText + "/heartbeatabc");
        Signing.Sha256Hex(Array.Empty<byte>()).Should().Be(Signing.EmptyBodySha256);
        Signing.ToHex(new byte[] { 0x00, 0xAB, 0xFF }).Should().Be("00abff");
        Signing.DecodeSecret(Convert.ToBase64String(Secret)).Should().Equal(Secret);

        var signature = Signing.Sign(Secret, 1789992930, "GET", "/api/v1/pcs", Signing.EmptyBodySha256);
        signature.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$");
        signature.Should().Be(Signing.ToHex(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes("1789992930GET/api/v1/pcs" + Signing.EmptyBodySha256))));
    }

    [Fact]
    public void Signature_verification_enforces_the_skew_window()
    {
        var now = UnixNow;
        var signature = Signing.Sign(Secret, now, "GET", "/api/v1/pcs", Signing.EmptyBodySha256);

        Signing.Verify(Secret, signature, now, "GET", "/api/v1/pcs", Signing.EmptyBodySha256, _clock).Should().BeTrue();
        Signing.Verify(Secret, signature.ToUpperInvariant(), now, "GET", "/api/v1/pcs", Signing.EmptyBodySha256, _clock).Should().BeTrue("hex case is irrelevant");
        Signing.Verify(Secret, signature, now, "POST", "/api/v1/pcs", Signing.EmptyBodySha256, _clock).Should().BeFalse();
        Signing.Verify(Secret, signature, now, "GET", "/api/v1/pcs?zone=VIP", Signing.EmptyBodySha256, _clock).Should().BeFalse();
        Signing.Verify(Secret, signature, now, "GET", "/api/v1/pcs", Signing.Sha256Hex("{}"u8), _clock).Should().BeFalse();
        Signing.Verify(Secret, "", now, "GET", "/api/v1/pcs", Signing.EmptyBodySha256, _clock).Should().BeFalse();

        var stale = now - 301;
        var staleSignature = Signing.Sign(Secret, stale, "GET", "/api/v1/pcs", Signing.EmptyBodySha256);
        Signing.Verify(Secret, staleSignature, stale, "GET", "/api/v1/pcs", Signing.EmptyBodySha256, _clock).Should().BeFalse("outside the default ±5 min window");
        Signing.Verify(Secret, staleSignature, stale, "GET", "/api/v1/pcs", Signing.EmptyBodySha256, _clock, TimeSpan.FromMinutes(10)).Should().BeTrue();
        Signing.DefaultSkew.Should().Be(TimeSpan.FromMinutes(5));
    }

    #endregion

    #region Errors

    [Fact]
    public async Task Error_envelope_becomes_ServerApiException_with_code_and_trace_id()
    {
        await RegisterAsync();
        const string body = """{ "error": { "code": "insufficientFunds", "message": "Balance too low", "details": { "required": { "amount": 500000, "currency": "UZS" }, "available": { "amount": 120000, "currency": "UZS" } }, "traceId": "6f1d2c4e" } }""";
        _handler.Responder = _ => Json(HttpStatusCode.PaymentRequired, body);

        Func<Task> act = () => _client.GetBalanceAsync(UserId, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ServerApiException>()).Which;
        error.Code.Should().Be(ErrorCode.InsufficientFunds);
        error.Status.Should().Be(HttpStatusCode.PaymentRequired);
        error.TraceId.Should().Be("6f1d2c4e");
        error.Message.Should().Be("Balance too low");
        error.Reason.Should().BeNull();
        error.IsRetryable.Should().BeFalse();
        error.IsAuthFailure.Should().BeFalse();
        error.Error.Should().NotBeNull();
        error.Error!.Details.HasValue.Should().BeTrue();

        var ipc = error.ToIpcError();
        ipc.Code.Should().Be(ErrorCode.InsufficientFunds);
        ipc.DetailsAs<InsufficientFundsDetails>().Should().Be(new InsufficientFundsDetails(Money.Uzs(500000), Money.Uzs(120000)));
        ipc.DetailsAs<TraceDetails>()!.TraceId.Should().Be("6f1d2c4e", "the trace id moves into details");
    }

    [Fact]
    public async Task Non_json_error_body_falls_back_to_the_status_mapping()
    {
        await RegisterAsync();
        _handler.Responder = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("<html>down</html>", Encoding.UTF8, "text/html"),
            };
            response.Headers.Add("X-Trace-Id", "hdr-trace");
            return response;
        };

        Func<Task> act = () => _client.GetPcAsync(PcId, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ServerApiException>()).Which;
        error.Code.Should().Be(ErrorCode.ServerUnavailable);
        error.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
        error.TraceId.Should().Be("hdr-trace", "the response header is the fallback trace source");
        error.Error.Should().BeNull();
        error.IsRetryable.Should().BeTrue();
        error.ToIpcError().DetailsAs<TraceDetails>()!.TraceId.Should().Be("hdr-trace");
    }

    [Fact]
    public async Task Transport_failures_map_to_serverUnavailable()
    {
        await RegisterAsync();
        _handler.Responder = _ => throw new HttpRequestException("connection refused");

        Func<Task> act = () => _client.GetPcAsync(PcId, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ServerApiException>()).Which;
        error.Code.Should().Be(ErrorCode.ServerUnavailable);
        error.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
        error.InnerException.Should().BeOfType<HttpRequestException>();
        error.TraceId.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region 401 handling

    [Fact]
    public async Task Expired_token_triggers_one_refresh_for_concurrent_callers_and_retries()
    {
        await RegisterAsync();
        var refreshes = 0;
        var pcJson = JsonDefaults.Serialize(SamplePc());
        var refreshJson = JsonDefaults.Serialize(new AgentRefreshResponse("access-2", "refresh-2", _clock.UtcNow.AddHours(1)));
        var expired = ErrorBody(ErrorCode.Unauthorized, "Token expired", "expired", "t-401");
        _handler.Responder = request =>
        {
            if (IsRefresh(request))
            {
                Interlocked.Increment(ref refreshes);
                return Json(HttpStatusCode.OK, refreshJson);
            }

            return request.Header("Authorization") == "Bearer access-2"
                ? Json(HttpStatusCode.OK, pcJson)
                : Json(HttpStatusCode.Unauthorized, expired);
        };

        var results = await Task.WhenAll(_client.GetPcAsync(PcId, CancellationToken.None), _client.GetPcAsync(PcId, CancellationToken.None));

        results.Should().HaveCount(2).And.OnlyContain(pc => pc.Id == PcId);
        refreshes.Should().Be(1, "refresh is single-flight");

        var agent = _tokens.Agent!;
        agent.AccessToken.Should().Be("access-2");
        agent.RefreshToken.Should().Be("refresh-2");
        agent.SigningSecret.Should().Be(Convert.ToBase64String(Secret), "a refresh without signingSecret keeps the old key");
        agent.PcId.Should().Be(PcId);

        var refresh = _handler.Requests.Single(IsRefresh);
        refresh.Method.Should().Be(HttpMethod.Post);
        refresh.Header("Authorization").Should().BeNull("the refresh call is unauthenticated");
        refresh.Header(Signing.SignatureHeader).Should().BeNull();
        var refreshRequest = JsonDefaults.Deserialize<AgentRefreshRequest>(refresh.Body!)!;
        refreshRequest.RefreshToken.Should().Be("refresh-1");
        refreshRequest.Hwid.Should().Be(Hwid.Compute(HardwareComponents));

        _handler.Requests.Count(r => !IsRefresh(r) && r.Header("Authorization") == "Bearer access-2").Should().Be(2, "each caller succeeds exactly once with the new token");
        _handler.Requests.Count(r => !IsRefresh(r) && r.Header("Authorization") == "Bearer access-1").Should().BeInRange(1, 2);
    }

    [Fact]
    public async Task Second_unauthorized_after_refresh_is_surfaced_to_the_caller()
    {
        await RegisterAsync();
        var refreshJson = JsonDefaults.Serialize(new AgentRefreshResponse("access-2", "refresh-2", _clock.UtcNow.AddHours(1)));
        _handler.Responder = request => IsRefresh(request)
            ? Json(HttpStatusCode.OK, refreshJson)
            : Json(HttpStatusCode.Unauthorized, ErrorBody(ErrorCode.Unauthorized, "Still rejected", "expired", "t-402"));

        Func<Task> act = () => _client.GetPcAsync(PcId, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ServerApiException>()).Which;
        error.Code.Should().Be(ErrorCode.Unauthorized);
        error.TraceId.Should().Be("t-402");
        _handler.Requests.Should().HaveCount(3, "one attempt, one refresh, one retry and no further loop");
        _handler.Requests.Count(IsRefresh).Should().Be(1);
    }

    [Fact]
    public async Task User_token_rejection_is_not_refreshed()
    {
        await RegisterAsync();
        await _tokens.SetUserAsync(new UserTokens(UserId, "user-access", "user-refresh", _clock.UtcNow.AddHours(1)), CancellationToken.None);
        _handler.Responder = _ => Json(HttpStatusCode.Unauthorized, ErrorBody(ErrorCode.Unauthorized, "User token invalid", "userToken", "t-user"));

        Func<Task> act = () => _client.GetBalanceAsync(UserId, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ServerApiException>()).Which;
        error.Reason.Should().Be("userToken");
        error.IsAuthFailure.Should().BeTrue();
        _handler.Requests.Should().ContainSingle().Which.Header("X-User-Token").Should().Be("user-access");
        _tokens.Agent!.AccessToken.Should().Be("access-1");
    }

    [Fact]
    public async Task Clock_skew_rejection_is_retried_once_with_the_server_time()
    {
        await RegisterAsync();
        var serverTime = _clock.UtcNow.AddMinutes(10);
        var attempts = 0;
        _handler.Responder = _ =>
        {
            if (Interlocked.Increment(ref attempts) > 1)
            {
                return Ok(SamplePc());
            }

            var rejected = Json(HttpStatusCode.Unauthorized, ErrorBody(ErrorCode.Unauthorized, "Clock skew", "clockSkew", "t-skew"));
            rejected.Headers.Add(Signing.ServerTimeHeader, JsonDefaults.Serialize(serverTime).Trim('"'));
            return rejected;
        };
        var sentAt = UnixNow;

        var pc = await _client.GetPcAsync(PcId, CancellationToken.None);

        pc.Id.Should().Be(PcId);
        _handler.Requests.Should().HaveCount(2);
        _handler.Requests[1].Uri.Should().Be(_handler.Requests[0].Uri);
        _handler.Requests[0].Header(Signing.TimestampHeader).Should().Be(Unix(sentAt));
        _handler.Requests[1].Header(Signing.TimestampHeader).Should().Be(Unix(sentAt + 600));
        _handler.Requests[1].Header(Signing.SignatureHeader).Should().Be(Signing.Sign(Secret, sentAt + 600, "GET", PcPath, Signing.EmptyBodySha256));
        _client.ServerTimeOffset.Should().Be(TimeSpan.FromMinutes(10));
        _tokens.Agent!.AccessToken.Should().Be("access-1", "clock skew never refreshes tokens");
    }

    #endregion

    #region Response shapes

    [Fact]
    public async Task No_content_yields_a_null_session()
    {
        await RegisterAsync();
        _handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        var session = await _client.GetCurrentSessionAsync(PcId, CancellationToken.None);

        session.Should().BeNull();
        _handler.Requests.Should().ContainSingle().Which.Uri.PathAndQuery.Should().Be("/api/v1/sessions/current?pcId=" + PcText);

        var manifest = await _client.GetUpdateManifestAsync(UpdateChannel.Stable, UpdateComponent.Agent, "1.4.2", CancellationToken.None);
        manifest.Should().BeNull();
        _handler.Requests[1].Uri.PathAndQuery.Should().Be("/api/v1/updates/stable/manifest?component=agent&current=1.4.2&arch=x64");
    }

    [Fact]
    public async Task Current_session_is_parsed_when_present()
    {
        await RegisterAsync();
        _handler.Responder = _ => Ok(SampleSession());

        var session = await _client.GetCurrentSessionAsync(PcId, CancellationToken.None);

        session.Should().NotBeNull();
        session!.Id.Should().Be(SessionId);
        session.State.Should().Be(SessionState.Active);
    }

    [Fact]
    public async Task Conditional_get_sends_if_none_match_and_maps_304_to_not_modified()
    {
        await RegisterAsync();
        _handler.Responder = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.ETag = new EntityTagHeaderValue("\"v12\"");
            return response;
        };

        var unchanged = await _client.GetConfigAsync(PcId, "\"v12\"", CancellationToken.None);

        unchanged.NotModified.Should().BeTrue();
        unchanged.Value.Should().BeNull();
        unchanged.ETag.Should().Be("\"v12\"");
        _handler.Requests.Should().ContainSingle().Which.Header("If-None-Match").Should().Be("\"v12\"");
        Action require = () => unchanged.Require();
        require.Should().Throw<InvalidOperationException>();
        EtagResponse<AgentServerConfig>.Unchanged("\"x\"").Should().Be(new EtagResponse<AgentServerConfig>(null, "\"x\"", true));
    }

    [Fact]
    public async Task Conditional_get_returns_the_value_and_the_new_etag_on_200()
    {
        await RegisterAsync();
        _handler.Responder = _ =>
        {
            var response = Ok(new AgentServerConfig(3, "PC-12", "Standard", 12));
            response.Headers.ETag = new EntityTagHeaderValue("\"v13\"");
            return response;
        };

        var fresh = await _client.GetConfigAsync(PcId, null, CancellationToken.None);

        fresh.NotModified.Should().BeFalse();
        fresh.ETag.Should().Be("\"v13\"");
        fresh.Require().PcName.Should().Be("PC-12");
        _handler.Requests.Should().ContainSingle().Which.Header("If-None-Match").Should().BeNull();
    }

    [Fact]
    public async Task Create_session_sends_the_idempotency_key_and_the_user_token()
    {
        await RegisterAsync();
        await _tokens.SetUserAsync(new UserTokens(UserId, "user-access", "user-refresh", _clock.UtcNow.AddHours(1)), CancellationToken.None);
        _handler.Responder = _ => Ok(SampleSession());
        var create = new SessionCreateRequest(PcId, UserId, TariffId, 60, true);

        var session = await _client.CreateSessionAsync(create, IdempotencyKey, CancellationToken.None);

        session.Id.Should().Be(SessionId);
        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.PathAndQuery.Should().Be("/api/v1/sessions");
        request.Header(RetryPolicy.IdempotencyKeyHeader).Should().Be("0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");
        request.Header("X-User-Token").Should().Be("user-access");
        request.Header("Authorization").Should().Be("Bearer access-1");
        JsonDefaults.Deserialize<SessionCreateRequest>(request.Body!).Should().Be(create);
    }

    [Fact]
    public async Task Malformed_success_body_is_a_protocol_error()
    {
        await RegisterAsync();
        _handler.Responder = _ => Json(HttpStatusCode.OK, "{ not json");

        Func<Task> act = () => _client.GetPcAsync(PcId, CancellationToken.None);

        (await act.Should().ThrowAsync<ServerApiException>()).Which.Code.Should().Be(ErrorCode.ProtocolError);
    }

    #endregion

    #region Endpoints / query

    [Theory]
    [MemberData(nameof(Routes))]
    public void Endpoints_format_every_route(string actual, string expected)
    {
        actual.Should().Be(expected);
        actual.Should().NotStartWith("/", "routes are relative to server.baseUrl");
    }

    [Fact]
    public void Endpoint_constants_match_the_server_api()
    {
        Endpoints.ApiVersion.Should().Be("v1");
        Endpoints.ApiPrefix.Should().Be("/api/v1");
        Endpoints.WsPath.Should().Be("/ws/agent");
        Endpoints.NormalizeBaseUrl(BaseUrl).ToString().Should().Be(BaseUrl + "/");
        Endpoints.NormalizeBaseUrl(BaseUrl + "/").ToString().Should().Be(BaseUrl + "/");
    }

    [Fact]
    public void BuildUri_appends_relative_paths_and_queries()
    {
        Endpoints.BuildUri(BaseUrl, "pcs").Should().Be(new Uri("https://club.test/api/v1/pcs"));
        Endpoints.BuildUri(BaseUrl + "/", "pcs", "?zone=VIP").Should().Be(new Uri("https://club.test/api/v1/pcs?zone=VIP"));
        Endpoints.BuildUri(BaseUrl, "pcs", "zone=VIP").Should().Be(new Uri("https://club.test/api/v1/pcs?zone=VIP"));
        Endpoints.BuildUri(BaseUrl, "pcs", "").Should().Be(new Uri("https://club.test/api/v1/pcs"));
        Endpoints.BuildUri(BaseUrl, "sessions/current?pcId=" + PcText).PathAndQuery.Should().Be("/api/v1/sessions/current?pcId=" + PcText);

        var query = new[] { KeyValuePair.Create("pcId", (string?)PcText), KeyValuePair.Create("zone", (string?)null), KeyValuePair.Create("q", (string?)"a b") };
        Endpoints.BuildUri(BaseUrl, "sessions/current", query).PathAndQuery.Should().Be("/api/v1/sessions/current?pcId=" + PcText + "&q=a%20b");
    }

    [Fact]
    public void QueryBuilder_uses_wire_formats_and_skips_nulls()
    {
        var query = new QueryBuilder()
            .Add("page", (int?)2)
            .Add("zone", (string?)null)
            .Add("from", (DateTimeOffset?)new DateTimeOffset(2026, 9, 21, 15, 15, 30, 123, TimeSpan.FromHours(5)))
            .Add("date", (DateOnly?)new DateOnly(2026, 9, 21))
            .Add("activeOnly", (bool?)true)
            .Add("pcId", (Guid?)PcId)
            .AddEnum("type", (TransactionType?)TransactionType.TopUp)
            .AddEnum("state", (SessionState?)null)
            .ToString();

        query.Should().Be("?page=2&from=2026-09-21T10%3A15%3A30.123Z&date=2026-09-21&activeOnly=true&pcId=" + PcText + "&type=topUp");
        new QueryBuilder().ToString().Should().BeEmpty();
        new QueryBuilder().Add("q", "a b&c=d").ToString().Should().Be("?q=a%20b%26c%3Dd");
        new QueryBuilder().Add("muted", (bool?)false).ToString().Should().Be("?muted=false");
        QueryBuilder.EnumName(UpdateChannel.Beta).Should().Be("beta");
        QueryBuilder.EnumName(ServerCommandType.RemoteControlStart).Should().Be("remoteControlStart");
    }

    #endregion

    #region Retry classification

    [Theory]
    [InlineData(408, true)]
    [InlineData(425, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(200, false)]
    [InlineData(204, false)]
    [InlineData(304, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(409, false)]
    [InlineData(426, false)]
    public void Transient_statuses_are_retryable(int status, bool expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        if (status == 429)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
        }

        RetryPolicy.IsTransient(response, null).Should().Be(expected);
    }

    [Fact]
    public void Transport_exceptions_are_retryable_but_other_exceptions_are_not()
    {
        RetryPolicy.IsTransient(null, new HttpRequestException("reset")).Should().BeTrue();
        RetryPolicy.IsTransient(null, new TimeoutRejectedException()).Should().BeTrue();
        RetryPolicy.IsTransient(null, new InvalidOperationException()).Should().BeFalse();
        RetryPolicy.IsTransient(null, null).Should().BeFalse();
        using var ok = new HttpResponseMessage(HttpStatusCode.OK);
        RetryPolicy.IsTransient(ok, new HttpRequestException("reset")).Should().BeTrue("the exception wins over the response");
    }

    [Theory]
    [InlineData("GET", false, true)]
    [InlineData("HEAD", false, true)]
    [InlineData("PUT", false, true)]
    [InlineData("DELETE", false, true)]
    [InlineData("POST", false, false)]
    [InlineData("POST", true, true)]
    [InlineData("PATCH", false, false)]
    [InlineData("PATCH", true, true)]
    public void Only_safe_or_idempotency_keyed_requests_are_replayed(string method, bool withKey, bool expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri("https://club.test/api/v1/x"));
        if (withKey)
        {
            request.Headers.Add(RetryPolicy.IdempotencyKeyHeader, IdempotencyKey.ToString("D"));
        }

        RetryPolicy.IsIdempotent(request).Should().Be(expected);
        RetryPolicy.IsIdempotent(null).Should().BeTrue();
    }

    [Fact]
    public void Resilience_pipeline_builds_from_settings()
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        RetryPolicy.ConfigurePipeline(builder, new ServerSettings());
        builder.Build().Should().NotBeNull();

        var single = new ResiliencePipelineBuilder<HttpResponseMessage>();
        RetryPolicy.ConfigurePipeline(single, new ServerSettings { Retry = new RetrySettings { MaxAttempts = 1 } });
        single.Build().Should().NotBeNull("a single attempt disables the retry strategy without failing validation");

        RetryPolicy.HttpClientName.Should().Be("ClubShell.Server");
        RetryPolicy.IdempotencyKeyHeader.Should().Be("Idempotency-Key");
    }

    #endregion

    private static string Unix(long seconds) => seconds.ToString(CultureInfo.InvariantCulture);

    private static bool IsRefresh(CapturedRequest request) => request.Uri.AbsolutePath.EndsWith("/agents/refresh", StringComparison.Ordinal);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => FakeHttpHandler.Json(status, body);

    private static HttpResponseMessage Ok<T>(T value) => FakeHttpHandler.Json(HttpStatusCode.OK, JsonDefaults.Serialize(value));

    private static string ErrorBody(ErrorCode code, string message, string? reason, string traceId) =>
        JsonDefaults.Serialize(new ServerErrorEnvelope(new ServerError(code, message, reason is null ? null : JsonDefaults.ToElement(new ReasonDetails(reason)), traceId)));

    private Task RegisterAsync() =>
        _tokens.SetAgentAsync(new AgentTokens(PcId, "access-1", "refresh-1", Convert.ToBase64String(Secret), _clock.UtcNow.AddHours(1)), CancellationToken.None);

    private Pc SamplePc() => new(PcId, "PC-12", "Standard", 12, null, "10.0.1.12", PcStatus.Free, null, "1.4.2", "1.4.2", _clock.UtcNow);

    private Session SampleSession() => new(SessionId, UserId, PcId, SessionState.Active, _clock.UtcNow, _clock.UtcNow.AddHours(1), null, TariffId, 3600, 0, Money.Zero, true, Array.Empty<int>());
}

/// <summary>Snapshot of one HTTP request as the handler saw it (the original message is disposed by the client).</summary>
internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, byte[]? Body)
{
    /// <summary>Header value (comma-joined when repeated) or <see langword="null"/> when absent.</summary>
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Primary handler that records every request and answers through <see cref="Responder"/>.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<CapturedRequest> _requests = new();
    private readonly object _gate = new();

    /// <summary>Produces the response for a captured request; must return a fresh message per call.</summary>
    public Func<CapturedRequest, HttpResponseMessage> Responder { get; set; } = _ => Json(HttpStatusCode.OK, "{}");

    /// <summary>Requests in send order.</summary>
    public CapturedRequest[] Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers, body);
        lock (_gate)
        {
            _requests.Add(captured);
        }

        return Responder(captured);
    }
}
