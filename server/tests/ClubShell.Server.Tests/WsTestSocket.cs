using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ClubShell.Server.Tests;

/// <summary>
/// A raw <see cref="ClientWebSocket"/> on <see cref="KestrelServerFixture.WsUri"/> for frame-level tests (the real agent
/// client is <see cref="AgentHarness"/>). Every frame the server sends is checked against its AsyncAPI message.
/// </summary>
public sealed class WsTestSocket : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private WsTestSocket(ClientWebSocket socket) => Socket = socket;

    public ClientWebSocket Socket { get; }

    /// <summary>Connects with <c>Authorization: Bearer</c> and <c>clubshell.v1</c> unless told otherwise.</summary>
    public static async Task<WsTestSocket> ConnectAsync(KestrelServerFixture server, string? bearer, string? queryToken = null, string? subprotocol = "clubshell.v1")
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (subprotocol is not null)
        {
            socket.Options.AddSubProtocol(subprotocol);
        }

        if (bearer is not null)
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
        }

        var uri = queryToken is null ? server.WsUri : new Uri(server.WsUri + "?token=" + Uri.EscapeDataString(queryToken));
        using var timeout = new CancellationTokenSource(Wait);
        await socket.ConnectAsync(uri, timeout.Token);
        return new WsTestSocket(socket);
    }

    public Task SendAsync(object frame) =>
        Socket.SendAsync(Encoding.UTF8.GetBytes(frame as string ?? JsonSerializer.Serialize(frame)), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next frame that is not a server ping; pings are answered with a pong unless <paramref name="answerPings"/> is off.</summary>
    public async Task<JsonElement> ReceiveAsync(bool answerPings = true)
    {
        while (true)
        {
            var frame = await ReceiveAnyAsync() ?? throw new InvalidOperationException($"socket closed: {Socket.CloseStatus} {Socket.CloseStatusDescription}");
            if (frame.GetProperty("type").GetString() != "ping")
            {
                return frame;
            }

            Contract.AssertMessage("ping", frame);
            if (answerPings)
            {
                await SendAsync(new { type = "pong", id = frame.GetProperty("id").GetGuid(), ts = DateTimeOffset.UtcNow, payload = (object?)null });
            }
        }
    }

    /// <summary>The next frame, or null once the server closed (then <see cref="WebSocket.CloseStatus"/> is set).</summary>
    public async Task<JsonElement?> ReceiveAnyAsync()
    {
        using var timeout = new CancellationTokenSource(Wait);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await Socket.ReceiveAsync(chunk, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Write(chunk, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonElement.Parse(buffer.ToArray());
    }

    /// <summary>Reads (answering pings) until the server closes; returns the close status.</summary>
    public async Task<WebSocketCloseStatus?> ClosedAsync(bool answerPings = true)
    {
        while (true)
        {
            var frame = await ReceiveAnyAsync();
            if (frame is null)
            {
                return Socket.CloseStatus;
            }

            if (answerPings && frame.Value.GetProperty("type").GetString() == "ping")
            {
                await SendAsync(new { type = "pong", id = frame.Value.GetProperty("id").GetGuid(), ts = DateTimeOffset.UtcNow, payload = (object?)null });
            }
        }
    }

    public void Dispose() => Socket.Dispose();
}

/// <summary>Polling for state another thread changes (the hub registry, a socket's state).</summary>
public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }
}
