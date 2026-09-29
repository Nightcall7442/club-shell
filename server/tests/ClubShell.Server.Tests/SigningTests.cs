using System.Security.Cryptography;
using System.Text;
using ClubShell.Server.Auth;

namespace ClubShell.Server.Tests;

/// <summary>HMAC request signature (DESIGN §3.3): contract vectors, window, missing/mismatch, repeats are not rejected (D-4).</summary>
public sealed class SigningTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    // An agent-mode operation (S2); 200 means authentication passed.
    private const string Tariffs = "/api/v1/tariffs";

    [Theory]
    [InlineData("GET", "/api/v1/sessions/current?pcId=7d2f1c3a-1111-4222-8333-444455556666", "", "50428126405b54687a7cb8f813b7bbde31a74b8e5dddf1aa9aa596cc532abaa2")]
    [InlineData("POST", "/api/v1/auth/logout", """{"reason":"user"}""", "20046a0e46e3a7681e416ba37d5ce61baf40bfcec560685fa5042fe6f90ee7c4")]
    public void Contract_vectors_base_yaml(string method, string target, string body, string expected)
    {
        var secret = Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        Assert.Equal(expected, RequestSignature.Compute(secret, "1789992930", method, target, bodyHash));
    }

    [Fact]
    public async Task Signed_request_passes_authentication()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var response = await agent.SendAsync(HttpMethod.Get, Tariffs);
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Theory]
    [InlineData(-300, 200)]
    [InlineData(300, 200)]
    [InlineData(-301, 401)]
    [InlineData(301, 401)]
    public async Task Timestamp_window_is_300_seconds_and_skew_answers_clockSkew_with_server_time(int offsetSec, int status)
    {
        var agent = await TestAgent.CreateAsync(server);
        var now = server.Clock.GetUtcNow();
        using var response = await server.Http.SendAsync(agent.Request(HttpMethod.Get, Tariffs, timestamp: now.ToUnixTimeSeconds() + offsetSec));
        if (status == 401)
        {
            await Contract.ReadErrorAsync(response, 401, "unauthorized", "clockSkew");
            Assert.Equal(now, DateTimeOffset.Parse(response.Headers.GetValues("X-Server-Time").Single(), System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            Assert.Equal(status, (int)response.StatusCode);
        }
    }

    [Fact]
    public async Task Missing_signature_is_401_signature_missing()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var request = agent.Request(HttpMethod.Get, Tariffs);
        request.Headers.Remove(RequestSignature.SignatureHeader);
        using var response = await server.Http.SendAsync(request);
        var body = await Contract.ReadErrorAsync(response, 401, "unauthorized", "signature");
        Assert.Equal("missing", body.GetProperty("error").GetProperty("details").GetProperty("problem").GetString());
    }

    [Theory]
    [InlineData("/api/v1/tariffs?page=2", "/api/v1/tariffs", null, null)]
    [InlineData("/api/v1/agents/{pc}/heartbeat", null, """{"status":"free"}""", """{"status":"busy"}""")]
    [InlineData("/api/v1/tariffs", "/api/v1/Tariffs", null, null)]
    public async Task Signature_covers_raw_target_and_body(string target, string? signedTarget, string? body, string? signedBody)
    {
        var agent = await TestAgent.CreateAsync(server);
        target = target.Replace("{pc}", agent.PcId.ToString(), StringComparison.Ordinal);
        var method = body is null ? HttpMethod.Get : HttpMethod.Post;
        using var response = await server.Http.SendAsync(agent.Request(method, target, body, signedTarget: signedTarget, signedBody: signedBody));
        var error = await Contract.ReadErrorAsync(response, 401, "unauthorized", "signature");
        Assert.Equal("mismatch", error.GetProperty("error").GetProperty("details").GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Query_string_is_signed_exactly_as_sent()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var response = await agent.SendAsync(HttpMethod.Get, "/api/v1/tariffs?page=2&pageSize=10");
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task Repeated_signature_tuple_is_not_rejected()
    {
        var agent = await TestAgent.CreateAsync(server);
        var ts = server.Clock.GetUtcNow().ToUnixTimeSeconds();
        var heartbeat = $"/api/v1/agents/{agent.PcId}/heartbeat";
        foreach (var (method, target, body, status) in new[] { (HttpMethod.Get, Tariffs, (string?)null, 200), (HttpMethod.Post, heartbeat, TestAgent.Heartbeat(), 200) })
        {
            for (var i = 0; i < 2; i++)
            {
                using var response = await server.Http.SendAsync(agent.Request(method, target, body, timestamp: ts));
                Assert.Equal(status, (int)response.StatusCode);
            }
        }
    }

    [Fact]
    public void Replay_log_remembers_a_tuple_for_twice_the_window()
    {
        var clock = new FakeClock();
        var log = new ReplayLog(clock, new AuthOptions { SignatureWindowSec = 300 });
        var pc = Guid.NewGuid();
        Assert.False(log.Seen(pc, "1", "AB"));
        Assert.True(log.Seen(pc, "1", "ab"));
        Assert.False(log.Seen(pc, "2", "ab"));
        clock.Advance(TimeSpan.FromSeconds(601));
        Assert.False(log.Seen(pc, "1", "ab"));
    }
}
