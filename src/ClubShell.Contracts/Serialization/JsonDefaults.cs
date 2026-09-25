using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Shop;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Serialization;

/// <summary>
/// The one wire format of ClubShell (ARCHITECTURE.md §1.2): camelCase properties, camelCase string enums,
/// ISO-8601 UTC timestamps with milliseconds and <c>Z</c>, <c>HH:mm</c> club times, nulls omitted, case-insensitive
/// reads, unknown members ignored, UTF-8 without escaping of non-ASCII. All helpers are AOT/trim safe because they
/// resolve contracts exclusively through <see cref="ContractsJsonContext"/>.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// Read-only options used for every contract (de)serialization. <see cref="JsonSerializerOptions.TypeInfoResolver"/>
    /// is <see cref="ContractsJsonContext.Default"/>; types outside the contract assembly are not resolvable.
    /// </summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>The source-generated context backing <see cref="Options"/>.</summary>
    public static ContractsJsonContext Context => ContractsJsonContext.Default;

    /// <summary>Contract metadata for <typeparamref name="T"/>. Throws <see cref="NotSupportedException"/> for types outside the contract.</summary>
    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    /// <summary>Serializes to a compact JSON string.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, TypeInfo<T>());

    /// <summary>Serializes to compact UTF-8 bytes (no BOM, no trailing newline) — the pipe frame body.</summary>
    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>());

    /// <summary>Serializes into an existing writer.</summary>
    public static void Serialize<T>(Utf8JsonWriter writer, T value) => JsonSerializer.Serialize(writer, value, TypeInfo<T>());

    /// <summary>Serializes asynchronously to a stream.</summary>
    public static Task SerializeAsync<T>(Stream utf8Json, T value, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(utf8Json, value, TypeInfo<T>(), cancellationToken);

    /// <summary>Serializes to a detached <see cref="JsonElement"/> (for <c>payload</c> / <c>details</c> slots).</summary>
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());

    /// <summary>Deserializes a JSON string.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>());

    /// <summary>Deserializes UTF-8 bytes.</summary>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json) => JsonSerializer.Deserialize(utf8Json, TypeInfo<T>());

    /// <summary>Deserializes from a reader positioned at the value.</summary>
    public static T? Deserialize<T>(ref Utf8JsonReader reader) => JsonSerializer.Deserialize(ref reader, TypeInfo<T>());

    /// <summary>Deserializes asynchronously from a stream.</summary>
    public static ValueTask<T?> DeserializeAsync<T>(Stream utf8Json, CancellationToken cancellationToken = default) =>
        JsonSerializer.DeserializeAsync(utf8Json, TypeInfo<T>(), cancellationToken);

    /// <summary>Deserializes a <see cref="JsonElement"/>.</summary>
    public static T? FromElement<T>(JsonElement element) => element.Deserialize(TypeInfo<T>());

    /// <summary>Deserializes an envelope-style body strictly: throws <see cref="JsonException"/> on <see langword="null"/> or on failure.</summary>
    public static T DeserializeRequired<T>(ReadOnlySpan<byte> utf8Json)
    {
        var value = Deserialize<T>(utf8Json);
        if (value is null)
        {
            throw new JsonException($"Expected a {typeof(T).Name} object, got null");
        }

        return value;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.Strict,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            // Frames never reach an HTML context; emit Cyrillic/Uzbek text as raw UTF-8 like serde and JSON.stringify do.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 32,
            TypeInfoResolver = ContractsJsonContext.Default,
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.Converters.Add(new ClubTimeConverter());
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>
/// <see cref="JsonStringEnumConverter{TEnum}"/> with the camelCase naming policy and integer values rejected.
/// Applied to every contract enum via <see cref="JsonConverterAttribute"/> so that both reflection and
/// source-generated paths agree on the wire form (<c>battleNet</c>, <c>insufficientFunds</c>, …).
/// </summary>
/// <typeparam name="TEnum">Enum type.</typeparam>
public sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>Creates the converter.</summary>
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Writes <see cref="DateTimeOffset"/> as ISO-8601 UTC with exactly three fractional digits and a <c>Z</c> suffix
/// (<c>2026-09-21T10:15:30.123Z</c>); reads any ISO-8601 offset form and normalizes to UTC.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected an ISO-8601 timestamp string");
        }

        if (reader.TryGetDateTimeOffset(out var value))
        {
            return value.ToUniversalTime();
        }

        var text = reader.GetString();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            return value;
        }

        throw new JsonException($"Invalid ISO-8601 timestamp '{text}'");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        Span<char> buffer = stackalloc char[32];
        var formatted = value.UtcDateTime.TryFormat(buffer, out var written, Format, CultureInfo.InvariantCulture);
        Debug.Assert(formatted, "32 chars always suffice for the fixed format");
        writer.WriteStringValue(buffer[..written]);
    }
}

