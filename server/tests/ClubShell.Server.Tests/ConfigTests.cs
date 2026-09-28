using System.Net.Http.Json;
using System.Text.Json;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ClubShell.Server.Tests;

/// <summary>
/// <c>GET /agents/{pcId}/config</c> and <c>/policies</c> (DESIGN §5.9, §7.3): features that answer 501 are sent as an
/// explicit <c>false</c>, versions equal the heartbeat's, strong ETags <c>"c&lt;v&gt;"</c>/<c>"p&lt;v&gt;"</c> with 304, and
/// the policy seed bumps <c>policyVersion</c> only when it changes.
/// </summary>
public sealed class ConfigTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Config_sends_every_unimplemented_feature_as_false_and_the_heartbeat_version()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var response = await agent.SendAsync(HttpMethod.Get, agent.Path("config"));
        var config = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("\"c1\"", response.Headers.ETag!.ToString());

        var features = config.GetProperty("shell").GetProperty("features");
        foreach (var feature in new[] { "shop", "chat", "booking", "tournaments", "topup", "apps", "callAdmin" })
        {
            Assert.False(features.GetProperty(feature).GetBoolean(), feature);
        }

        Assert.True(features.GetProperty("profile").GetBoolean());
        Assert.False(config.GetProperty("games").GetProperty("accountPool").GetProperty("enabled").GetBoolean());
        Assert.False(config.GetProperty("games").GetProperty("cloudSave").GetProperty("enabled").GetBoolean());
        Assert.False(config.GetProperty("updates").GetProperty("enabled").GetBoolean());
        Assert.True(config.GetProperty("anticheat").GetProperty("reportViolations").GetBoolean());
        Assert.Equal(60, config.GetProperty("session").GetProperty("graceSec").GetInt32());
        Assert.Equal(240, config.GetProperty("offline").GetProperty("maxOfflineMinutes").GetInt32());
        Assert.Equal("ClubShell", config.GetProperty("shell").GetProperty("club").GetProperty("name").GetString());
        Assert.Equal(agent.Registration.GetProperty("pc").GetProperty("number").GetInt32(), config.GetProperty("number").GetInt32());
        Assert.Equal(config.GetRawText(), agent.Registration.GetProperty("config").GetRawText());

        using var heartbeat = await agent.SendAsync(HttpMethod.Post, agent.Path("heartbeat"), TestAgent.Heartbeat());
        Assert.Equal(config.GetProperty("version").GetInt32(), (await heartbeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("configVersion").GetInt32());

        using var cached = agent.Request(HttpMethod.Get, agent.Path("config"));
        cached.Headers.TryAddWithoutValidation("If-None-Match", "\"c1\"");
        using var notModified = await server.Http.SendAsync(cached);
        Assert.Equal(304, (int)notModified.StatusCode);
    }

    [Fact]
    public async Task Policies_carry_the_seed_with_an_etag_and_a_reseed_bumps_the_version()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var response = await agent.SendAsync(HttpMethod.Get, agent.Path("policies"));
        var policy = await response.Content.ReadFromJsonAsync<JsonElement>();
        var version = policy.GetProperty("version").GetInt32();
        Assert.Equal($"\"p{version}\"", response.Headers.ETag!.ToString());
        Assert.Equal("deny", policy.GetProperty("processAllowlist").GetProperty("mode").GetString());

        using (var heartbeat = await agent.SendAsync(HttpMethod.Post, agent.Path("heartbeat"), TestAgent.Heartbeat()))
        {
            Assert.Equal(version, (await heartbeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("policyVersion").GetInt32());
        }

        using (var request = agent.Request(HttpMethod.Get, agent.Path("policies")))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", $"\"p{version}\"");
            using var notModified = await server.Http.SendAsync(request);
            Assert.Equal(304, (int)notModified.StatusCode);
            Assert.Equal($"\"p{version}\"", notModified.Headers.ETag!.ToString());
        }

        // The same seed on the next start changes nothing; an edited seed is a new version.
        var clubs = server.Services.GetRequiredService<ClubRepository>();
        var seed = JsonDefaults.Deserialize<Policy>(policy.GetRawText())!;
        Assert.Equal(0, await clubs.SeedPolicyAsync(seed with { Version = 99 }));
        Assert.Equal(1, await clubs.SeedPolicyAsync(seed with { Kiosk = seed.Kiosk with { IdleTimeoutSec = 600 } }));

        using var reseeded = await agent.SendAsync(HttpMethod.Get, agent.Path("policies"));
        var next = await reseeded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(version + 1, next.GetProperty("version").GetInt32());
        Assert.Equal(600, next.GetProperty("kiosk").GetProperty("idleTimeoutSec").GetInt32());
        Assert.Equal($"\"p{version + 1}\"", reseeded.Headers.ETag!.ToString());
    }
}
