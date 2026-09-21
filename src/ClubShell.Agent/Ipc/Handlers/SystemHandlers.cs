using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Power;
using ClubShell.Agent.Remote;
using ClubShell.Agent.Server;
using ClubShell.Agent.Session;
using ClubShell.Agent.Updates;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Windows.Hardware;
using ClubShell.Windows.Network;
using Microsoft.Extensions.Options;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Ipc.Handlers;

/// <summary><c>sys.*</c> and <c>update.*</c> requests (IPC_PROTOCOL.md §7.12, §7.14).</summary>
[SupportedOSPlatform("windows")]
public sealed class SystemHandlers : IIpcHandlerGroup
{
    /// <summary>Minimum interval between <c>sys.callAdmin</c> tickets.</summary>
    public static readonly TimeSpan CallAdminInterval = TimeSpan.FromSeconds(30);

    /// <summary>Lifetime of a token issued by <c>sys.unlockAdmin</c>.</summary>
    public static readonly TimeSpan AdminTokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Failed <c>sys.unlockAdmin</c> attempts allowed per minute.</summary>
    public const int MaxUnlockAdminAttemptsPerMinute = 3;

    /// <summary><c>sys.logClientError</c> entries accepted per second; the rest are dropped silently.</summary>
    public const int MaxClientErrorsPerSecond = 10;

    /// <summary>Longest delay accepted for <c>sys.reboot</c> / <c>sys.shutdown</c>.</summary>
    public const int MaxPowerDelaySec = 3600;

    /// <summary>Telemetry kind of an admin call queued while offline.</summary>
    public const string CallAdminTelemetryKind = "callAdmin";

    /// <summary>Telemetry kind of a Shell client error.</summary>
    public const string ClientErrorTelemetryKind = "shellClientError";

    private static readonly TimeSpan PcCacheTtl = TimeSpan.FromSeconds(60);

    private readonly ServerConnection _connection;
    private readonly IServerClient _server;
    private readonly HardwareInventory _hardware;
    private readonly PerformanceCounters _counters;
    private readonly IpcEventBridge _bridge;
    private readonly PowerCommands _power;
    private readonly ISessionService _sessions;
    private readonly ShellSettingsStore _shellSettings;
    private readonly ShellUserContext _users;
    private readonly AdminMessageService _adminMessages;
    private readonly AgentUpdater _updater;
    private readonly TelemetryBus _telemetry;
    private readonly NetworkProbe _network;
    private readonly IPolicyEnforcer _policy;
    private readonly IKioskCredentials _kiosk;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<SystemHandlers> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _adminTokens = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _unlockFailures = new();
    private readonly object _gate = new();
    private DateTimeOffset? _lastCallAdminAt;
    private DateTimeOffset _clientErrorWindow;
    private int _clientErrorsInWindow;
    private Pc? _cachedPc;
    private DateTimeOffset _cachedPcAt;