/// <summary>
/// Writes <see cref="TimeOnly"/> as club local time <c>HH:mm</c> (tariff windows, shutdown schedule, apply windows);
/// reads <c>HH:mm</c>, <c>H:mm</c>, <c>HH:mm:ss</c> and <c>HH:mm:ss.fffffff</c>.
/// </summary>
public sealed class ClubTimeConverter : JsonConverter<TimeOnly>
{
    private static readonly string[] ReadFormats = { "HH:mm", "H:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF" };

    /// <inheritdoc />
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected an HH:mm time string");
        }

        var text = reader.GetString();
        if (TimeOnly.TryParseExact(text, ReadFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return value;
        }

        throw new JsonException($"Invalid time '{text}', expected HH:mm");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
    {
        Span<char> buffer = stackalloc char[8];
        var formatted = value.TryFormat(buffer, out var written, "HH:mm", CultureInfo.InvariantCulture);
        Debug.Assert(formatted, "8 chars always suffice for HH:mm");
        writer.WriteStringValue(buffer[..written]);
    }
}

/// <summary>
/// Source-generated metadata for every contract type (AOT/trim safe). Options mirror <see cref="JsonDefaults.Options"/>
/// except the encoder, which the attribute cannot express; prefer <see cref="JsonDefaults"/> helpers.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    NumberHandling = JsonNumberHandling.Strict,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    GenerationMode = JsonSourceGenerationMode.Default,
    Converters = new[] { typeof(UtcDateTimeOffsetConverter), typeof(ClubTimeConverter) })]

// Primitives that appear as top-level payloads / details.
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<int>))]
[JsonSerializable(typeof(IReadOnlyList<Guid>))]

// Serialization / errors / IPC core.
[JsonSerializable(typeof(Money))]
[JsonSerializable(typeof(ErrorCode))]
[JsonSerializable(typeof(ServerErrorEnvelope))]
[JsonSerializable(typeof(ServerError))]
[JsonSerializable(typeof(IpcKind))]
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(IpcError))]
[JsonSerializable(typeof(ValidationDetails))]
[JsonSerializable(typeof(ReasonDetails))]
[JsonSerializable(typeof(NameDetails))]
[JsonSerializable(typeof(RateLimitDetails))]
[JsonSerializable(typeof(InsufficientFundsDetails))]
[JsonSerializable(typeof(PolicyDeniedDetails))]
[JsonSerializable(typeof(AntiCheatBlockedDetails))]
[JsonSerializable(typeof(LaunchFailedDetails))]
[JsonSerializable(typeof(VersionMismatchDetails))]
[JsonSerializable(typeof(TraceDetails))]
[JsonSerializable(typeof(AgentCommand))]
[JsonSerializable(typeof(IpcAuthLevel))]

