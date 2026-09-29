using System.Net.Http.Headers;
using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Tests;

/// <summary>
/// Every contract operation the server does not implement answers 501 <c>notImplemented</c> with the envelope, never 404
/// (DESIGN §1, §11; port of club-server <c>AgentApiTests</c>). Operations come from openapi.json, not from the server's
/// own table; the skip list is <see cref="ContractStatus.Implemented"/>, the same list the server maps from.
/// </summary>
public sealed class NotImplementedTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Every_unimplemented_operation_answers_501_never_404()
    {
        var (agent, _) = await Players.SignedInAsync(server);
        Assert.Equal(102, Contract.Operations.Count);

        // Exactly the operations of the slices done so far (S1: the eight agent operations; S2: 18 player operations), each
        // required by the contract, plus getBalance, which S2 implements beyond it (DESIGN §1, §12.2 item 1).
        string[] s1 = ["register", "refresh", "heartbeat", "sendTelemetry", "getConfig", "getPolicies", "getCommands", "ackCommand"];
        string[] s2 =
        [
            "login", "startQrLogin", "getQrLoginStatus", "guestLogin", "logout", "getUser", "updateUser", "getUserStats",
            "getUserAchievements", "getUserLoyalty", "getCurrentSession", "createSession", "pauseSession", "resumeSession",
            "endSession", "extendSession", "postSessionEvents", "getTariffs",
        ];
        Assert.Equal(s1.Concat(s2).Append("getBalance").Order(), ContractStatus.Implemented.Order());
        Assert.All(s1.Concat(s2), id => Assert.Equal("required", Contract.Operations.Single(o => o.OperationId == id).Operation.GetProperty("x-server-status").GetString()));

        var pending = Contract.Operations.Where(o => !ContractStatus.Implemented.Contains(o.OperationId)).ToList();
        foreach (var op in pending)
        {
            var url = "/api/v1" + op.Path
                .Replace("{pcId}", agent.PcId.ToString(), StringComparison.Ordinal)
                .Replace("{channel}", "stable", StringComparison.Ordinal)
                .Replace("{roomId}", "club", StringComparison.Ordinal)
                .Replace("{token}", "qr-token", StringComparison.Ordinal);
            url = System.Text.RegularExpressions.Regex.Replace(url, @"\{\w+\}", _ => Guid.NewGuid().ToString());
            var method = new HttpMethod(op.Method);
            var body = method == HttpMethod.Get || method == HttpMethod.Delete ? null : "{}";

            using var request = Authenticated(op, agent, method, url, body);
            using var response = await server.Http.SendAsync(request);
            var error = await response.Content.ReadAsStringAsync();
            Assert.True(501 == (int)response.StatusCode, $"{op.OperationId} {op.Method} {url} -> {(int)response.StatusCode} {error}");
            Contract.AssertError(JsonElement.Parse(error), "notImplemented", "notImplemented");
            Assert.False(response.Headers.Contains("Retry-After"), op.OperationId);
        }

        Assert.Equal(75, pending.Count);
    }

    /// <summary>Credentials of the operation's contract <c>security</c>; agent routes also carry the signed-in player's token.</summary>
    private static HttpRequestMessage Authenticated(ContractOp op, TestAgent agent, HttpMethod method, string url, string? body)
    {
        var schemes = op.Operation.TryGetProperty("security", out var security)
            ? security.EnumerateArray().SelectMany(s => s.EnumerateObject().Select(p => p.Name)).ToHashSet()
            : [];
        HttpRequestMessage request;
        if (schemes.Contains("agentBearer"))
        {
            return agent.Request(method, url, body);
        }

        request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        if (schemes.Contains("clubKey"))
        {
            request.Headers.Add(AgentAuthMiddleware.ClubKeyHeader, ServerFixture.ClubKey);
        }
        else if (schemes.Contains("staffBearer"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "staff-token");
        }

        return request;
    }
}
