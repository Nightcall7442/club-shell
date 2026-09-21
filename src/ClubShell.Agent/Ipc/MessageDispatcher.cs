using System.ComponentModel;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Ipc;

/// <summary>
/// Per-request view of the connection a handler serves. <see cref="Auth"/> is the effective level computed by the
/// server (handshake done, a user logged in, a session open); <see cref="User"/> and <see cref="Session"/> are the
/// Agent-wide snapshots at dispatch time.
/// </summary>
/// <param name="ConnectionId">Connection the request arrived on.</param>
/// <param name="Auth">Effective authentication level of the connection.</param>
/// <param name="User">Logged-in user (Agent-wide, see <see cref="ShellUserContext"/>).</param>
/// <param name="Session">Open session, when any.</param>
/// <param name="ClientPid">Client process id.</param>
/// <param name="WtsSessionId">Client WTS session id.</param>
/// <param name="Elevate">
/// Marks the connection as authenticated: <see cref="IpcAuthLevel.Hello"/> after a successful <c>auth.hello</c>. The user
/// and session arguments are informational (the user lives in <see cref="ShellUserContext"/>, the session in the
/// session service); a level below <see cref="IpcAuthLevel.Hello"/> revokes the handshake.
/// </param>
public sealed record IpcContext(
    Guid ConnectionId,
    IpcAuthLevel Auth,
    User? User,
    PlaySession? Session,
    int ClientPid,
    int WtsSessionId,
    Action<IpcAuthLevel, User?, PlaySession?> Elevate)
{
    /// <summary>The logged-in user or <c>unauthorized</c>.</summary>
    public User RequireUser() => User ?? throw IpcError.Unauthorized("Login required", "loginRequired").ToException();

    /// <summary>The open session or <c>sessionNotActive</c>.</summary>
    public PlaySession RequireSession() => Session ?? throw IpcError.SessionNotActive().ToException();
}

/// <summary>A group of IPC handlers registered into the <see cref="MessageDispatcher"/> at startup.</summary>
public interface IIpcHandlerGroup
{
    /// <summary>Registers every handler of the group.</summary>
    void Register(MessageDispatcher dispatcher);
}

/// <summary>
/// The Agent-wide logged-in user (IPC_PROTOCOL.md §7.1). One kiosk = one player; the state survives Shell reconnects
/// so <c>auth.status</c> after a Shell crash still reports the user.
/// </summary>
public sealed class ShellUserContext
{
    private readonly object _gate = new();
    private User? _user;
    private DateTimeOffset? _expiresAt;
    private ConnectivityState _mode;

    /// <summary>Raised after every change (login, refresh, logout).</summary>
    public event EventHandler? Changed;

    /// <summary>Logged-in user, or <see langword="null"/>.</summary>
    public User? User
    {
        get
        {
            lock (_gate)
            {
                return _user;
            }
        }
    }

    /// <summary>Access-token expiry of the user (Agent-held).</summary>
    public DateTimeOffset? ExpiresAt
    {
        get
        {
            lock (_gate)
            {
                return _expiresAt;
            }
        }
    }

