using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Shop;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Tests;

/// <summary>
/// Wire-format conformance of <see cref="JsonDefaults"/> against IPC_PROTOCOL.md / SERVER_API.md: every enum is a
/// camelCase string, timestamps are UTC ISO-8601 with milliseconds, club times are <c>HH:mm</c>, nulls are omitted
/// unless the contract marks the key as always present, and every contract type is reachable through the
/// source-generated <see cref="ContractsJsonContext"/>.
/// </summary>
public sealed class SerializationTests
{
    private static readonly Guid RequestId = Guid.Parse("6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11");
    private static readonly Guid EventId = Guid.Parse("0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");
    private static readonly Guid TariffId = Guid.Parse("b1a5b6b4-3f6e-4b7f-8c26-1d0e2e5c9a10");
    private static readonly Guid PcId = Guid.Parse("7d2f1c3a-1111-4222-8333-444455556666");
    private static readonly Guid UserId = Guid.Parse("3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b");
    private static readonly Guid SessionId = Guid.Parse("9c1e0000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Ts = new(2026, 9, 21, 10, 15, 30, 123, TimeSpan.Zero);
    private const string ShellToken = "9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f";

    #region Enums

    [Fact]
    public void Every_enum_serializes_as_camelCase_string_and_rejects_integers()
    {
        var enums = typeof(JsonDefaults).Assembly.GetExportedTypes().Where(t => t.IsEnum).ToList();
        enums.Should().NotBeEmpty();

        foreach (var enumType in enums)
        {
            var typeInfo = JsonDefaults.Options.GetTypeInfo(enumType);
            foreach (var value in Enum.GetValues(enumType).Cast<object>())
            {
                var expected = enumType == typeof(AgentCommand)
                    ? ((AgentCommand)value).ToIpcName()
                    : JsonNamingPolicy.CamelCase.ConvertName(value.ToString()!);

                var json = JsonSerializer.Serialize(value, typeInfo);
                json.Should().Be($"\"{expected}\"", "{0}.{1} must use its camelCase wire literal", enumType.Name, value);
                JsonSerializer.Deserialize(json, typeInfo).Should().Be(value);
            }

            Action integer = () => _ = JsonSerializer.Deserialize("0", typeInfo);
            integer.Should().Throw<JsonException>("{0} must reject integer values", enumType.Name);
        }
    }

    [Theory]
    [InlineData("\"battleNet\"", LauncherType.BattleNet)]
    [InlineData("\"battlEye\"", AntiCheatKind.BattlEye)]
    [InlineData("\"topUp\"", TransactionType.TopUp)]
    [InlineData("\"timeUp\"", SessionEndReason.TimeUp)]
    [InlineData("\"insufficientFunds\"", ErrorCode.InsufficientFunds)]
    [InlineData("\"sessionAlreadyActive\"", ErrorCode.SessionAlreadyActive)]
    [InlineData("\"remoteControlStart\"", ServerCommandType.RemoteControlStart)]
    [InlineData("\"offlineQueueFlushed\"", AgentEventType.OfflineQueueFlushed)]
    [InlineData("\"pcStatusChanged\"", WsPushKind.PcStatusChanged)]
    [InlineData("\"showMessage\"", ShellCommandKind.ShowMessage)]
    [InlineData("\"uz\"", Locale.Uz)]
    [InlineData("\"mon\"", Weekday.Mon)]
    [InlineData("\"event\"", IpcKind.Event)]
    public void Enum_wire_values_match_protocol_tables(string json, object value)
    {
        var typeInfo = JsonDefaults.Options.GetTypeInfo(value.GetType());
        JsonSerializer.Serialize(value, typeInfo).Should().Be(json);
        JsonSerializer.Deserialize(json, typeInfo).Should().Be(value);
    }

    [Fact]
    public void Unknown_enum_literal_is_rejected()
    {
        Action act = () => _ = JsonDefaults.Deserialize<Locale>("\"fr\"");
        act.Should().Throw<JsonException>();
    }

    #endregion

    #region Money

    [Fact]
    public void Money_serializes_as_amount_and_currency()
    {
        JsonDefaults.Serialize(Money.Uzs(1500000)).Should().Be("{\"amount\":1500000,\"currency\":\"UZS\"}");
        JsonDefaults.Serialize(Money.Of(-250, "usd")).Should().Be("{\"amount\":-250,\"currency\":\"USD\"}");

        JsonDefaults.Deserialize<Money>("{\"amount\":1500000,\"currency\":\"UZS\"}").Should().Be(Money.Uzs(1500000));
        JsonDefaults.Deserialize<Money>("{\"AMOUNT\":5}").Should().Be(Money.Uzs(5), "property names are case-insensitive and the currency defaults to UZS");
        JsonDefaults.Deserialize<Money>("{\"amount\":5,\"currency\":null}").Should().Be(Money.Uzs(5));
        JsonDefaults.Deserialize<Money>("{\"amount\":5,\"currency\":\"usd\",\"note\":\"ignored\"}").Should().Be(Money.Of(5, "USD"));
    }

    [Theory]
    [InlineData("{\"currency\":\"UZS\"}")]
    [InlineData("{\"amount\":\"5\"}")]
    [InlineData("{\"amount\":5.5}")]
    [InlineData("{\"amount\":5,\"currency\":7}")]
    [InlineData("5")]
    [InlineData("[5]")]
    public void Money_rejects_malformed_objects(string json)
    {
        Action act = () => _ = JsonDefaults.Deserialize<Money>(json);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Money_value_semantics()
    {
        default(Money).Should().Be(Money.Zero);
        new Money(7, "uzs").Should().Be(Money.Uzs(7));
        new Money(7, " ").Currency.Should().Be("UZS");
        Money.Of(5, " usd ").Currency.Should().Be("USD");

        (Money.Uzs(100) + Money.Uzs(50)).Should().Be(Money.Uzs(150));
        (Money.Uzs(100) - Money.Uzs(150)).Should().Be(Money.Uzs(-50));
        (Money.Uzs(100) * 3).Should().Be(Money.Uzs(300));
        Money.Uzs(-5).Abs().Should().Be(Money.Uzs(5));
        Money.Uzs(5).Negate().Should().Be(Money.Uzs(-5));
        Money.Sum(new[] { Money.Uzs(1), Money.Uzs(2), Money.Uzs(3) }).Should().Be(Money.Uzs(6));
        Money.Sum(Array.Empty<Money>()).Should().Be(Money.Zero);
        (Money.Uzs(1) < Money.Uzs(2)).Should().BeTrue();
        (Money.Uzs(2) >= Money.Uzs(2)).Should().BeTrue();
        Money.Uzs(1500000).ToString().Should().Be("1500000 UZS");

        Action mismatch = () => _ = Money.Uzs(1) + Money.Of(1, "USD");
        mismatch.Should().Throw<InvalidOperationException>();
        Action compare = () => _ = Money.Uzs(1) < Money.Of(1, "USD");
        compare.Should().Throw<InvalidOperationException>();
        Action overflow = () => _ = Money.Uzs(long.MaxValue) + Money.Uzs(1);
        overflow.Should().Throw<OverflowException>();
    }

    #endregion

    #region Scalars

    [Fact]
    public void Timestamps_are_utc_iso8601_with_milliseconds_and_z()
    {
        JsonDefaults.Serialize(Ts).Should().Be("\"2026-09-21T10:15:30.123Z\"");
        JsonDefaults.Serialize(new DateTimeOffset(2026, 9, 21, 15, 15, 30, 123, TimeSpan.FromHours(5))).Should().Be("\"2026-09-21T10:15:30.123Z\"", "offsets are normalized to UTC");
        JsonDefaults.Serialize(new DateTimeOffset(2026, 9, 21, 10, 15, 30, TimeSpan.Zero)).Should().Be("\"2026-09-21T10:15:30.000Z\"", "milliseconds are always written");

        var parsed = JsonDefaults.Deserialize<DateTimeOffset>("\"2026-09-21T15:15:30.123+05:00\"");
        parsed.Should().Be(Ts);
        parsed.Offset.Should().Be(TimeSpan.Zero);
        JsonDefaults.Deserialize<DateTimeOffset>("\"2026-09-21T10:15:30Z\"").Should().Be(new DateTimeOffset(2026, 9, 21, 10, 15, 30, TimeSpan.Zero));

        Action number = () => _ = JsonDefaults.Deserialize<DateTimeOffset>("1789992930");
        number.Should().Throw<JsonException>();
        Action garbage = () => _ = JsonDefaults.Deserialize<DateTimeOffset>("\"yesterday\"");
        garbage.Should().Throw<JsonException>();
    }

    [Fact]
    public void Club_times_are_hh_mm()
    {
        JsonDefaults.Serialize(new TimeOnly(4, 5)).Should().Be("\"04:05\"");
        JsonDefaults.Serialize(new TimeOnly(23, 59, 59)).Should().Be("\"23:59\"");
        JsonDefaults.Serialize(new TimeWindow(new TimeOnly(4, 0), new TimeOnly(7, 0))).Should().Be("{\"from\":\"04:00\",\"to\":\"07:00\"}");

        JsonDefaults.Deserialize<TimeOnly>("\"04:05\"").Should().Be(new TimeOnly(4, 5));
        JsonDefaults.Deserialize<TimeOnly>("\"4:05\"").Should().Be(new TimeOnly(4, 5));
        JsonDefaults.Deserialize<TimeOnly>("\"04:05:00\"").Should().Be(new TimeOnly(4, 5));
        JsonDefaults.Deserialize<TimeOnly>("\"04:05:00.5000000\"").Should().Be(new TimeOnly(4, 5, 0, 500));
        JsonDefaults.Deserialize<TimeWindow>("{\"from\":\"22:00\",\"to\":\"02:00\"}").Should().Be(new TimeWindow(new TimeOnly(22, 0), new TimeOnly(2, 0)));
    }

    [Theory]
    [InlineData("\"25:00\"")]
    [InlineData("\"0405\"")]
    [InlineData("\"4\"")]
    [InlineData("405")]
    public void Club_times_reject_other_forms(string json)
    {
        Action act = () => _ = JsonDefaults.Deserialize<TimeOnly>(json);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Dates_are_yyyy_mm_dd()
    {
        JsonDefaults.Serialize(new DateOnly(2026, 9, 21)).Should().Be("\"2026-09-21\"");
        JsonDefaults.Serialize(new BookingSeatsRequest(new DateOnly(2026, 9, 21))).Should().Be("{\"date\":\"2026-09-21\"}");
        JsonDefaults.Deserialize<BookingSeatsRequest>("{\"date\":\"2026-09-21\"}").Should().Be(new BookingSeatsRequest(new DateOnly(2026, 9, 21)));
    }

    [Fact]
    public void Uuids_are_lowercase()
    {
        JsonDefaults.Serialize(Guid.Parse("6F1D2C4E-1B3A-4A7C-9F7D-0E6F2B5A9C11")).Should().Be("\"6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11\"");
        JsonDefaults.Deserialize<Guid>("\"6F1D2C4E-1B3A-4A7C-9F7D-0E6F2B5A9C11\"").Should().Be(RequestId);
    }

    #endregion

    #region IpcEnvelope

    [Fact]
    public void Request_without_payload_keeps_payload_and_error_keys()
    {
        var envelope = new IpcEnvelope(IpcEnvelope.CurrentVersion, RequestId, IpcKind.Request, IpcMessages.Session.Get, Ts, null, null);

        JsonDefaults.Serialize(envelope).Should().Be(
            "{\"v\":1,\"id\":\"6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11\",\"kind\":\"request\",\"name\":\"session.get\",\"ts\":\"2026-09-21T10:15:30.123Z\",\"payload\":null,\"error\":null}");

        var parsed = JsonDefaults.Deserialize<IpcEnvelope>(JsonDefaults.Serialize(envelope))!;
        parsed.Should().Be(envelope);
        parsed.HasPayload.Should().BeFalse();
        parsed.IsError.Should().BeFalse();
        parsed.PayloadAs<SessionStartRequest>().Should().BeNull();
        parsed.Validate().Should().BeNull();
    }

    [Fact]
    public void Request_envelope_matches_protocol_example()
    {
        const string expected = """
            {
              "v": 1,
              "id": "6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11",
              "kind": "request",
              "name": "session.start",
              "ts": "2026-09-21T10:15:30.123Z",
              "payload": { "tariffId": "b1a5b6b4-3f6e-4b7f-8c26-1d0e2e5c9a10", "minutes": 60, "prepaid": true },
              "error": null
            }
            """;

        var envelope = new IpcEnvelope(
            IpcEnvelope.CurrentVersion,
            RequestId,
            IpcKind.Request,
            IpcMessages.Session.Start,
            Ts,
            JsonDefaults.ToElement(new SessionStartRequest(TariffId, true, 60)),
            null);

        Canonical(JsonDefaults.Serialize(envelope)).Should().Be(Canonical(expected));

        var parsed = JsonDefaults.Deserialize<IpcEnvelope>(expected)!;
        parsed.V.Should().Be(1);
        parsed.Id.Should().Be(RequestId);
        parsed.Kind.Should().Be(IpcKind.Request);
        parsed.Name.Should().Be("session.start");
        parsed.Ts.Should().Be(Ts);
        parsed.Error.Should().BeNull();
        parsed.HasPayload.Should().BeTrue();
        parsed.Validate().Should().BeNull();
        parsed.PayloadAs<SessionStartRequest>().Should().Be(new SessionStartRequest(TariffId, true, 60));
        parsed.RequirePayload<SessionStartRequest>().Minutes.Should().Be(60);
    }

    [Fact]
    public void Hello_handshake_matches_protocol_example()
    {
        const string request = """
            { "v": 1, "id": "6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11", "kind": "request", "name": "auth.hello", "ts": "2026-09-21T10:00:00.000Z",
              "payload": { "shellToken": "9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f", "shellVersion": "1.4.2", "pid": 5120, "wtsSessionId": 1, "locale": "ru",
                           "capabilities": ["gamepad", "virtualKeyboard", "multiMonitor", "overlay"] }, "error": null }
            """;
        const string response = """
            { "v": 1, "id": "6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11", "kind": "response", "name": "auth.hello", "ts": "2026-09-21T10:00:00.010Z",
              "payload": { "agentVersion": "1.4.2", "protocol": 1, "pcId": "7d2f1c3a-1111-4222-8333-444455556666", "pcName": "PC-12", "zone": "Standard",
                           "serverOnline": true, "policyVersion": 12, "serverTime": "2026-09-21T10:00:00.009Z",
                           "capabilities": ["accountPool", "cloudSave", "remoteControl", "screenCapture", "virtualKeyboard", "wol", "offline", "shop", "chat", "booking", "tournaments"],
                           "kioskUser": "club" }, "error": null }
            """;

        var hello = new AuthHelloRequest(
            ShellToken,
            "1.4.2",
            5120,
            1,
            Locale.Ru,
            new[] { ShellCapabilities.Gamepad, ShellCapabilities.VirtualKeyboard, ShellCapabilities.MultiMonitor, ShellCapabilities.Overlay });
        var requestEnvelope = new IpcEnvelope(1, RequestId, IpcKind.Request, IpcMessages.Auth.Hello, new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero), JsonDefaults.ToElement(hello), null);
        Canonical(JsonDefaults.Serialize(requestEnvelope)).Should().Be(Canonical(request));

        var helloResponse = new AuthHelloResponse(
            "1.4.2",
            IpcEnvelope.CurrentVersion,
            PcId,
            "PC-12",
            "Standard",
            true,
            12,
            new DateTimeOffset(2026, 9, 21, 10, 0, 0, 9, TimeSpan.Zero),
            new[]
            {
                AgentCapabilities.AccountPool, AgentCapabilities.CloudSave, AgentCapabilities.RemoteControl, AgentCapabilities.ScreenCapture,
                AgentCapabilities.VirtualKeyboard, AgentCapabilities.Wol, AgentCapabilities.Offline, AgentCapabilities.Shop, AgentCapabilities.Chat,
                AgentCapabilities.Booking, AgentCapabilities.Tournaments,
            },
            "club");
        var responseEnvelope = IpcEnvelope.ReplyTo(requestEnvelope, helloResponse, new DateTimeOffset(2026, 9, 21, 10, 0, 0, 10, TimeSpan.Zero));
        Canonical(JsonDefaults.Serialize(responseEnvelope)).Should().Be(Canonical(response));

        var parsedRequest = JsonDefaults.Deserialize<IpcEnvelope>(request)!;
        var payload = parsedRequest.RequirePayload<AuthHelloRequest>();
        payload.ShellVersion.Should().Be("1.4.2");
        payload.Pid.Should().Be(5120);
        payload.Locale.Should().Be(Locale.Ru);
        payload.Capabilities.Should().Equal("gamepad", "virtualKeyboard", "multiMonitor", "overlay");

        var parsedResponse = JsonDefaults.Deserialize<IpcEnvelope>(response)!;
        parsedResponse.Id.Should().Be(parsedRequest.Id, "a response copies the request id");
        parsedResponse.Kind.Should().Be(IpcKind.Response);
        var body = parsedResponse.RequirePayload<AuthHelloResponse>();
        body.PcId.Should().Be(PcId);
        body.Protocol.Should().Be(1);
        body.ServerTime.Should().Be(new DateTimeOffset(2026, 9, 21, 10, 0, 0, 9, TimeSpan.Zero));
        body.Capabilities.Should().HaveCount(11).And.Contain(AgentCapabilities.Tournaments);
    }

    [Fact]
    public void Failed_response_has_null_payload_and_error_with_details()
    {
        var request = new IpcEnvelope(1, RequestId, IpcKind.Request, "foo.bar", Ts, null, null);
        var failure = IpcEnvelope.Fail(request, IpcError.UnknownMessage("foo.bar"), Ts);

        JsonDefaults.Serialize(failure).Should().Be(
            "{\"v\":1,\"id\":\"6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11\",\"kind\":\"response\",\"name\":\"foo.bar\",\"ts\":\"2026-09-21T10:15:30.123Z\",\"payload\":null,"
            + "\"error\":{\"code\":\"notFound\",\"message\":\"Unknown message 'foo.bar'\",\"details\":{\"name\":\"foo.bar\"}}}");

        var parsed = JsonDefaults.Deserialize<IpcEnvelope>(JsonDefaults.Serialize(failure))!;
        parsed.IsError.Should().BeTrue();
        parsed.Error!.Code.Should().Be(ErrorCode.NotFound);
        parsed.Error!.DetailsAs<NameDetails>().Should().Be(new NameDetails("foo.bar"));
        parsed.Validate().Should().BeNull();

        Action requirePayload = () => _ = parsed.RequirePayload<OkResponse>();
        requirePayload.Should().Throw<IpcException>().Which.Code.Should().Be(ErrorCode.Validation);
    }

    [Fact]
    public void Event_envelope_carries_payload_and_null_error()
    {
        var endsAt = new DateTimeOffset(2026, 9, 21, 10, 20, 30, 123, TimeSpan.Zero);
        var envelope = new IpcEnvelope(1, EventId, IpcKind.Event, IpcMessages.Events.SessionWarning, Ts, JsonDefaults.ToElement(new SessionWarning(SessionId, 5, 300, endsAt)), null);

        JsonDefaults.Serialize(envelope).Should().Be(
            "{\"v\":1,\"id\":\"0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d\",\"kind\":\"event\",\"name\":\"session.warning\",\"ts\":\"2026-09-21T10:15:30.123Z\","
            + "\"payload\":{\"sessionId\":\"9c1e0000-0000-4000-8000-000000000001\",\"minutesLeft\":5,\"secondsLeft\":300,\"endsAt\":\"2026-09-21T10:20:30.123Z\"},\"error\":null}");

        var built = IpcEnvelope.Event(IpcMessages.Events.SessionWarning, new SessionWarning(SessionId, 5, 300, endsAt), Ts);
        built.Kind.Should().Be(IpcKind.Event);
        built.Id.Should().NotBe(Guid.Empty);
        built.PayloadAs<SessionWarning>().Should().Be(new SessionWarning(SessionId, 5, 300, endsAt));
        built.Validate().Should().BeNull();
    }

    [Fact]
    public void Ping_is_answered_as_pong_with_the_same_id()
    {
        var ping = IpcEnvelope.Request(IpcMessages.Sys.Ping, new SysPingRequest(7, Ts), Ts);
        var pong = IpcEnvelope.ReplyTo(ping, new SysPongResponse(7, Ts, Ts, ConnectivityState.Online), Ts);

        pong.Id.Should().Be(ping.Id);
        pong.Name.Should().Be(IpcMessages.Sys.Pong);
        pong.Kind.Should().Be(IpcKind.Response);
        IpcEnvelope.ResponseNameFor(IpcMessages.Session.Start).Should().Be(IpcMessages.Session.Start);
        IpcEnvelope.Fail(ping, IpcError.Timeout()).Name.Should().Be(IpcMessages.Sys.Pong);
    }

    [Fact]
    public void Validate_enforces_envelope_rules()
    {
        var ok = new IpcEnvelope(1, RequestId, IpcKind.Request, "session.get", Ts, null, null);
        ok.Validate().Should().BeNull();

        var badVersion = ok with { V = 2 };
        badVersion.Validate()!.Code.Should().Be(ErrorCode.VersionMismatch);
        badVersion.Validate()!.DetailsAs<VersionMismatchDetails>()!.Got.Should().Be(2);
        badVersion.Validate()!.DetailsAs<VersionMismatchDetails>()!.Supported.Should().Equal(1);

        (ok with { Id = Guid.Empty }).Validate()!.Code.Should().Be(ErrorCode.ProtocolError);
        (ok with { Name = "nodot" }).Validate()!.Code.Should().Be(ErrorCode.ProtocolError);
        (ok with { Error = IpcError.Timeout() }).Validate()!.Code.Should().Be(ErrorCode.ProtocolError, "only responses may carry an error");

        using var array = JsonDocument.Parse("[1,2]");
        (ok with { Payload = array.RootElement.Clone() }).Validate()!.Code.Should().Be(ErrorCode.ProtocolError, "payload must be an object or null");

        var errorWithPayload = ok with { Kind = IpcKind.Response, Error = IpcError.Timeout(), Payload = JsonDefaults.ToElement(OkResponse.Instance) };
        errorWithPayload.Validate()!.Code.Should().Be(ErrorCode.ProtocolError, "a failed response must not carry a payload");

        IpcEnvelope.IsVersionSupported(1).Should().BeTrue();
        IpcEnvelope.IsVersionSupported(0).Should().BeFalse();
        IpcEnvelope.MaxFrameBytes.Should().Be(4 * 1024 * 1024);
    }

    [Theory]
    [InlineData("session.start", true)]
    [InlineData("sys.ping", true)]
    [InlineData("games.installStatus", true)]
    [InlineData("nodot", false)]
    [InlineData("a.b.c", false)]
    [InlineData(".x", false)]
    [InlineData("x.", false)]
    [InlineData("session.start1", false)]
    [InlineData("session.start-now", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Message_names_are_domain_dot_action(string? name, bool valid)
    {
        IpcEnvelope.IsValidName(name).Should().Be(valid);
    }

    #endregion

    #region IpcError / ServerError

    [Fact]
    public void Error_details_records_serialize_with_their_documented_shapes()
    {
        JsonDefaults.Serialize(IpcError.Of(ErrorCode.Timeout)).Should().Be("{\"code\":\"timeout\",\"message\":\"Operation timed out\",\"details\":null}", "the details key is always present");
        JsonDefaults.Serialize(IpcError.Validation("minutes", "min")).Should().Be("{\"code\":\"validation\",\"message\":\"Invalid 'minutes': min\",\"details\":{\"field\":\"minutes\",\"reason\":\"min\"}}");
        JsonDefaults.Serialize(IpcError.RateLimited(3)).Should().Be("{\"code\":\"rateLimited\",\"message\":\"Rate limit exceeded\",\"details\":{\"retryAfterSec\":3}}");
        JsonDefaults.Serialize(IpcError.PolicyDenied("ageRating")).Should().Be("{\"code\":\"policyDenied\",\"message\":\"Denied by policy rule 'ageRating'\",\"details\":{\"rule\":\"ageRating\"}}");
        JsonDefaults.Serialize(IpcError.GameLaunchFailed("createProcess", "boom", 5)).Should().Be("{\"code\":\"gameLaunchFailed\",\"message\":\"boom\",\"details\":{\"stage\":\"createProcess\",\"exitCode\":5}}");
        JsonDefaults.Serialize(IpcError.VersionMismatch(IpcEnvelope.SupportedVersions, 2)).Should().Be("{\"code\":\"versionMismatch\",\"message\":\"Protocol version 2 unsupported\",\"details\":{\"supported\":[1],\"got\":2}}");
        JsonDefaults.Serialize(IpcError.InsufficientFunds(Money.Uzs(500000), Money.Uzs(120000))).Should().Be(
            "{\"code\":\"insufficientFunds\",\"message\":\"Balance too low\",\"details\":{\"required\":{\"amount\":500000,\"currency\":\"UZS\"},\"available\":{\"amount\":120000,\"currency\":\"UZS\"}}}");
        JsonDefaults.Serialize(IpcError.AntiCheatBlocked(AntiCheatKind.Vanguard, AntiCheatChecks.SecureBootOff)).Should().Be(
            "{\"code\":\"antiCheatBlocked\",\"message\":\"Anti-cheat check failed: secureBootOff\",\"details\":{\"kind\":\"vanguard\",\"reason\":\"secureBootOff\"}}");
        JsonDefaults.Serialize(IpcError.Internal("trace-1")).Should().Be("{\"code\":\"internal\",\"message\":\"Internal error\",\"details\":{\"traceId\":\"trace-1\"}}");
    }

    [Fact]
    public void Error_details_round_trip_through_DetailsAs()
    {
        var error = JsonDefaults.Deserialize<IpcError>(JsonDefaults.Serialize(IpcError.InsufficientFunds(Money.Uzs(500000), Money.Uzs(120000))))!;
        error.Code.Should().Be(ErrorCode.InsufficientFunds);
        error.DetailsAs<InsufficientFundsDetails>().Should().Be(new InsufficientFundsDetails(Money.Uzs(500000), Money.Uzs(120000)));
        error.IsRetryable.Should().BeFalse();
        error.ToException().Code.Should().Be(ErrorCode.InsufficientFunds);

        JsonDefaults.Deserialize<IpcError>("{\"code\":\"unauthorized\",\"message\":\"x\",\"details\":{\"reason\":\"expired\"}}")!
            .DetailsAs<ReasonDetails>().Should().Be(new ReasonDetails("expired"));
        JsonDefaults.Deserialize<IpcError>("{\"code\":\"timeout\",\"message\":\"x\",\"details\":null}")!
            .DetailsAs<ReasonDetails>().Should().BeNull();
        IpcError.Timeout().IsRetryable.Should().BeTrue();
        IpcError.RateLimited(1).IsRetryable.Should().BeTrue();
        IpcError.Unauthorized("Token expired", "expired").DetailsAs<ReasonDetails>()!.Reason.Should().Be("expired");
    }

    [Fact]
    public void Server_error_envelope_maps_onto_ipc_error_with_trace_id_in_details()
    {
        const string body = """
            { "error": { "code": "insufficientFunds", "message": "Balance too low", "details": { "required": { "amount": 500000, "currency": "UZS" }, "available": { "amount": 120000, "currency": "UZS" } }, "traceId": "6f1d2c4e" } }
            """;

        var envelope = JsonDefaults.Deserialize<ServerErrorEnvelope>(body)!;
        envelope.Error.Code.Should().Be(ErrorCode.InsufficientFunds);
        envelope.Error.TraceId.Should().Be("6f1d2c4e");
        envelope.Error.Code.ToHttpStatus().Should().Be(402);

        var ipc = envelope.Error.ToIpcError();
        ipc.Code.Should().Be(ErrorCode.InsufficientFunds);
        ipc.Message.Should().Be("Balance too low");
        ipc.DetailsAs<InsufficientFundsDetails>().Should().Be(new InsufficientFundsDetails(Money.Uzs(500000), Money.Uzs(120000)));
        ipc.DetailsAs<TraceDetails>()!.TraceId.Should().Be("6f1d2c4e");

        JsonDefaults.Serialize(new ServerError(ErrorCode.Internal, "x", null, "t1")).Should().Be("{\"code\":\"internal\",\"message\":\"x\",\"details\":null,\"traceId\":\"t1\"}");
        var fromNothing = IpcError.WithTraceId(null, "t2");
        fromNothing.GetProperty("traceId").GetString().Should().Be("t2");
        fromNothing.EnumerateObject().Should().HaveCount(1);
    }

    [Fact]
    public void Error_code_metadata_is_consistent()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            code.Describe().Should().NotBe("Unknown error");
            code.ToHttpStatus().Should().BeInRange(400, 599);
        }

        ErrorCodes.FromHttpStatus(402).Should().Be(ErrorCode.InsufficientFunds);
        ErrorCodes.FromHttpStatus(426).Should().Be(ErrorCode.VersionMismatch);
        ErrorCodes.FromHttpStatus(502).Should().Be(ErrorCode.ServerUnavailable);
        ErrorCodes.FromHttpStatus(504).Should().Be(ErrorCode.Timeout);
        ErrorCode.Forbidden.IsAuthFailure().Should().BeTrue();
        ErrorCode.AgentOffline.IsRetryable().Should().BeTrue();
        ErrorCode.Conflict.IsRetryable().Should().BeFalse();
    }

    #endregion

    #region Server commands / WebSocket

    [Fact]
    public void Server_command_payload_is_typed_through_PayloadAs()
    {
        const string body = """
            { "items": [ { "id": "6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11", "ts": "2026-09-21T10:15:30.123Z", "name": "lock",
                           "payload": { "reason": "admin", "message": "Please wait" }, "issuedBy": "admin1", "expiresAt": "2026-09-21T10:20:30.123Z" },
                         { "id": "0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", "ts": "2026-09-21T10:15:30.123Z", "name": "unlock", "payload": null } ] }
            """;

        var response = JsonDefaults.Deserialize<ServerCommandsResponse>(body)!;
        response.Items.Should().HaveCount(2);

        var lockCommand = ServerCommand.FromEnvelope(response.Items[0]);
        lockCommand.Id.Should().Be(RequestId);
        lockCommand.Type.Should().Be(ServerCommandType.Lock);
        lockCommand.IssuedAt.Should().Be(Ts);
        lockCommand.IssuedBy.Should().Be("admin1");
        lockCommand.PayloadAs<LockCommand>().Should().Be(new LockCommand("admin", "Please wait"));
        lockCommand.IsExpired(Ts).Should().BeFalse();
        lockCommand.IsExpired(Ts.AddMinutes(5)).Should().BeTrue();

        var unlock = ServerCommand.FromEnvelope(response.Items[1]);
        unlock.Type.Should().Be(ServerCommandType.Unlock);
        unlock.Payload.Should().BeNull();
        unlock.PayloadAs<LockCommand>().Should().BeNull();
        unlock.ExpiresAt.Should().BeNull();
        unlock.IsExpired(Ts.AddYears(10)).Should().BeFalse();
    }

    [Fact]
    public void Server_command_types_round_trip_their_wire_names()
    {
        foreach (var type in Enum.GetValues<ServerCommandType>())
        {
            var wire = type.ToWireName();
            wire.Should().Be(JsonNamingPolicy.CamelCase.ConvertName(type.ToString()));
            ServerCommandTypes.TryParse(wire, out var parsed).Should().BeTrue();
            parsed.Should().Be(type);
            JsonDefaults.Serialize(type).Should().Be($"\"{wire}\"");
        }

        ServerCommandTypes.TryParse("1", out _).Should().BeFalse();
        ServerCommandTypes.TryParse("explode", out _).Should().BeFalse();
        ServerCommandTypes.TryParse(null, out _).Should().BeFalse();
        ServerCommandType.RemoteControlStart.ToWireName().Should().Be("remoteControlStart");
    }

    [Fact]
    public void Ws_frames_serialize_with_payload_always_present()
    {
        var ping = new WsFrame(WsFrameType.Ping, RequestId, Ts);
        JsonDefaults.Serialize(ping).Should().Be("{\"type\":\"ping\",\"id\":\"6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11\",\"ts\":\"2026-09-21T10:15:30.123Z\",\"payload\":null}");

        var pong = WsFrame.PongFor(ping, Ts);
        pong.Type.Should().Be(WsFrameType.Pong);
        pong.Id.Should().Be(ping.Id, "a pong copies the ping id");

        var ack = new WsFrame(WsFrameType.Ack, EventId, Ts, null, null, WsAck.From(RequestId, CommandAck.Success()));
        JsonDefaults.Serialize(ack).Should().Be(
            "{\"type\":\"ack\",\"id\":\"0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d\",\"ts\":\"2026-09-21T10:15:30.123Z\",\"payload\":null,\"ack\":{\"id\":\"6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11\",\"ok\":true}}");

        var failedAck = WsFrame.AckOf(RequestId, CommandAck.Failure(IpcError.Timeout()), Ts);
        failedAck.Ack!.Ok.Should().BeFalse();
        failedAck.Ack!.Error!.Code.Should().Be(ErrorCode.Timeout);
        failedAck.Ack!.Result.Should().BeNull();

        var resultAck = CommandAck.Success(new ScheduledResult(Ts));
        JsonDefaults.Serialize(resultAck).Should().Be("{\"ok\":true,\"result\":{\"scheduledAt\":\"2026-09-21T10:15:30.123Z\"}}");

        var agentEvent = AgentEvent.Of(AgentEventType.GameLaunched, Ts, new GameLaunchedEvent(SessionId, TariffId, 4242, null, Ts));
        var eventFrame = WsFrame.EventOf(agentEvent);
        eventFrame.Type.Should().Be(WsFrameType.Event);
        eventFrame.Name.Should().Be("gameLaunched");
        eventFrame.Ts.Should().Be(Ts);
        eventFrame.PayloadAs<GameLaunchedEvent>().Should().Be(new GameLaunchedEvent(SessionId, TariffId, 4242, null, Ts));

        WsFrame.Subprotocol.Should().Be("clubshell.v1");
        WsFrame.MaxFrameBytes.Should().Be(1024 * 1024);
    }

    [Fact]
    public void Command_frames_become_server_commands_and_push_frames_expose_their_kind()
    {
        const string command = """
            { "type": "command", "id": "6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11", "ts": "2026-09-21T10:15:30.123Z", "name": "endSession",
              "payload": { "sessionId": "9c1e0000-0000-4000-8000-000000000001", "reason": "admin" },
              "supersedes": "0a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", "expiresAt": "2026-09-21T10:20:30.123Z" }
            """;

        var frame = JsonDefaults.Deserialize<WsFrame>(command)!;
        frame.Type.Should().Be(WsFrameType.Command);
        frame.Supersedes.Should().Be(EventId);
        frame.ExpiresAt.Should().Be(Ts.AddMinutes(5));
        frame.TryGetPushKind(out _).Should().BeFalse();

        var serverCommand = ServerCommand.FromFrame(frame);
        serverCommand.Type.Should().Be(ServerCommandType.EndSession);
        serverCommand.Supersedes.Should().Be(EventId);
        serverCommand.PayloadAs<EndSessionCommand>().Should().Be(new EndSessionCommand(SessionId, SessionEndReason.Admin));

        Action wrongType = () => ServerCommand.FromFrame(frame with { Type = WsFrameType.Push });
        wrongType.Should().Throw<ArgumentException>();
        Action unknownName = () => ServerCommand.FromFrame(frame with { Name = "explode" });
        unknownName.Should().Throw<ArgumentException>();

        var push = new WsFrame(WsFrameType.Push, EventId, Ts, "pcStatusChanged", JsonDefaults.ToElement(new PcStatusChangedPush(PcId, PcStatus.Busy)));
        push.TryGetPushKind(out var kind).Should().BeTrue();
        kind.Should().Be(WsPushKind.PcStatusChanged);
        push.PayloadAs<PcStatusChangedPush>().Should().Be(new PcStatusChangedPush(PcId, PcStatus.Busy));

        foreach (var value in Enum.GetValues<WsPushKind>())
        {
            WsPushKinds.TryParse(value.ToWireName(), out var parsed).Should().BeTrue();
            parsed.Should().Be(value);
        }
    }

    #endregion

    #region Collections / domain helpers

    [Fact]
    public void Paged_result_serializes_items_total_page_and_pageSize()
    {
        var transaction = new Transaction(RequestId, UserId, TransactionType.Charge, Money.Uzs(-250000), Money.Uzs(1250000), "Session", Ts, "s-1");
        var page = new PagedResult<Transaction>(new[] { transaction }, 51, 1, 50);

        var json = JsonDefaults.Serialize(page);
        json.Should().StartWith("{\"items\":[{\"id\":\"6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11\"");
        json.Should().EndWith("\"total\":51,\"page\":1,\"pageSize\":50}");
        json.Should().NotContain("hasMore", "computed properties are not on the wire");
        page.HasMore.Should().BeTrue();
        (page with { Page = 2 }).HasMore.Should().BeFalse();
        (page with { Total = 50 }).HasMore.Should().BeFalse();

        var parsed = JsonDefaults.Deserialize<PagedResult<Transaction>>(json)!;
        parsed.Items.Should().ContainSingle().Which.Should().Be(transaction);
        parsed.Total.Should().Be(51);
        parsed.Page.Should().Be(1);
        parsed.PageSize.Should().Be(50);
        JsonDefaults.Serialize(parsed).Should().Be(json);
    }

    [Fact]
    public void Policy_DiffSections_reports_changed_sections_in_key_order()
    {
        var policy = SamplePolicy();
        Policy.SectionKeys.Should().HaveCount(9).And.StartWith("shellReplacement").And.EndWith("kiosk");

        policy.DiffSections(SamplePolicy()).Should().BeEmpty("equal contents in fresh list instances are not a change");

        policy.DiffSections(policy with { Usb = new UsbPolicy(true, true) }).Should().Equal("usb");
        policy.DiffSections(policy with { ProcessAllowlist = new ProcessAllowlistPolicy(AllowlistMode.Deny, new[] { "cmd.exe" }) }).Should().Equal("processAllowlist");
        policy.DiffSections(policy with { WebFilter = policy.WebFilter with { DnsServers = new[] { "8.8.8.8" } } }).Should().Equal("webFilter");
        policy.DiffSections(policy with { Explorer = policy.Explorer with { DisableAltTab = true } }).Should().Equal("explorer");
        policy.DiffSections(policy with { Anticheat = new AntiCheatPolicy(Array.Empty<AntiCheatKind>(), true) }).Should().Equal("anticheat");

        var changed = policy with
        {
            Kiosk = policy.Kiosk with { IdleTimeoutSec = 1 },
            ShellReplacement = policy.ShellReplacement with { Enabled = false },
            Power = new PowerPolicy(null, null),
            Updates = new UpdatesPolicy(UpdateChannel.Beta, false),
            Version = 13,
        };
        policy.DiffSections(changed).Should().Equal("shellReplacement", "power", "updates", "kiosk");

        var roundTripped = JsonDefaults.Deserialize<Policy>(JsonDefaults.Serialize(policy))!;
        roundTripped.DiffSections(policy).Should().BeEmpty();
        roundTripped.Power.ScheduledShutdown.Should().Be(new TimeOnly(6, 0));
    }

    [Fact]
    public void Tariff_PriceFor_rounds_up_to_the_minor_unit()
    {
        var hourly = SampleTariff(Money.Uzs(12000));
        hourly.PriceFor(60).Should().Be(Money.Uzs(12000));
        hourly.PriceFor(30).Should().Be(Money.Uzs(6000));
        hourly.PriceFor(1).Should().Be(Money.Uzs(200));
        hourly.PriceFor(0).Should().Be(Money.Uzs(0));
        SampleTariff(Money.Uzs(100)).PriceFor(7).Should().Be(Money.Uzs(12), "700 / 60 = 11.67 rounds up");
        SampleTariff(Money.Of(100, "USD")).PriceFor(30).Should().Be(Money.Of(50, "USD"));

        var package = SampleTariff(Money.Uzs(12000)) with { IsPackage = true, PackageMinutes = 180, PackagePrice = Money.Uzs(30000) };
        package.PriceFor(180).Should().Be(Money.Uzs(30000));
        package.PriceFor(1).Should().Be(Money.Uzs(30000), "packages have a fixed price");
        (package with { PackagePrice = null }).PriceFor(60).Should().Be(Money.Uzs(12000), "a package without a price falls back to the hourly rate");

        Action negative = () => _ = hourly.PriceFor(-1);
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Tariff_IsValidFor_checks_zone_and_time_windows()
    {
        var weekdays = new[] { Weekday.Mon, Weekday.Tue, Weekday.Wed, Weekday.Thu, Weekday.Fri };
        var tariff = SampleTariff(Money.Uzs(12000)) with
        {
            Zones = new[] { "Standard" },
            TimeWindows = new[] { new TariffTimeWindow(weekdays, new TimeOnly(10, 0), new TimeOnly(18, 0)) },
        };

        tariff.IsValidFor("Standard", Weekday.Mon, new TimeOnly(12, 0)).Should().BeTrue();
        tariff.IsValidFor("standard", Weekday.Mon, new TimeOnly(12, 0)).Should().BeTrue("zones compare case-insensitively");
        tariff.IsValidFor("VIP", Weekday.Mon, new TimeOnly(12, 0)).Should().BeFalse();
        tariff.IsValidFor("Standard", Weekday.Sat, new TimeOnly(12, 0)).Should().BeFalse();
        tariff.IsValidFor("Standard", Weekday.Mon, new TimeOnly(10, 0)).Should().BeTrue("the start is inclusive");
        tariff.IsValidFor("Standard", Weekday.Mon, new TimeOnly(18, 0)).Should().BeFalse("the end is exclusive");
        tariff.IsValidFor("Standard", Weekday.Mon, new TimeOnly(9, 59)).Should().BeFalse();

        var anywhereAnytime = SampleTariff(Money.Uzs(12000));
        anywhereAnytime.IsValidFor("VIP", Weekday.Sun, new TimeOnly(3, 0)).Should().BeTrue("no zones and no windows means always valid");

        var night = new TariffTimeWindow(new[] { Weekday.Fri }, new TimeOnly(22, 0), new TimeOnly(2, 0));
        night.Contains(Weekday.Fri, new TimeOnly(23, 0)).Should().BeTrue();
        night.Contains(Weekday.Sat, new TimeOnly(1, 0)).Should().BeTrue("a wrapped window continues into the next day");
        night.Contains(Weekday.Sat, new TimeOnly(2, 0)).Should().BeFalse();
        night.Contains(Weekday.Sat, new TimeOnly(23, 0)).Should().BeFalse();
        night.Contains(Weekday.Sun, new TimeOnly(1, 0)).Should().BeFalse();
        night.Contains(Weekday.Mon, new TimeOnly(1, 0)).Should().BeFalse();

        var monday = new TariffTimeWindow(new[] { Weekday.Mon }, new TimeOnly(0, 0), new TimeOnly(0, 0));
        monday.Contains(Weekday.Mon, new TimeOnly(0, 0)).Should().BeTrue("from == to covers the whole day");
        monday.Contains(Weekday.Mon, new TimeOnly(23, 59)).Should().BeTrue();
        monday.Contains(Weekday.Tue, new TimeOnly(0, 0)).Should().BeFalse();
        Weekday.Sun.ToDayOfWeek().Should().Be(DayOfWeek.Sunday);
        DayOfWeek.Monday.ToWeekday().Should().Be(Weekday.Mon);
    }

    [Theory]
    [InlineData(SessionState.Idle, false, false, false)]
    [InlineData(SessionState.Starting, true, false, false)]
    [InlineData(SessionState.Active, true, true, true)]
    [InlineData(SessionState.Paused, true, true, false)]
    [InlineData(SessionState.Locked, true, false, true)]
    [InlineData(SessionState.Ending, true, false, true)]
    [InlineData(SessionState.Ended, false, false, false)]
    public void Session_state_extensions(SessionState state, bool isOpen, bool allowsRequests, bool timerRunning)
    {
        state.IsOpen().Should().Be(isOpen);
        state.AllowsSessionRequests().Should().Be(allowsRequests);
        state.IsTimerRunning().Should().Be(timerRunning);
    }

    [Fact]
    public void Session_document_matches_protocol_example_and_omits_null_optionals()
    {
        const string example = """
            { "id": "9c1e0000-0000-4000-8000-000000000001", "userId": "3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b", "pcId": "7d2f1c3a-1111-4222-8333-444455556666", "state": "active",
              "startedAt": "2026-09-21T10:00:00.000Z", "endsAt": "2026-09-21T11:00:00.000Z", "pausedAt": null,
              "tariffId": "b1a5b6b4-3f6e-4b7f-8c26-1d0e2e5c9a10", "secondsLeft": 2700, "secondsUsed": 900,
              "cost": { "amount": 250000, "currency": "UZS" }, "isPrepaid": true, "warningsSent": [] }
            """;

        var session = JsonDefaults.Deserialize<Session>(example)!;
        session.Id.Should().Be(SessionId);
        session.State.Should().Be(SessionState.Active);
        session.EndsAt.Should().Be(new DateTimeOffset(2026, 9, 21, 11, 0, 0, TimeSpan.Zero));
        session.PausedAt.Should().BeNull();
        session.Cost.Should().Be(Money.Uzs(250000));
        session.WarningsSent.Should().BeEmpty();
        session.IsOpenEnded.Should().BeFalse();
        (session with { SecondsLeft = Session.OpenEnded }).IsOpenEnded.Should().BeTrue();

        var json = JsonDefaults.Serialize(session);
        json.Should().NotContain("pausedAt", "null optionals are omitted on write");
        json.Should().NotContain("isOpenEnded");
        json.Should().Contain("\"warningsSent\":[]");
        JsonDefaults.Serialize(JsonDefaults.Deserialize<Session>(json)!).Should().Be(json);
    }

    [Fact]
    public void Session_events_carry_typed_data()
    {
        var extended = SessionEvent.Extended(SessionId, Ts, 30, Money.Uzs(6000));
        JsonDefaults.Serialize(extended).Should().Be(
            "{\"sessionId\":\"9c1e0000-0000-4000-8000-000000000001\",\"type\":\"extended\",\"at\":\"2026-09-21T10:15:30.123Z\",\"data\":{\"minutes\":30,\"cost\":{\"amount\":6000,\"currency\":\"UZS\"}}}");
        JsonDefaults.Deserialize<SessionEvent>(JsonDefaults.Serialize(extended))!.DataAs<SessionExtendedData>().Should().Be(new SessionExtendedData(30, Money.Uzs(6000)));

        var started = SessionEvent.Of(SessionId, SessionEventType.Started, Ts);
        JsonDefaults.Serialize(started).Should().Be("{\"sessionId\":\"9c1e0000-0000-4000-8000-000000000001\",\"type\":\"started\",\"at\":\"2026-09-21T10:15:30.123Z\"}", "absent data is omitted");
        started.DataAs<SessionWarningData>().Should().BeNull();

        SessionEvent.Ended(SessionId, Ts, SessionEndReason.TimeUp).DataAs<SessionEndedData>().Should().Be(new SessionEndedData(SessionEndReason.TimeUp));
        SessionEvent.Warning(SessionId, Ts, 5).DataAs<SessionWarningData>().Should().Be(new SessionWarningData(5));
        SessionEvent.Charged(SessionId, Ts, Money.Uzs(1)).DataAs<SessionChargedData>().Should().Be(new SessionChargedData(Money.Uzs(1)));
    }

    #endregion

    #region AgentCommand

    [Fact]
    public void Every_agent_command_round_trips_through_its_ipc_name()
    {
        AgentCommands.All.Should().HaveCount(Enum.GetValues<AgentCommand>().Length);
        AgentCommands.Names.Should().OnlyHaveUniqueItems().And.HaveCount(AgentCommands.All.Count);

        foreach (var command in AgentCommands.All)
        {
            var name = command.ToIpcName();
            IpcEnvelope.IsValidName(name).Should().BeTrue("{0} must be '<domain>.<action>'", name);
            AgentCommands.IsKnown(name).Should().BeTrue();
            AgentCommands.Parse(name).Should().Be(command);
            AgentCommands.TryParse(name, out var parsed).Should().BeTrue();
            parsed.Should().Be(command);
            JsonDefaults.Serialize(command).Should().Be($"\"{name}\"");
            JsonDefaults.Deserialize<AgentCommand>($"\"{name}\"").Should().Be(command);
            command.ResponseName().Should().Be(IpcEnvelope.ResponseNameFor(name));
        }

        AgentCommand.SysPing.ResponseName().Should().Be(IpcMessages.Sys.Pong);
        AgentCommand.AuthLogin.ResponseName().Should().Be(IpcMessages.Auth.Login);
        AgentCommand.AuthHello.ToIpcName().Should().Be("auth.hello");
        AgentCommand.GamesInstallStatus.ToIpcName().Should().Be("games.installStatus");
        AgentCommand.SysAckAdminMessage.ToIpcName().Should().Be("sys.ackAdminMessage");
    }

    [Fact]
    public void Unknown_agent_command_names_are_rejected()
    {
        AgentCommands.TryParse("nope.nothing", out _).Should().BeFalse();
        AgentCommands.TryParse(null, out _).Should().BeFalse();
        AgentCommands.IsKnown("Auth.Hello").Should().BeFalse("names are case-sensitive");

        Action parse = () => AgentCommands.Parse("nope.nothing");
        parse.Should().Throw<ArgumentException>();
        Action unknownJson = () => _ = JsonDefaults.Deserialize<AgentCommand>("\"nope.nothing\"");
        unknownJson.Should().Throw<JsonException>();
        Action integerJson = () => _ = JsonDefaults.Deserialize<AgentCommand>("3");
        integerJson.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData(AgentCommand.AuthHello, IpcAuthLevel.None)]
    [InlineData(AgentCommand.SysPing, IpcAuthLevel.None)]
    [InlineData(AgentCommand.GamesList, IpcAuthLevel.Hello)]
    [InlineData(AgentCommand.WalletTariffs, IpcAuthLevel.Hello)]
    [InlineData(AgentCommand.SessionGet, IpcAuthLevel.User)]
    [InlineData(AgentCommand.WalletBalance, IpcAuthLevel.User)]
    [InlineData(AgentCommand.GamesLaunch, IpcAuthLevel.Session)]
    [InlineData(AgentCommand.ShopOrder, IpcAuthLevel.Session)]
    public void Agent_commands_declare_their_auth_level(AgentCommand command, IpcAuthLevel level)
    {
        command.RequiredAuth().Should().Be(level);
    }

    #endregion

    #region Null handling / wire rules

    [Fact]
    public void Null_optionals_are_omitted_but_contract_nulls_are_written()
    {
        JsonDefaults.Serialize(new LockCommand()).Should().Be("{}");
        JsonDefaults.Serialize(new SettingsSetRequest()).Should().Be("{}");
        new SettingsSetRequest().IsEmpty.Should().BeTrue();
        new SettingsSetRequest(Volume: 10).IsEmpty.Should().BeFalse();
        JsonDefaults.Serialize(new PolicyDeniedDetails("rule")).Should().Be("{\"rule\":\"rule\"}");

        JsonDefaults.Serialize(new ShellCommand(ShellCommandKind.Unlock, null, RequestId)).Should().Be(
            "{\"command\":\"unlock\",\"args\":null,\"commandId\":\"6f1d2c4e-1b3a-4a7c-9f7d-0e6f2b5a9c11\"}",
            "shell.command args are always present");
        ShellCommand.Of<ShellRebootArgs>(ShellCommandKind.Reboot, null, RequestId).Args.Should().BeNull();
        ShellCommand.Of(ShellCommandKind.Reboot, new ShellRebootArgs(10, "bye"), RequestId).ArgsAs<ShellRebootArgs>().Should().Be(new ShellRebootArgs(10, "bye"));

        new RefreshConfigCommand().IsAll.Should().BeTrue();
        new RefreshConfigCommand(Games: true).IsAll.Should().BeFalse();
        new RefreshConfigCommand(Games: false).IsAll.Should().BeTrue("explicit false selects nothing");
    }

    [Fact]
    public void Reads_are_case_insensitive_and_skip_unknown_members()
    {
        JsonDefaults.Deserialize<LockCommand>("{}").Should().Be(new LockCommand());
        JsonDefaults.Deserialize<LockCommand>("{\"reason\":null}").Should().Be(new LockCommand());
        JsonDefaults.Deserialize<LockCommand>("{\"REASON\":\"admin\",\"Message\":\"hi\"}").Should().Be(new LockCommand("admin", "hi"));
        JsonDefaults.Deserialize<ReasonDetails>("{\"reason\":\"a\",\"extra\":{\"nested\":[1,2]}}").Should().Be(new ReasonDetails("a"));
        JsonDefaults.Deserialize<PowerCommand>("{\"delaySec\":5,\"force\":true}").Should().Be(new PowerCommand(5, true));
    }

    [Theory]
    [InlineData("{\"retryAfterSec\":\"3\"}", "numbers are strict")]
    [InlineData("{\"retryAfterSec\":3,}", "trailing commas are rejected")]
    [InlineData("{/*c*/\"retryAfterSec\":3}", "comments are rejected")]
    [InlineData("{\"retryAfterSec\":3.5}", "integers do not accept fractions")]
    public void Strict_reader_rejects_lenient_json(string json, string because)
    {
        Action act = () => _ = JsonDefaults.Deserialize<RateLimitDetails>(json);
        act.Should().Throw<JsonException>(because);
    }

    [Fact]
    public void Non_ascii_text_is_written_as_raw_utf8()
    {
        var message = new ShowMessageArgs("Привет", "Salom, o'yinchi!", NotificationLevel.Info);
        var json = JsonDefaults.Serialize(message);
        json.Should().Be("{\"title\":\"Привет\",\"body\":\"Salom, o'yinchi!\",\"level\":\"info\"}");
        json.Should().NotContain("\\u");
        Encoding.UTF8.GetString(JsonDefaults.SerializeToUtf8Bytes(message)).Should().Be(json);
        JsonDefaults.SerializeToUtf8Bytes(message)[0].Should().Be((byte)'{', "no BOM");
        JsonDefaults.Deserialize<ShowMessageArgs>(json).Should().Be(message);
    }

    [Fact]
    public void DeserializeRequired_throws_on_null_document()
    {
        Action act = () => _ = JsonDefaults.DeserializeRequired<OkResponse>("null"u8);
        act.Should().Throw<JsonException>();
        JsonDefaults.DeserializeRequired<OkResponse>("{\"ok\":true}"u8).Should().Be(OkResponse.Instance);
        JsonDefaults.FromElement<OkResponse>(JsonDefaults.ToElement(OkResponse.Instance)).Should().Be(OkResponse.Instance);
    }

    #endregion

    #region Context coverage

    [Fact]
    public void ContractsJsonContext_covers_every_contract_type()
    {
        var candidates = typeof(JsonDefaults).Assembly.GetExportedTypes()
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Where(t => !typeof(Exception).IsAssignableFrom(t)
                && !typeof(JsonConverter).IsAssignableFrom(t)
                && !typeof(JsonSerializerContext).IsAssignableFrom(t)
                && !typeof(Attribute).IsAssignableFrom(t)
                && !typeof(Delegate).IsAssignableFrom(t))
            .Concat(new[] { typeof(PagedResult<Transaction>), typeof(PagedResult<Order>) })
            .ToList();

        candidates.Should().HaveCountGreaterThan(200, "the contract assembly is large; an empty scan means the filter is broken");
        candidates.Should().Contain(typeof(Money)).And.Contain(typeof(IpcEnvelope)).And.Contain(typeof(ErrorCode));
        candidates.Should().NotContain(typeof(IpcException)).And.NotContain(typeof(MoneyJsonConverter)).And.NotContain(typeof(JsonDefaults));

        var missing = candidates.Where(t => JsonDefaults.Context.GetTypeInfo(t) is null).Select(t => t.FullName).ToList();
        missing.Should().BeEmpty("every public contract type must be registered with [JsonSerializable] for AOT/trim safety");

        JsonDefaults.Context.GetTypeInfo(typeof(SerializationTests)).Should().BeNull("types outside the contract assembly are not resolvable");
        JsonDefaults.Options.IsReadOnly.Should().BeTrue();
    }

    #endregion

    private static Policy SamplePolicy() => new(
        12,
        Ts,
        new ShellReplacementPolicy(true, "clubshell-shell.exe"),
        new ProcessAllowlistPolicy(AllowlistMode.Deny, new[] { "cmd.exe", "regedit.exe" }),
        new UsbPolicy(false, true),
        new WebFilterPolicy(true, new[] { "example.com" }, Array.Empty<string>(), new[] { "1.1.1.1" }),
        new ExplorerPolicy(true, true, true, true, false, true, new[] { "Ctrl+Alt+Del" }),
        new PowerPolicy(30, new TimeOnly(6, 0)),
        new UpdatesPolicy(UpdateChannel.Stable, true),
        new AntiCheatPolicy(new[] { AntiCheatKind.Eac }, true),
        new KioskPolicy(300, 600, true));

    private static Tariff SampleTariff(Money pricePerHour) => new(
        TariffId,
        "Standard",
        pricePerHour,
        15,
        480,
        Array.Empty<string>(),
        Array.Empty<TariffTimeWindow>(),
        false);

    /// <summary>Re-serializes <paramref name="json"/> compactly with object keys sorted, so that documents can be compared regardless of key order and whitespace.</summary>
    private static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>(json.Length);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