// IPC payloads.
[JsonSerializable(typeof(PagedResult<Transaction>))]
[JsonSerializable(typeof(PagedResult<Order>))]
[JsonSerializable(typeof(OkResponse))]
[JsonSerializable(typeof(AuthHelloRequest))]
[JsonSerializable(typeof(AuthHelloResponse))]
[JsonSerializable(typeof(AuthLoginRequest))]
[JsonSerializable(typeof(AuthLoginResponse))]
[JsonSerializable(typeof(AuthLogoutRequest))]
[JsonSerializable(typeof(AuthLogoutResponse))]
[JsonSerializable(typeof(AuthStatusResponse))]
[JsonSerializable(typeof(SessionStartRequest))]
[JsonSerializable(typeof(SessionPauseRequest))]
[JsonSerializable(typeof(SessionEndRequest))]
[JsonSerializable(typeof(SessionExtendRequest))]
[JsonSerializable(typeof(SessionLockRequest))]
[JsonSerializable(typeof(SessionUnlockRequest))]
[JsonSerializable(typeof(SessionTimeLeftResponse))]
[JsonSerializable(typeof(GamesSort))]
[JsonSerializable(typeof(GamesListRequest))]
[JsonSerializable(typeof(GamesListResponse))]
[JsonSerializable(typeof(GamesGetRequest))]
[JsonSerializable(typeof(GamesLaunchRequest))]
[JsonSerializable(typeof(GamesKillRequest))]
[JsonSerializable(typeof(GamesKillResponse))]
[JsonSerializable(typeof(GamesRunningResponse))]
[JsonSerializable(typeof(GamesInstallStatusRequest))]
[JsonSerializable(typeof(GameInstallStatus))]
[JsonSerializable(typeof(AppsListResponse))]
[JsonSerializable(typeof(AppsLaunchRequest))]
[JsonSerializable(typeof(AppsLaunchResponse))]
[JsonSerializable(typeof(WalletTariffsRequest))]
[JsonSerializable(typeof(WalletTariffsResponse))]
[JsonSerializable(typeof(TariffsResponse))]
[JsonSerializable(typeof(WalletHistoryRequest))]
[JsonSerializable(typeof(WalletHistoryResponse))]
[JsonSerializable(typeof(WalletTopupIntentRequest))]
[JsonSerializable(typeof(ShopProductsRequest))]
[JsonSerializable(typeof(ShopProductsResponse))]
[JsonSerializable(typeof(ShopOrderRequest))]
[JsonSerializable(typeof(ShopOrderStatusRequest))]
[JsonSerializable(typeof(ShopOrdersRequest))]
[JsonSerializable(typeof(ShopOrdersResponse))]
[JsonSerializable(typeof(ChatHistoryRequest))]
[JsonSerializable(typeof(ChatHistoryResponse))]
[JsonSerializable(typeof(ChatSendRequest))]
[JsonSerializable(typeof(ChatMarkReadRequest))]
[JsonSerializable(typeof(ChatMarkReadResponse))]
[JsonSerializable(typeof(BookingSeatsRequest))]
[JsonSerializable(typeof(BookingSeatsResponse))]
[JsonSerializable(typeof(BookingReserveRequest))]
[JsonSerializable(typeof(BookingCancelRequest))]
[JsonSerializable(typeof(TournamentsListRequest))]
[JsonSerializable(typeof(TournamentsListResponse))]
[JsonSerializable(typeof(TournamentsJoinRequest))]
[JsonSerializable(typeof(TournamentsLeaderboardRequest))]
[JsonSerializable(typeof(TournamentsLeaderboardResponse))]
[JsonSerializable(typeof(ProfileAchievementsResponse))]
[JsonSerializable(typeof(ShellFeatures))]
[JsonSerializable(typeof(ShellSettings))]
[JsonSerializable(typeof(SettingsSetRequest))]
[JsonSerializable(typeof(CallAdminCategory))]
[JsonSerializable(typeof(ClientErrorLevel))]
[JsonSerializable(typeof(SysPingRequest))]
[JsonSerializable(typeof(SysPongResponse))]
[JsonSerializable(typeof(SysHardwareRequest))]
[JsonSerializable(typeof(SysCallAdminRequest))]
[JsonSerializable(typeof(SysCallAdminResponse))]
[JsonSerializable(typeof(SysPowerRequest))]
[JsonSerializable(typeof(SysLockScreenRequest))]
[JsonSerializable(typeof(SetVolumeRequest))]
[JsonSerializable(typeof(VolumeState))]
[JsonSerializable(typeof(SysSetLocaleRequest))]
[JsonSerializable(typeof(SysSetLocaleResponse))]
[JsonSerializable(typeof(SysUnlockAdminRequest))]
[JsonSerializable(typeof(SysUnlockAdminResponse))]
[JsonSerializable(typeof(SysLogClientErrorRequest))]
[JsonSerializable(typeof(SysAckAdminMessageRequest))]
[JsonSerializable(typeof(PolicySource))]
[JsonSerializable(typeof(PolicyReloadRequest))]
[JsonSerializable(typeof(PolicyReloadResponse))]
[JsonSerializable(typeof(ComponentVersions))]
[JsonSerializable(typeof(UpdateCheckResponse))]
[JsonSerializable(typeof(UpdateApplyRequest))]
[JsonSerializable(typeof(UpdateApplyResponse))]