    /// <summary>Whether the login was validated online or from the offline cache.</summary>
    public ConnectivityState Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
    }

    /// <summary><see langword="true"/> while a user is logged in.</summary>
    public bool IsAuthenticated => User is not null;

    /// <summary>Sets the logged-in user.</summary>
    public void Set(User user, DateTimeOffset expiresAt, ConnectivityState mode)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            _user = user;
            _expiresAt = expiresAt;
            _mode = mode;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the user snapshot (profile refresh) when it is the same account; <see langword="false"/> otherwise.</summary>
    public bool Update(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            if (_user is null || _user.Id != user.Id)
            {
                return false;
            }

            _user = user;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Logs the user out; <see langword="true"/> when someone was logged in.</summary>
    public bool Clear()
    {
        lock (_gate)
        {
            if (_user is null)
            {
                return false;
            }

            _user = null;
            _expiresAt = null;
            _mode = ConnectivityState.Offline;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

/// <summary>
/// Maps IPC request names to typed handlers and turns the outcome into a response envelope (IPC_PROTOCOL.md §2 rules 3–6):
/// unknown name → <c>notFound</c>, insufficient auth → <c>unauthorized</c> / <c>sessionNotActive</c>, bad payload →
/// <c>validation</c>, <see cref="IpcException"/> → its error, <see cref="ServerApiException"/> → its IPC mapping,
/// cancellation → <c>timeout</c>, anything else → <c>internal</c> with a trace id.
/// </summary>
public sealed class MessageDispatcher
{
    private readonly Dictionary<string, HandlerDescriptor> _handlers = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private readonly ILogger<MessageDispatcher> _logger;

    /// <summary>Creates the dispatcher and lets every group register its handlers.</summary>
    public MessageDispatcher(IEnumerable<IIpcHandlerGroup> groups, IClock clock, ILogger<MessageDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _logger = logger;
        foreach (IIpcHandlerGroup group in groups)
        {
            group.Register(this);
        }

        var missing = UnhandledCommands;
        if (missing.Count > 0)
        {
            _logger.LogWarning("IPC requests without a handler: {Names}", string.Join(", ", missing));
        }
    }

    /// <summary>Registered request names.</summary>
    public IReadOnlyCollection<string> Names => _handlers.Keys;

    /// <summary>Request names of <see cref="AgentCommands.All"/> that have no handler (should be empty).</summary>
    public IReadOnlyList<string> UnhandledCommands => AgentCommands.Names.Where(n => !_handlers.ContainsKey(n)).ToList();

    /// <summary><see langword="true"/> when <paramref name="name"/> has a handler.</summary>
    public bool IsRegistered(string name) => name is not null && _handlers.ContainsKey(name);

    /// <summary>Auth level required by <paramref name="name"/>, or <see langword="null"/> when unknown.</summary>
    public IpcAuthLevel? RequiredAuth(string name) => name is not null && _handlers.TryGetValue(name, out var d) ? d.Auth : null;

    /// <summary>Registers a handler whose payload is required (<c>validation</c> when absent).</summary>
    /// <param name="name">Request name.</param>
    /// <param name="handler">Handler; return <see langword="null"/> for a <c>payload: null</c> response.</param>
    /// <param name="auth">Override of the level from <see cref="AgentCommands.RequiredAuth"/> (unknown names default to <see cref="IpcAuthLevel.Hello"/>).</param>
    public void Register<TRequest, TResponse>(string name, Func<IpcContext, TRequest, CancellationToken, Task<TResponse>> handler, IpcAuthLevel? auth = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Add(name, auth, async (context, payload, cancellationToken) =>
        {
            TRequest? request = ParsePayload<TRequest>(payload);
            if (request is null)
            {
                throw IpcError.Validation("payload", "required").ToException();
            }

            return Serialize(await handler(context, request, cancellationToken).ConfigureAwait(false));
        });
    }

    /// <summary>Registers a handler whose payload may be omitted (the handler receives <see langword="null"/>).</summary>
    public void RegisterOptional<TRequest, TResponse>(string name, Func<IpcContext, TRequest?, CancellationToken, Task<TResponse>> handler, IpcAuthLevel? auth = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        Add(name, auth, async (context, payload, cancellationToken) =>
            Serialize(await handler(context, ParsePayload<TRequest>(payload), cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Registers a handler that ignores the payload.</summary>
    public void RegisterNoPayload<TResponse>(string name, Func<IpcContext, CancellationToken, Task<TResponse>> handler, IpcAuthLevel? auth = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Add(name, auth, async (context, _, cancellationToken) => Serialize(await handler(context, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Dispatches one request and always returns exactly one response envelope (never throws).</summary>
    public async Task<IpcEnvelope> DispatchAsync(IpcContext context, IpcEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind != IpcKind.Request)
        {
            return IpcEnvelope.Fail(request, IpcError.ProtocolError("Only requests are dispatched"), _clock.UtcNow);
        }

        if (!_handlers.TryGetValue(request.Name, out HandlerDescriptor? descriptor))
        {
            return IpcEnvelope.Fail(request, IpcError.UnknownMessage(request.Name), _clock.UtcNow);
        }

        if (descriptor.Auth > context.Auth)
        {
            IpcError denied = descriptor.Auth == IpcAuthLevel.Session && context.Auth == IpcAuthLevel.User
                ? IpcError.SessionNotActive()
                : IpcError.Unauthorized(context.Auth == IpcAuthLevel.None ? "auth.hello required" : "Login required", context.Auth == IpcAuthLevel.None ? "helloRequired" : "loginRequired");
            return IpcEnvelope.Fail(request, denied, _clock.UtcNow);
        }

        long started = _clock.GetTimestamp();
        try
        {
            JsonElement? payload = await descriptor.Handler(context, request.Payload, cancellationToken).ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Debug) && !string.Equals(request.Name, IpcMessages.Sys.Ping, StringComparison.Ordinal))
            {
                _logger.LogDebug("ipc {Name} ok in {Elapsed:F1} ms", request.Name, _clock.GetElapsedTime(started).TotalMilliseconds);
            }

            return IpcEnvelope.ReplyTo(request, payload, _clock.UtcNow);
        }
        catch (Exception ex)
        {
            IpcError error = MapError(ex, request.Name, cancellationToken);
            return IpcEnvelope.Fail(request, error, _clock.UtcNow);
        }
    }

    private static JsonElement? Serialize<TResponse>(TResponse value) =>
        value is null ? null : JsonDefaults.ToElement(value);

    private static TRequest? ParsePayload<TRequest>(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element)
        {
            return default;
        }

        try
        {
            return JsonDefaults.FromElement<TRequest>(element);
        }
        catch (JsonException ex)
        {
            throw IpcError.Validation(ex.Path is { Length: > 1 } p ? p.TrimStart('$', '.') : "payload", "format", ex.Message).ToException();
        }
        catch (NotSupportedException ex)
        {
            throw IpcError.Validation("payload", "format", ex.Message).ToException();
        }
    }

    private IpcError MapError(Exception exception, string name, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case IpcException ipc:
                _logger.LogInformation("ipc {Name} failed: {Code} {Message}", name, ipc.Code, ipc.Message);
                return ipc.Error;
            case ServerApiException server:
                _logger.LogWarning("ipc {Name} failed upstream: {Code} ({Status}) {Message}", name, server.Code, server.Status, server.Message);
                return server.ToIpcError();
            case HttpRequestException http:
                _logger.LogWarning(http, "ipc {Name}: server unreachable", name);
                return IpcError.AgentOffline();
            case OperationCanceledException:
                _logger.LogWarning("ipc {Name} timed out or was cancelled (token cancelled: {Cancelled})", name, cancellationToken.IsCancellationRequested);
                return IpcError.Timeout($"'{name}' did not complete in time");
            case TimeoutException timeout:
                _logger.LogWarning(timeout, "ipc {Name} timed out", name);
                return IpcError.Timeout(timeout.Message);
            case JsonException json:
                _logger.LogWarning(json, "ipc {Name}: payload could not be decoded", name);
                return IpcError.Validation("payload", "format", json.Message);
            case Win32Exception win32:
            {
                string traceId = NewTraceId();
                _logger.LogError(win32, "ipc {Name} failed with Win32 error {Code} (trace {TraceId})", name, win32.NativeErrorCode, traceId);
                return IpcError.Internal(traceId);
            }

            default:
            {
                string traceId = NewTraceId();
                _logger.LogError(exception, "ipc {Name} failed unexpectedly (trace {TraceId})", name, traceId);
                return IpcError.Internal(traceId);
            }
        }
    }

    private static string NewTraceId() => Guid.NewGuid().ToString("N");

    private void Add(string name, IpcAuthLevel? auth, Func<IpcContext, JsonElement?, CancellationToken, Task<JsonElement?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!IpcEnvelope.IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid IPC message name", nameof(name));
        }

        IpcAuthLevel level = auth ?? (AgentCommands.TryParse(name, out AgentCommand command) ? command.RequiredAuth() : IpcAuthLevel.Hello);
        if (!_handlers.TryAdd(name, new HandlerDescriptor(level, handler)))
        {
            throw new InvalidOperationException($"IPC handler for '{name}' is already registered");
        }
    }

    private sealed record HandlerDescriptor(IpcAuthLevel Auth, Func<IpcContext, JsonElement?, CancellationToken, Task<JsonElement?>> Handler);
}