    /// <summary>Creates the group.</summary>
    public SystemHandlers(
        ServerConnection connection,
        IServerClient server,
        HardwareInventory hardware,
        PerformanceCounters counters,
        IpcEventBridge bridge,
        PowerCommands power,
        ISessionService sessions,
        ShellSettingsStore shellSettings,
        ShellUserContext users,
        AdminMessageService adminMessages,
        AgentUpdater updater,
        TelemetryBus telemetry,
        NetworkProbe network,
        IPolicyEnforcer policy,
        IKioskCredentials kiosk,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<SystemHandlers> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(shellSettings);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(adminMessages);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _connection = connection;
        _server = server;
        _hardware = hardware;
        _counters = counters;
        _bridge = bridge;
        _power = power;
        _sessions = sessions;
        _shellSettings = shellSettings;
        _users = users;
        _adminMessages = adminMessages;
        _updater = updater;
        _telemetry = telemetry;
        _network = network;
        _policy = policy;
        _kiosk = kiosk;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Register(MessageDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.Register<SysPingRequest, SysPongResponse>(IpcMessages.Sys.Ping, (_, request, _) => Task.FromResult(new SysPongResponse(request.Seq, request.SentAt, _clock.UtcNow, _connection.Connectivity)));
        dispatcher.RegisterNoPayload<PcInfo>(IpcMessages.Sys.PcInfo, PcInfoAsync);
        dispatcher.RegisterOptional<SysHardwareRequest, HardwareInfo>(IpcMessages.Sys.Hardware, (_, request, cancellationToken) => request?.Refresh == true ? _hardware.RefreshAsync(cancellationToken) : _hardware.GetAsync(cancellationToken));
        dispatcher.RegisterNoPayload<PcMetrics>(IpcMessages.Sys.Metrics, MetricsAsync);
        dispatcher.Register<SysCallAdminRequest, SysCallAdminResponse>(IpcMessages.Sys.CallAdmin, CallAdminAsync);
        dispatcher.RegisterOptional<SysPowerRequest, ScheduledResult>(IpcMessages.Sys.Reboot, (_, request, cancellationToken) => PowerAsync(reboot: true, request, cancellationToken));
        dispatcher.RegisterOptional<SysPowerRequest, ScheduledResult>(IpcMessages.Sys.Shutdown, (_, request, cancellationToken) => PowerAsync(reboot: false, request, cancellationToken));
        dispatcher.RegisterOptional<SysLockScreenRequest, OkResponse>(IpcMessages.Sys.LockScreen, LockScreenAsync);
        dispatcher.Register<SetVolumeRequest, VolumeState>(IpcMessages.Sys.SetVolume, (_, request, _) => Task.FromResult(SetVolume(request)));
        dispatcher.Register<SysSetLocaleRequest, SysSetLocaleResponse>(IpcMessages.Sys.SetLocale, SetLocaleAsync);
        dispatcher.Register<SysUnlockAdminRequest, SysUnlockAdminResponse>(IpcMessages.Sys.UnlockAdmin, (_, request, _) => Task.FromResult(UnlockAdmin(request)));
        dispatcher.Register<SysLogClientErrorRequest, OkResponse>(IpcMessages.Sys.LogClientError, (_, request, _) => Task.FromResult(LogClientError(request)));
        dispatcher.Register<SysAckAdminMessageRequest, OkResponse>(IpcMessages.Sys.AckAdminMessage, (_, request, _) => Task.FromResult(AckAdminMessage(request)));
        dispatcher.RegisterNoPayload<UpdateCheckResponse>(IpcMessages.Update.Check, UpdateCheckAsync);
        dispatcher.Register<UpdateApplyRequest, UpdateApplyResponse>(IpcMessages.Update.Apply, UpdateApplyAsync);
    }

    /// <summary><see langword="true"/> while <paramref name="token"/> was issued by <c>sys.unlockAdmin</c> and has not expired.</summary>
    public bool IsAdminTokenValid(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        PruneAdminTokens(_clock.UtcNow);
        return _adminTokens.TryGetValue(token, out DateTimeOffset expiresAt) && expiresAt > _clock.UtcNow;
    }

    /// <summary>Best-effort master volume on the default render endpoint (Core Audio); persisted to <c>shell.json</c> either way.</summary>
    public VolumeState SetVolume(SetVolumeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Level is < 0 or > 100)
        {
            throw IpcError.Validation("level", "range").ToException();
        }

        bool muted = request.Muted ?? _shellSettings.Get().Muted;
        if (!CoreAudioVolume.TryApply(request.Level, muted, _logger))
        {
            // Session 0 has no console audio endpoint; the Shell applies the persisted value in the user session.
            _logger.LogDebug("Core Audio endpoint unavailable from the service; volume {Level} (muted {Muted}) persisted only", request.Level, muted);
        }

        return _shellSettings.SetVolume(request.Level, muted);
    }

    // ---- sys --------------------------------------------------------------------------------

    private async Task<PcInfo> PcInfoAsync(IpcContext context, CancellationToken cancellationToken)
    {
        Pc pc = await GetPcAsync(cancellationToken).ConfigureAwait(false);
        return new PcInfo(
            pc,
            ClubShellVersion.Current,
            ShellVersion,
            IpcEnvelope.CurrentVersion,
            Environment.TickCount64 / 1000,
            _kiosk.UserName,
            _connection.Connectivity,
            _server.ServerNow,
            _policy.Current?.Version ?? 0);
    }

