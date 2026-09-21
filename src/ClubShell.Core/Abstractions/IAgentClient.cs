using ClubShell.Contracts.Ipc;

namespace ClubShell.Core.Abstractions;

/// <summary>Connection state of an <see cref="IAgentClient"/>.</summary>
public enum AgentConnectionState
{
    /// <summary>No pipe connection.</summary>
    Disconnected,

    /// <summary>Connecting / performing <c>auth.hello</c>.</summary>
    Connecting,

    /// <summary>Pipe connected and hello accepted.</summary>
    Connected,
}

/// <summary>
/// Client-side view of the Agent IPC (IPC_PROTOCOL.md): a request/response pipe with unsolicited events.
/// Implemented by the test harness and dev tools against <c>\\.\pipe\clubshell-agent</c>; the production Shell
/// implements the same protocol in Rust. Responses carrying <see cref="IpcEnvelope.Error"/> are surfaced as
/// <see cref="IpcException"/> by <see cref="RequestAsync{TRequest, TResponse}"/>.
/// </summary>
public interface IAgentClient : IAsyncDisposable
{
    /// <summary>Current connection state.</summary>
    AgentConnectionState ConnectionState { get; }

    /// <summary>Raised for every <see cref="IpcKind.Event"/> envelope received from the Agent.</summary>
    event EventHandler<IpcEnvelope>? EventReceived;

    /// <summary>Raised when <see cref="ConnectionState"/> changes.</summary>
    event EventHandler<AgentConnectionState>? ConnectionStateChanged;

    /// <summary>Connects the pipe and performs <c>auth.hello</c>.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Closes the pipe; pending requests fail with <see cref="OperationCanceledException"/>.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>Sends a raw request envelope and returns the matching response envelope (error responses are returned, not thrown).</summary>
    Task<IpcEnvelope> SendAsync(IpcEnvelope request, CancellationToken cancellationToken);

    /// <summary>Sends a typed request and deserializes the successful response payload; throws <see cref="IpcException"/> on an error response.</summary>
    Task<TResponse> RequestAsync<TRequest, TResponse>(string name, TRequest payload, CancellationToken cancellationToken);

    /// <summary>Sends a request without payload and deserializes the successful response payload; throws <see cref="IpcException"/> on an error response.</summary>
    Task<TResponse> RequestAsync<TResponse>(string name, CancellationToken cancellationToken);
}