// IPC events.
[JsonSerializable(typeof(RemoteControlState))]
[JsonSerializable(typeof(ShellCommandKind))]
[JsonSerializable(typeof(AdMediaType))]
[JsonSerializable(typeof(AdminMessage))]
[JsonSerializable(typeof(RemoteControlEvent))]
[JsonSerializable(typeof(GameStateChanged))]
[JsonSerializable(typeof(PolicyChanged))]
[JsonSerializable(typeof(UpdateAvailable))]
[JsonSerializable(typeof(UpdateProgress))]
[JsonSerializable(typeof(UpdateReady))]
[JsonSerializable(typeof(ConnectivityEvent))]
[JsonSerializable(typeof(ShellRebootArgs))]
[JsonSerializable(typeof(AdItem))]
[JsonSerializable(typeof(ShowAdsArgs))]
[JsonSerializable(typeof(ShowMessageArgs))]
[JsonSerializable(typeof(ShellCommand))]
[JsonSerializable(typeof(AuthExpired))]

// Commands / WS / REST agent surface.
[JsonSerializable(typeof(UpdateChannel))]
[JsonSerializable(typeof(UpdateComponent))]
[JsonSerializable(typeof(UpdatePhase))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(ServerCommandType))]
[JsonSerializable(typeof(ServerCommand))]
[JsonSerializable(typeof(ServerCommandEnvelope))]
[JsonSerializable(typeof(ServerCommandsResponse))]
[JsonSerializable(typeof(CommandAck))]
[JsonSerializable(typeof(LockCommand))]
[JsonSerializable(typeof(MessageCommand))]
[JsonSerializable(typeof(MessageDeliveryResult))]
[JsonSerializable(typeof(PowerCommand))]
[JsonSerializable(typeof(ScheduledResult))]
[JsonSerializable(typeof(WakeCommand))]
[JsonSerializable(typeof(EndSessionCommand))]
[JsonSerializable(typeof(ExtendSessionCommand))]
[JsonSerializable(typeof(SessionResult))]
[JsonSerializable(typeof(KillGameCommand))]
[JsonSerializable(typeof(SetPolicyResult))]
[JsonSerializable(typeof(ReloadPolicyResult))]
[JsonSerializable(typeof(ScreenshotCommand))]
[JsonSerializable(typeof(ScreenshotResult))]
[JsonSerializable(typeof(RemoteControlStartCommand))]
[JsonSerializable(typeof(RemoteControlStartResult))]
[JsonSerializable(typeof(RemoteControlStopCommand))]
[JsonSerializable(typeof(RemoteControlStopResult))]
[JsonSerializable(typeof(UpdateCommand))]
[JsonSerializable(typeof(RefreshConfigCommand))]
[JsonSerializable(typeof(RefreshConfigResult))]
[JsonSerializable(typeof(AgentEventType))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(GameLaunchedEvent))]
[JsonSerializable(typeof(GameExitedEvent))]
[JsonSerializable(typeof(HardwareChangedEvent))]
[JsonSerializable(typeof(OfflineQueueFlushedEvent))]
[JsonSerializable(typeof(WsFrameType))]
[JsonSerializable(typeof(WsPushKind))]
[JsonSerializable(typeof(PcStatusChangedPush))]
[JsonSerializable(typeof(UserRevokedPush))]
[JsonSerializable(typeof(WsAck))]
[JsonSerializable(typeof(WsFrame))]
[JsonSerializable(typeof(AgentRegisterRequest))]
[JsonSerializable(typeof(AgentRegisterResponse))]
[JsonSerializable(typeof(AgentRefreshRequest))]
[JsonSerializable(typeof(AgentRefreshResponse))]
[JsonSerializable(typeof(HeartbeatRunningGame))]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(HeartbeatResponse))]
[JsonSerializable(typeof(TelemetryEvent))]
[JsonSerializable(typeof(TelemetryBatch))]
[JsonSerializable(typeof(CallAdminTicketRequest))]

