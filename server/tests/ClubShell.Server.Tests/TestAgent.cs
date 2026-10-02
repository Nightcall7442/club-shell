using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClubShell.Server.Auth;

namespace ClubShell.Server.Tests;

/// <summary>
/// A PC registered through <c>POST /agents/register</c> that signs requests like the agent's <c>RequestSigningHandler</c>
/// (port of club-server <c>TestAgent</c>). For the real agent client see <see cref="AgentHarness"/>.
/// </summary>
public sealed class TestAgent(ServerFixture server, JsonElement registration, string hwid)
{
    public Guid PcId { get; } = registration.GetProperty("pcId").GetGuid();
    public string AccessToken { get; set; } = registration.GetProperty("accessToken").GetString()!;
    public string RefreshToken { get; set; } = registration.GetProperty("refreshToken").GetString()!;
    public byte[] Secret { get; } = Convert.FromBase64String(registration.GetProperty("signingSecret").GetString()!);
    public string Hwid { get; } = hwid;
    public JsonElement Registration { get; } = registration;

    public ServerFixture Server => server;

    /// <summary>Registers a new PC (the fixture auto-approves) with a random HWID and MAC unless given.</summary>
    public static async Task<TestAgent> CreateAsync(ServerFixture server, string? hwid = null, string? mac = null)
    {
        hwid ??= RandomHwid();
        using var response = await RegisterAsync(server, RegisterBody(hwid, mac ?? RandomMac()));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"register -> {(int)response.StatusCode} {body}");
        return new TestAgent(server, body, hwid);
    }

    public static Task<HttpResponseMessage> RegisterAsync(ServerFixture server, string body, string key = ServerFixture.ClubKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/register") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(AgentAuthMiddleware.ClubKeyHeader, key);
        return server.Http.SendAsync(request);
    }

    /// <summary>A contract-valid <c>AgentRegisterRequest</c>.</summary>
    public static string RegisterBody(string hwid, string mac, Guid? previousPcId = null, string machineName = "CLUB-PC") => JsonSerializer.Serialize(new
    {
        hwid,
        machineName,
        agentVersion = "1.4.2",
        hardware = new
        {
            cpu = new { model = "Ryzen 5 5600", cores = 6, threads = 12 },
            gpu = Array.Empty<object>(),
            ramMb = 16384,
            disks = Array.Empty<object>(),
            monitors = Array.Empty<object>(),
            network = new { mac, ip = "10.0.0.12", adapter = "Ethernet" },
            os = new { version = "10.0.22631", build = "22631" },
            peripherals = Array.Empty<object>(),
        },
        ipAddress = "10.0.0.12",
        macAddress = mac,
        previousPcId,
    });

    public static string RandomHwid() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string RandomMac() => string.Join(':', RandomNumberGenerator.GetBytes(6).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>A contract-valid <c>HeartbeatRequest</c>.</summary>
    /// <summary>A heartbeat body; <paramref name="offlineQueue"/> is the agent's outbox size (events it has not flushed yet).</summary>
    public static string Heartbeat(int offlineQueue = 0) => $$"""
        {"status":"free","agentVersion":"1.4.2","shellVersion":"1.4.2","uptimeSec":3600,"ipAddress":"10.0.0.12",
         "policyVersion":0,"runningGames":[],"offlineQueue":{{offlineQueue}},"shellConnected":true}
        """;

    /// <summary>
    /// A signed request. <paramref name="signedTarget"/>/<paramref name="signedBody"/> sign something other than what is
    /// sent (mismatch tests); <paramref name="timestamp"/> defaults to the fixture clock.
    /// </summary>
    public HttpRequestMessage Request(HttpMethod method, string target, string? body = null, long? timestamp = null, string? signedTarget = null, string? signedBody = null)
    {
        var ts = (timestamp ?? server.Clock.GetUtcNow().ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signedBody ?? body ?? "")));
        var request = new HttpRequestMessage(method, target);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        if (UserToken is not null)
        {
            request.Headers.Add(AgentAuthMiddleware.UserTokenHeader, UserToken);
        }

        request.Headers.Add(RequestSignature.TimestampHeader, ts);
        request.Headers.Add(RequestSignature.SignatureHeader, RequestSignature.Compute(Secret, ts, method.Method, signedTarget ?? target, bodyHash));
        return request;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string target, string? body = null) =>
        server.Http.SendAsync(Request(method, target, body));

    /// <summary><c>X-User-Token</c> sent with every request once set (by <see cref="LoginAsync"/>).</summary>
    public string? UserToken { get; set; }

    /// <summary>A signed POST of <paramref name="body"/> (serialized unless a string), with an <c>Idempotency-Key</c> when given.</summary>
    public Task<HttpResponseMessage> PostAsync(string target, object? body, Guid? key = null)
    {
        var request = Request(HttpMethod.Post, target, body is null ? null : body as string ?? JsonSerializer.Serialize(body));
        if (key is { } k)
        {
            request.Headers.Add("Idempotency-Key", k.ToString());
        }

        return server.Http.SendAsync(request);
    }

    /// <summary>Signs <paramref name="player"/> in on this PC with a password (200 expected); keeps the user token.</summary>
    public async Task<JsonElement> LoginAsync(TestPlayer player)
    {
        using var response = await PostAsync("/api/v1/auth/login", new { kind = "password", username = player.Username, password = Players.Password, pcId = PcId, hwid = Hwid });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"login -> {(int)response.StatusCode} {body}");
        UserToken = body.GetProperty("accessToken").GetString();
        return body;
    }

    public Task<HttpResponseMessage> HeartbeatAsync(int offlineQueue = 0) => SendAsync(HttpMethod.Post, Path("heartbeat"), Heartbeat(offlineQueue));

    /// <summary><c>/api/v1/agents/{pcId}/…</c> of this PC.</summary>
    public string Path(string rest) => $"/api/v1/agents/{PcId}/{rest}";

    public Task<HttpResponseMessage> RefreshAsync(string? token = null, string? hwid = null) =>
        server.Http.PostAsync("/api/v1/agents/refresh", new StringContent(
            JsonSerializer.Serialize(new { refreshToken = token ?? RefreshToken, hwid = hwid ?? Hwid }), Encoding.UTF8, "application/json"));
}
