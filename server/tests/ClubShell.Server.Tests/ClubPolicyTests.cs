using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The club's own PC policy (SHELL_CHANGES 27, beyond the contract): the seed sets no DNS servers (the club's DHCP
/// decides), the owner reads and replaces the policy in the console, the startup seed then leaves it alone, and going
/// back to the seed follows it again. Every change is a new <c>policyVersion</c>. A cashier gets <c>403 ownerOnly</c>.
/// </summary>
public sealed class ClubPolicyTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task The_owner_saves_the_clubs_own_policy_and_the_seed_leaves_it_until_reset()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var agent = await TestAgent.CreateAsync(server);
        var initial = await RawExpectAsync(server, 200, HttpMethod.Get, "/policy", owner);
        Assert.False(initial.GetProperty("custom").GetBoolean());
        var version = initial.GetProperty("policy").GetProperty("version").GetInt32();
        Assert.Empty(initial.GetProperty("policy").GetProperty("webFilter").GetProperty("dnsServers").EnumerateArray());

        var edited = JsonNode.Parse(initial.GetProperty("policy").GetRawText())!.AsObject();
        edited["webFilter"]!["dnsServers"] = new JsonArray("10.10.0.1");
        edited["usb"]!["allowStorage"] = true;
        edited["version"] = 999;
        var saved = await RawExpectAsync(server, 200, HttpMethod.Put, "/policy", owner, edited);
        Assert.True(saved.GetProperty("custom").GetBoolean());
        Assert.Equal(version + 1, saved.GetProperty("policy").GetProperty("version").GetInt32());

        var served = await AgentPolicyAsync(agent);
        Assert.Equal(version + 1, served.GetProperty("version").GetInt32());
        Assert.Equal("10.10.0.1", served.GetProperty("webFilter").GetProperty("dnsServers")[0].GetString());
        Assert.True(served.GetProperty("usb").GetProperty("allowStorage").GetBoolean());
        using (var heartbeat = await agent.SendAsync(HttpMethod.Post, agent.Path("heartbeat"), TestAgent.Heartbeat()))
        {
            Assert.Equal(version + 1, (await heartbeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("policyVersion").GetInt32());
        }

        // The next start seeds every other club; this one keeps what its owner saved.
        var seed = JsonDefaults.Deserialize<Policy>(initial.GetProperty("policy").GetRawText())!;
        await server.Services.GetRequiredService<ClubRepository>().SeedPolicyAsync(seed with { Kiosk = seed.Kiosk with { IdleTimeoutSec = 601 } });
        Assert.Equal(version + 1, (await AgentPolicyAsync(agent)).GetProperty("version").GetInt32());

        var reset = await RawExpectAsync(server, 200, HttpMethod.Delete, "/policy", owner);
        Assert.False(reset.GetProperty("custom").GetBoolean());
        Assert.Equal(version + 2, reset.GetProperty("policy").GetProperty("version").GetInt32());
        var back = await AgentPolicyAsync(agent);
        Assert.Empty(back.GetProperty("webFilter").GetProperty("dnsServers").EnumerateArray());
        Assert.False(back.GetProperty("usb").GetProperty("allowStorage").GetBoolean());
    }

    [Fact]
    public async Task A_policy_that_is_not_a_contract_policy_is_refused_and_a_cashier_cannot_touch_it()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        var policy = (await RawExpectAsync(server, 200, HttpMethod.Get, "/policy", owner)).GetProperty("policy");

        var broken = JsonNode.Parse(policy.GetRawText())!.AsObject();
        broken["processAllowlist"]!["mode"] = "maybe";
        Contract.AssertError(await RawExpectAsync(server, 400, HttpMethod.Put, "/policy", owner, broken), "validation");
        Contract.AssertError(await RawExpectAsync(server, 400, HttpMethod.Put, "/policy", owner, new[] { 1 }), "validation");
        Assert.False((await RawExpectAsync(server, 200, HttpMethod.Get, "/policy", owner)).GetProperty("custom").GetBoolean());

        Contract.AssertError(await RawExpectAsync(server, 403, HttpMethod.Get, "/policy", cashier), "forbidden", "ownerOnly");
        Contract.AssertError(await RawExpectAsync(server, 403, HttpMethod.Put, "/policy", cashier, policy), "forbidden", "ownerOnly");
        Contract.AssertError(await RawExpectAsync(server, 403, HttpMethod.Delete, "/policy", cashier), "forbidden", "ownerOnly");
    }

    private static async Task<JsonElement> AgentPolicyAsync(TestAgent agent)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, agent.Path("policies"));
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
