using System.Net.Http.Headers;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>
/// Authentication runs in the operation's mode before the 501 (DESIGN §1, §3.1); every 401 carries <c>details.reason</c>.
/// User and staff modes are S0 stubs that only require the credential to be present.
/// </summary>
public sealed class AuthModeTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private const string Tariffs = "/api/v1/tariffs";

    [Fact]
    public async Task Club_mode_requires_a_current_or_previous_enrollment_key()
    {
        await Register(new (string?, int)[] { (null, 401), ("wrong", 401), (ServerFixture.ClubKey, 501) });

        // Rotation: config change plus restart rewrites the hashes; the previous key keeps working.
        var clubs = server.Services.GetRequiredService<ClubRepository>();
        await clubs.EnsureAsync(new ClubOptions { EnrollmentKey = "rotated", PreviousEnrollmentKey = ServerFixture.ClubKey });
        try
        {
            await Register(new (string?, int)[] { ("rotated", 501), (ServerFixture.ClubKey, 501), ("wrong", 401) });
        }
        finally
        {
            await clubs.EnsureAsync(new ClubOptions { EnrollmentKey = ServerFixture.ClubKey });
        }

        await SetDisabledAsync(true);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/register") { Content = new StringContent("{}") };
            request.Headers.Add(AgentAuthMiddleware.ClubKeyHeader, ServerFixture.ClubKey);
            using var response = await server.Http.SendAsync(request);
            await Contract.ReadErrorAsync(response, 403, "forbidden", "clubDisabled");
        }
        finally
        {
            await SetDisabledAsync(false);
        }
    }

    private async Task SetDisabledAsync(bool disabled)
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE clubs SET disabled = @disabled", new { disabled });
    }

    private async Task Register((string? Key, int Status)[] cases)
    {
        foreach (var (key, status) in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/register") { Content = new StringContent("{}") };
            if (key is not null)
            {
                request.Headers.Add(AgentAuthMiddleware.ClubKeyHeader, key);
            }

            using var response = await server.Http.SendAsync(request);
            if (status == 401)
            {
                await Contract.ReadErrorAsync(response, 401, "unauthorized", "clubKey");
            }
            else
            {
                Assert.Equal(status, (int)response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task None_mode_answers_501_without_credentials()
    {
        using var refresh = await server.Http.PostAsync("/api/v1/agents/refresh", new StringContent("{}"));
        await Contract.ReadErrorAsync(refresh, 501, "notImplemented", "notImplemented");
    }

    [Fact]
    public async Task Agent_mode_401_reasons()
    {
        var agent = await TestAgent.CreateAsync(server);

        using (var request = agent.Request(HttpMethod.Get, Tariffs))
        {
            request.Headers.Authorization = null;
            using var response = await server.Http.SendAsync(request);
            await Contract.ReadErrorAsync(response, 401, "unauthorized", "missing");
        }

        using (var request = agent.Request(HttpMethod.Get, Tariffs))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent.AccessToken[..^4] + "AAAA");
            using var response = await server.Http.SendAsync(request);
            await Contract.ReadErrorAsync(response, 401, "unauthorized", "invalid");
        }

        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE pcs SET credentials_version = credentials_version + 1 WHERE id = @id", new { id = agent.PcId });
        }

        using (var response = await agent.SendAsync(HttpMethod.Get, Tariffs))
        {
            await Contract.ReadErrorAsync(response, 401, "unauthorized", "revoked");
        }
    }

    [Fact]
    public async Task Deleted_pc_is_revoked()
    {
        var agent = await TestAgent.CreateAsync(server);
        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE pcs SET deleted_at = now() WHERE id = @id", new { id = agent.PcId });
        }

        using var response = await agent.SendAsync(HttpMethod.Get, Tariffs);
        await Contract.ReadErrorAsync(response, 401, "unauthorized", "revoked");
    }

    [Fact]
    public async Task Expired_access_token_is_401_expired()
    {
        var agent = await TestAgent.CreateAsync(server);
        server.Clock.Advance(TimeSpan.FromMinutes(61));
        using var response = await agent.SendAsync(HttpMethod.Get, Tariffs);
        await Contract.ReadErrorAsync(response, 401, "unauthorized", "expired");
    }

    [Fact]
    public async Task User_mode_requires_user_token_optional_user_does_not()
    {
        var agent = await TestAgent.CreateAsync(server);
        var user = $"/api/v1/users/{Guid.NewGuid()}";

        using (var response = await agent.SendAsync(HttpMethod.Get, user))
        {
            var body = await Contract.ReadErrorAsync(response, 401, "unauthorized", "userToken");
            Assert.Equal("invalid", body.GetProperty("error").GetProperty("details").GetProperty("problem").GetString());
        }

        using (var request = agent.Request(HttpMethod.Get, user))
        {
            request.Headers.Add(AgentAuthMiddleware.UserTokenHeader, "user-token");
            using var response = await server.Http.SendAsync(request);
            Assert.Equal(501, (int)response.StatusCode);
        }

        // GET /games: the user token only enriches the answer, its absence is never 401.
        using (var response = await agent.SendAsync(HttpMethod.Get, "/api/v1/games"))
        {
            Assert.Equal(501, (int)response.StatusCode);
        }
    }

    [Fact]
    public async Task Operation_without_a_401_response_is_not_authenticated()
    {
        // adminLogout: "always 200", the contract declares no 401 — its 501 must not become one.
        using var response = await server.Http.PostAsync("/api/v1/admin/logout", new StringContent("{}"));
        await Contract.ReadErrorAsync(response, 501, "notImplemented", "notImplemented");
    }

    [Theory]
    [InlineData("/api/v1/x")]
    [InlineData("/API/V1/x")]
    public async Task Api_endpoint_without_auth_requirement_fails_closed_in_any_case(string path)
    {
        var middleware = ActivatorUtilities.CreateInstance<AgentAuthMiddleware>(server.Services, (RequestDelegate)(_ => Task.CompletedTask));
        var context = new DefaultHttpContext { RequestServices = server.Services };
        context.Request.Path = path;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "forgotten"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task Staff_mode_requires_a_bearer_token()
    {
        using (var response = await server.Http.GetAsync("/api/v1/admin/me"))
        {
            await Contract.ReadErrorAsync(response, 401, "unauthorized", "missing");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "staff-token");
        using (var response = await server.Http.SendAsync(request))
        {
            Assert.Equal(501, (int)response.StatusCode);
        }
    }
}
