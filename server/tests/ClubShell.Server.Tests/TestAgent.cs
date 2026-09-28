using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Server.Auth;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>
/// A PC with agent credentials that signs requests like the agent's <c>RequestSigningHandler</c> (port of club-server
/// <c>TestAgent</c>). Until registration exists (S1) the PC row and its token are created directly.
/// </summary>
public sealed class TestAgent(ServerFixture server, Guid pcId, Guid clubId, string accessToken, byte[] secret)
{
    public Guid PcId { get; } = pcId;
    public Guid ClubId { get; } = clubId;
    public string AccessToken { get; set; } = accessToken;
    public byte[] Secret { get; } = secret;

    public static async Task<TestAgent> CreateAsync(ServerFixture server)
    {
        var db = server.Services.GetRequiredService<NpgsqlDataSource>();
        await using var c = await db.OpenConnectionAsync();
        var clubId = await c.QuerySingleAsync<Guid>("SELECT id FROM clubs");
        var pcId = Guid.CreateVersion7();
        var hwid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var secret = RandomNumberGenerator.GetBytes(32);
        await c.ExecuteAsync(
            """
            INSERT INTO pcs (id, club_id, number, name, hwid, approved, signing_secret)
            SELECT @pcId, @clubId, coalesce(max(number), 0) + 1, 'PC-TEST', @hwid, true, @secret FROM pcs WHERE club_id = @clubId
            """,
            new { pcId, clubId, hwid, secret });
        var (token, _) = server.Services.GetRequiredService<TokenService>().IssueAccessToken(pcId, clubId, hwid, 1);
        return new TestAgent(server, pcId, clubId, token, secret);
    }

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
        request.Headers.Add(RequestSignature.TimestampHeader, ts);
        request.Headers.Add(RequestSignature.SignatureHeader, RequestSignature.Compute(Secret, ts, method.Method, signedTarget ?? target, bodyHash));
        return request;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string target, string? body = null) =>
        server.Http.SendAsync(Request(method, target, body));
}