    private async Task<PcMetrics> MetricsAsync(IpcContext context, CancellationToken cancellationToken) =>
        _bridge.LastMetrics ?? await _counters.SampleAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SysCallAdminResponse> CallAdminAsync(IpcContext context, SysCallAdminRequest request, CancellationToken cancellationToken)
    {
        if (!_shellSettings.Features.CallAdmin)
        {
            throw IpcError.PolicyDenied("features.callAdmin").ToException();
        }

        string? message = request.Message?.Trim();
        if (message is { Length: > 500 })
        {
            throw IpcError.Validation("message", "max").ToException();
        }

        DateTimeOffset now = _clock.UtcNow;
        lock (_gate)
        {
            if (_lastCallAdminAt is { } last && now - last < CallAdminInterval)
            {
                int retryAfter = (int)Math.Ceiling((CallAdminInterval - (now - last)).TotalSeconds);
                throw IpcError.RateLimited(Math.Max(1, retryAfter)).ToException();
            }

            _lastCallAdminAt = now;
        }

        var ticket = new CallAdminTicketRequest(PcId, context.User?.Id, request.Category, string.IsNullOrEmpty(message) ? null : message, now);
        if (_connection.Connectivity == ConnectivityState.Online)
        {
            try
            {
                SysCallAdminResponse response = await _server.CallAdminAsync(ticket, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Admin called ({Category}); ticket {TicketId}, queue position {Position}", request.Category, response.TicketId, response.QueuePosition);
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is ServerApiException { IsRetryable: true })
            {
                _logger.LogWarning("sys.callAdmin: server unavailable; ticket queued in telemetry");
            }
        }

        _ = _telemetry.Publish(CallAdminTelemetryKind, JsonDefaults.ToElement(ticket));
        var queued = new SysCallAdminResponse(Guid.NewGuid(), now);
        _logger.LogInformation("Admin call ({Category}) queued offline as {TicketId}", request.Category, queued.TicketId);
        return queued;
    }

    private Task<ScheduledResult> PowerAsync(bool reboot, SysPowerRequest? request, CancellationToken cancellationToken)
    {
        if (!_settings.CurrentValue.Power.AllowShellReboot)
        {
            throw IpcError.PolicyDenied("power.allowShellReboot").ToException();
        }

        if (request?.DelaySec is { } delay && delay is < 0 or > MaxPowerDelaySec)
        {
            throw IpcError.Validation("delaySec", "range").ToException();
        }

        if (request?.Reason is { Length: > 200 })
        {
            throw IpcError.Validation("reason", "max").ToException();
        }

        if (_sessions.State.IsOpen())
        {
            throw IpcError.Conflict("A session is active; end it first", "sessionActive").ToException();
        }

        _logger.LogWarning("{Action} requested by the Shell (delay {Delay}s): {Reason}", reboot ? "Reboot" : "Shutdown", request?.DelaySec, request?.Reason ?? "-");
        return reboot
            ? _power.RebootAsync(request?.DelaySec, request?.Reason, force: false, cancellationToken)
            : _power.ShutdownAsync(request?.DelaySec, request?.Reason, force: false, cancellationToken);
    }

    private async Task<OkResponse> LockScreenAsync(IpcContext context, SysLockScreenRequest? request, CancellationToken cancellationToken)
    {
        await _power.LockAsync(request?.Reason, cancellationToken).ConfigureAwait(false);
        return OkResponse.Instance;
    }

    private async Task<SysSetLocaleResponse> SetLocaleAsync(IpcContext context, SysSetLocaleRequest request, CancellationToken cancellationToken)
    {
        Locale locale = _shellSettings.SetLocale(request.Locale);
        _server.AcceptLanguage = ShellSettingsStore.WireName(locale);
        if (context.User is { } user && _connection.Connectivity == ConnectivityState.Online && user.Locale != locale && user.Role != UserRole.Guest)
        {
            try
            {
                User updated = await _server.UpdateUserAsync(user.Id, new ProfileUpdateRequest(Locale: locale), cancellationToken).ConfigureAwait(false);
                _users.Update(updated);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Locale {Locale} could not be saved to the user profile", locale);
            }
        }

        _logger.LogInformation("Locale set to {Locale}", locale);
        return new SysSetLocaleResponse(locale);
    }

    private SysUnlockAdminResponse UnlockAdmin(SysUnlockAdminRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Pin) || request.Pin.Length > 32)
        {
            throw IpcError.Validation("pin", "format").ToException();
        }

        DateTimeOffset now = _clock.UtcNow;
        lock (_gate)
        {
            while (_unlockFailures.Count > 0 && now - _unlockFailures.Peek() > TimeSpan.FromMinutes(1))
            {
                _unlockFailures.Dequeue();
            }

            if (_unlockFailures.Count >= MaxUnlockAdminAttemptsPerMinute)
            {
                int retryAfter = (int)Math.Ceiling((TimeSpan.FromMinutes(1) - (now - _unlockFailures.Peek())).TotalSeconds);
                throw IpcError.RateLimited(Math.Max(1, retryAfter)).ToException();
            }
        }