// Games.
[JsonSerializable(typeof(LauncherType))]
[JsonSerializable(typeof(AntiCheatKind))]
[JsonSerializable(typeof(AntiCheatSeverity))]
[JsonSerializable(typeof(AntiCheatAction))]
[JsonSerializable(typeof(AntiCheatCheckResult))]
[JsonSerializable(typeof(AntiCheatReport))]
[JsonSerializable(typeof(GameMinSpec))]
[JsonSerializable(typeof(Game))]
[JsonSerializable(typeof(App))]
[JsonSerializable(typeof(GameState))]
[JsonSerializable(typeof(Resolution))]
[JsonSerializable(typeof(LaunchRequest))]
[JsonSerializable(typeof(LaunchResult))]
[JsonSerializable(typeof(RunningGame))]
[JsonSerializable(typeof(CloudSaveDownload))]
[JsonSerializable(typeof(AccountLease))]
[JsonSerializable(typeof(AccountLeaseReleaseReason))]
[JsonSerializable(typeof(CloudSaveUpload))]
[JsonSerializable(typeof(AccountLeaseRelease))]
[JsonSerializable(typeof(SaveUploadTarget))]
[JsonSerializable(typeof(LaunchReportPhase))]
[JsonSerializable(typeof(LaunchReport))]

// PCs / policy / theme / config.
[JsonSerializable(typeof(DiskType))]
[JsonSerializable(typeof(CpuInfo))]
[JsonSerializable(typeof(GpuInfo))]
[JsonSerializable(typeof(DiskInfo))]
[JsonSerializable(typeof(MonitorInfo))]
[JsonSerializable(typeof(NetworkInfo))]
[JsonSerializable(typeof(OsInfo))]
[JsonSerializable(typeof(PeripheralInfo))]
[JsonSerializable(typeof(HardwareInfo))]
[JsonSerializable(typeof(PcStatus))]
[JsonSerializable(typeof(ConnectivityState))]
[JsonSerializable(typeof(Temperatures))]
[JsonSerializable(typeof(NetworkThroughput))]
[JsonSerializable(typeof(PcMetrics))]
[JsonSerializable(typeof(Pc))]
[JsonSerializable(typeof(PcsResponse))]
[JsonSerializable(typeof(PcInfo))]
[JsonSerializable(typeof(AllowlistMode))]
[JsonSerializable(typeof(ShellReplacementPolicy))]
[JsonSerializable(typeof(ProcessAllowlistPolicy))]
[JsonSerializable(typeof(UsbPolicy))]
[JsonSerializable(typeof(WebFilterPolicy))]
[JsonSerializable(typeof(ExplorerPolicy))]
[JsonSerializable(typeof(PowerPolicy))]
[JsonSerializable(typeof(UpdatesPolicy))]
[JsonSerializable(typeof(AntiCheatPolicy))]
[JsonSerializable(typeof(KioskPolicy))]
[JsonSerializable(typeof(Policy))]
[JsonSerializable(typeof(ThemeColors))]
[JsonSerializable(typeof(Theme))]
[JsonSerializable(typeof(ThemeRef))]
[JsonSerializable(typeof(TimeWindow))]
[JsonSerializable(typeof(UpdatesConfigOverride))]
[JsonSerializable(typeof(ShellConfigOverride))]
[JsonSerializable(typeof(ShellClub))]
[JsonSerializable(typeof(ClubBanner))]
[JsonSerializable(typeof(ClubRules))]
[JsonSerializable(typeof(AgentServerConfig))]