        string? hash = _shellSettings.AdminPinHash;
        bool ok = hash is not null && VerifyPin(hash, request.Pin);
        if (!ok)
        {
            lock (_gate)
            {
                _unlockFailures.Enqueue(now);
            }

            _logger.LogWarning("sys.unlockAdmin refused ({Reason})", hash is null ? "no admin PIN configured" : "wrong PIN");
            throw IpcError.Unauthorized("Wrong admin PIN", hash is null ? "notConfigured" : "badPin").ToException();
        }

        PruneAdminTokens(now);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        DateTimeOffset expiresAt = now + AdminTokenLifetime;
        _adminTokens[token] = expiresAt;
        _logger.LogWarning("Admin unlock granted until {ExpiresAt}", expiresAt);
        return new SysUnlockAdminResponse(true, token, expiresAt);
    }

    private OkResponse LogClientError(SysLogClientErrorRequest request)
    {
        DateTimeOffset now = _clock.UtcNow;
        lock (_gate)
        {
            if (now - _clientErrorWindow >= TimeSpan.FromSeconds(1))
            {
                _clientErrorWindow = now;
                _clientErrorsInWindow = 0;
            }

            if (++_clientErrorsInWindow > MaxClientErrorsPerSecond)
            {
                return OkResponse.Instance;
            }
        }

        string message = request.Message is { Length: > 2000 } m ? m[..2000] : request.Message ?? string.Empty;
        string? stack = request.Stack is { Length: > 8000 } s ? s[..8000] : request.Stack;
        if (request.Level == ClientErrorLevel.Error)
        {
            _logger.LogError("Shell error at {Route}: {Message}\n{Stack}", request.Route ?? "-", message, stack ?? string.Empty);
        }
        else
        {
            _logger.LogWarning("Shell warning at {Route}: {Message}\n{Stack}", request.Route ?? "-", message, stack ?? string.Empty);
        }

        _ = _telemetry.Publish(ClientErrorTelemetryKind, JsonDefaults.ToElement(new SysLogClientErrorRequest(request.Level, message, stack, request.Route)));
        return OkResponse.Instance;
    }

    private OkResponse AckAdminMessage(SysAckAdminMessageRequest request)
    {
        if (request.Id == Guid.Empty)
        {
            throw IpcError.Validation("id", "required").ToException();
        }

        if (!_adminMessages.Ack(request.Id))
        {
            _logger.LogDebug("sys.ackAdminMessage for unknown or already resolved message {Id}", request.Id);
        }

        return OkResponse.Instance;
    }

    // ---- update -----------------------------------------------------------------------------

    private Task<UpdateCheckResponse> UpdateCheckAsync(IpcContext context, CancellationToken cancellationToken)
    {
        if (_connection.Connectivity != ConnectivityState.Online)
        {
            throw IpcError.AgentOffline().ToException();
        }

        return _updater.CheckNowAsync(cancellationToken);
    }

    private async Task<UpdateApplyResponse> UpdateApplyAsync(IpcContext context, UpdateApplyRequest request, CancellationToken cancellationToken)
    {
        UpdateApplyResponse response = await _updater.ApplyAsync(request.Component, cancellationToken).ConfigureAwait(false);
        if (!response.Scheduled)
        {
            throw IpcError.NotFound($"Staged {request.Component} update").ToException();
        }

        return response;
    }

    // ---- helpers ----------------------------------------------------------------------------

    private Guid PcId => _settings.CurrentValue.PcId ?? _connection.PcId ?? Guid.Empty;

    private string ShellVersion => string.IsNullOrWhiteSpace(_server.ShellVersion) ? "0.0.0" : _server.ShellVersion;

    private async Task<Pc> GetPcAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        Guid pcId = PcId;
        if (_cachedPc is { } cached && cached.Id == pcId && now - _cachedPcAt < PcCacheTtl)
        {
            return cached;
        }

        if (pcId != Guid.Empty && _connection.Connectivity == ConnectivityState.Online)
        {
            try
            {
                Pc pc = await _server.GetPcAsync(pcId, cancellationToken).ConfigureAwait(false);
                _cachedPc = pc;
                _cachedPcAt = now;
                return pc;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is ServerApiException)
            {
                _logger.LogDebug(ex, "GET /pcs/{PcId} failed; synthesizing PC info", pcId);
            }
        }

        AgentSettings settings = _settings.CurrentValue;
        PlaySession? session = _sessions.Current;
        bool open = session is not null && _sessions.State.IsOpen();
        PcStatus status = !open ? PcStatus.Free : _sessions.State == SessionState.Locked ? PcStatus.Locked : PcStatus.Busy;
        string ip;
        try
        {
            ip = _network.GetInfo().Ip;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Network info unavailable");
            ip = string.Empty;
        }

        return new Pc(
            pcId,
            settings.PcName ?? Environment.MachineName,
            settings.Zone ?? string.Empty,
            0,
            null,
            ip,
            status,
            open ? session!.Id : null,
            ClubShellVersion.Current,
            ShellVersion,
            now);
    }

    private void PruneAdminTokens(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, DateTimeOffset> entry in _adminTokens)
        {
            if (entry.Value <= now)
            {
                _adminTokens.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>Accepts <c>pbkdf2$…</c> (see <see cref="OfflineSessionStore.HashPassword"/>) and SHA-256 hex hashes.</summary>
    private static bool VerifyPin(string hash, string pin)
    {
        if (hash.StartsWith("pbkdf2$", StringComparison.Ordinal))
        {
            return OfflineSessionStore.VerifyPassword(hash, pin);
        }

        if (hash.Length == 64 && hash.All(char.IsAsciiHexDigit))
        {
            byte[] expected = Convert.FromHexString(hash);
            byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(pin));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        return false;
    }
}

/// <summary>
/// Sets the master volume of the default render endpoint through Core Audio (<c>IMMDeviceEnumerator</c> →
/// <c>IAudioEndpointVolume</c>). Running in session 0 the service usually has no console audio endpoint, so the call
/// is best effort: <see langword="false"/> means "not applied here" and the persisted <c>shell.json</c> value is
/// applied by the Shell in the user session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CoreAudioVolume
{
    private const uint ClsctxAll = 0x17;
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid AudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>Applies <paramref name="level"/> (0–100) and <paramref name="muted"/>; <see langword="false"/> when no endpoint is reachable.</summary>
    public static bool TryApply(int level, bool muted, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        object? enumeratorObject = null;
        object? deviceObject = null;
        object? volumeObject = null;
        try
        {
            Type? type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid);
            if (type is null)
            {
                return false;
            }

            enumeratorObject = Activator.CreateInstance(type);
            if (enumeratorObject is not IMMDeviceEnumerator enumerator)
            {
                return false;
            }

            enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out IMMDevice device);
            deviceObject = device;
            Guid iid = AudioEndpointVolumeIid;
            device.Activate(ref iid, ClsctxAll, 0, out volumeObject);
            if (volumeObject is not IAudioEndpointVolume volume)
            {
                return false;
            }

            volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 100) / 100f, 0);
            volume.SetMute(muted, 0);
            return true;
        }
        catch (COMException ex)
        {
            logger.LogDebug(ex, "Core Audio call failed (HRESULT 0x{HResult:X8})", ex.HResult);
            return false;
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or MissingMethodException or TypeLoadException)
        {
            logger.LogDebug(ex, "Core Audio unavailable");
            return false;
        }
        finally
        {
            Release(volumeObject);
            Release(deviceObject);
            Release(enumeratorObject);
        }
    }

    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            _ = Marshal.ReleaseComObject(com);
        }
    }

#pragma warning disable SYSLIB1096 // classic COM interop keeps the Agent free of AllowUnsafeBlocks; a handful of calls per volume change.
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        void RegisterEndpointNotificationCallback(nint client);

        void UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        void OpenPropertyStore(uint access, out nint properties);

        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        void GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(nint notify);

        void UnregisterControlChangeNotify(nint notify);

        void GetChannelCount(out uint count);

        void SetMasterVolumeLevel(float levelDb, nint eventContext);

        void SetMasterVolumeLevelScalar(float level, nint eventContext);

        void GetMasterVolumeLevel(out float levelDb);

        void GetMasterVolumeLevelScalar(out float level);

        void SetChannelVolumeLevel(uint channel, float levelDb, nint eventContext);

        void SetChannelVolumeLevelScalar(uint channel, float level, nint eventContext);

        void GetChannelVolumeLevel(uint channel, out float levelDb);

        void GetChannelVolumeLevelScalar(uint channel, out float level);

        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint eventContext);

        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        void GetVolumeStepInfo(out uint step, out uint stepCount);

        void VolumeStepUp(nint eventContext);

        void VolumeStepDown(nint eventContext);

        void QueryHardwareSupport(out uint mask);

        void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }
#pragma warning restore SYSLIB1096
}