// Sessions.
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(SessionWarning))]
[JsonSerializable(typeof(SessionEventType))]
[JsonSerializable(typeof(SessionEndReason))]
[JsonSerializable(typeof(SessionEvent))]
[JsonSerializable(typeof(SessionExtendedData))]
[JsonSerializable(typeof(SessionWarningData))]
[JsonSerializable(typeof(SessionChargedData))]
[JsonSerializable(typeof(SessionEndedData))]
[JsonSerializable(typeof(SessionEndResult))]
[JsonSerializable(typeof(SessionEndedEvent))]
[JsonSerializable(typeof(SessionStartedEvent))]
[JsonSerializable(typeof(SessionCreateRequest))]
[JsonSerializable(typeof(SessionEndReport))]
[JsonSerializable(typeof(SessionEventsBatch))]

// Shop.
[JsonSerializable(typeof(ProductCategory))]
[JsonSerializable(typeof(Product))]
[JsonSerializable(typeof(OrderStatus))]
[JsonSerializable(typeof(OrderItem))]
[JsonSerializable(typeof(OrderLineRequest))]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(OrderCreateRequest))]

// Users.
[JsonSerializable(typeof(AuthKind))]
[JsonSerializable(typeof(QrStatus))]
[JsonSerializable(typeof(AuthExpiredReason))]
[JsonSerializable(typeof(AuthRequest))]
[JsonSerializable(typeof(AuthResponse))]
[JsonSerializable(typeof(QrStartRequest))]
[JsonSerializable(typeof(QrLoginStart))]
[JsonSerializable(typeof(QrLoginStatus))]
[JsonSerializable(typeof(GuestAuthRequest))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(UserRole))]
[JsonSerializable(typeof(Locale))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(ProfileUpdateRequest))]
[JsonSerializable(typeof(FavoriteGame))]
[JsonSerializable(typeof(UserStats))]
[JsonSerializable(typeof(AchievementProgress))]
[JsonSerializable(typeof(Achievement))]
[JsonSerializable(typeof(Loyalty))]
[JsonSerializable(typeof(NotificationLevel))]
[JsonSerializable(typeof(NotificationAction))]
[JsonSerializable(typeof(Notification))]
[JsonSerializable(typeof(ChatMessageKind))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatPostRequest))]
[JsonSerializable(typeof(ChatReadRequest))]
[JsonSerializable(typeof(BookingStatus))]
[JsonSerializable(typeof(Seat))]
[JsonSerializable(typeof(Booking))]
[JsonSerializable(typeof(BookingCreateRequest))]
[JsonSerializable(typeof(TournamentState))]
[JsonSerializable(typeof(BracketMatch))]
[JsonSerializable(typeof(BracketRound))]
[JsonSerializable(typeof(Bracket))]
[JsonSerializable(typeof(Tournament))]
[JsonSerializable(typeof(LeaderboardEntry))]

// Wallet.
[JsonSerializable(typeof(Balance))]
[JsonSerializable(typeof(Weekday))]
[JsonSerializable(typeof(TariffTimeWindow))]
[JsonSerializable(typeof(Tariff))]
[JsonSerializable(typeof(TransactionType))]
[JsonSerializable(typeof(TopupProvider))]
[JsonSerializable(typeof(TopupStatus))]
[JsonSerializable(typeof(Transaction))]
[JsonSerializable(typeof(TopupIntent))]
[JsonSerializable(typeof(TopupIntentCreateRequest))]
public sealed partial class ContractsJsonContext : JsonSerializerContext
{
}
