using System.Net.Http.Headers;
using System.Text.Json;
using static ClubShell.Server.Tests.PurchaseRulesTests;

namespace ClubShell.Server.Tests;

/// <summary>
/// The player's own profile, wallet and the tariffs (DESIGN §3.1, §4.2, §7.3): only one's own <c>userId</c>
/// (<c>403 notOwner</c>), profile validation, derived stats and loyalty, the balance beyond the contract, the
/// game-settings stubs, and <c>GET /tariffs</c> by zone with an ETag but always 200.
/// </summary>
public sealed class UserTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Only_the_own_profile_and_balance_are_readable()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 4_500_000);
        var user = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 200);
        Assert.Equal((player.Username, 4_500_000L, 0), (user.GetProperty("username").GetString(), user.GetProperty("balance").GetProperty("amount").GetInt64(), user.GetProperty("loyaltyLevel").GetInt32()));

        var balance = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/wallet/{player.Id}/balance"), 200);
        Assert.Equal((4_500_000L, 0L, "UZS"), (balance.GetProperty("amount").GetProperty("amount").GetInt64(), balance.GetProperty("bonus").GetProperty("amount").GetInt64(), balance.GetProperty("currency").GetString()));

        var other = await Players.CreateAsync(Server);
        foreach (var path in new[] { $"/api/v1/users/{other.Id}", $"/api/v1/users/{other.Id}/stats", $"/api/v1/wallet/{other.Id}/balance" })
        {
            using var response = await agent.SendAsync(HttpMethod.Get, path);
            await Contract.ReadErrorAsync(response, 403, "forbidden", "notOwner");
        }
    }

    [Fact]
    public async Task Profile_update_validates_each_field()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var path = $"/api/v1/users/{player.Id}";
        var updated = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Patch, path, """{"displayName":"  Али  ","locale":"uz","avatarUrl":"https://cdn.example.uz/a.png","pin":"1234"}"""), 200);
        Assert.Equal(("Али", "uz", "https://cdn.example.uz/a.png"), (updated.GetProperty("displayName").GetString(), updated.GetProperty("locale").GetString(), updated.GetProperty("avatarUrl").GetString()));
        Assert.StartsWith("pbkdf2$", await Players.ScalarAsync<string>(Server, "SELECT unlock_pin_hash FROM users WHERE id = @Id", new { player.Id }), StringComparison.Ordinal);

        foreach (var (body, field, reason) in new[]
        {
            ("{}", "body", "required"),
            ("""{"displayName":"   "}""", "displayName", "min"),
            ($$"""{"displayName":"{{new string('x', 65)}}"}""", "displayName", "max"),
            ("""{"avatarUrl":"ftp://x/a.png"}""", "avatarUrl", "format"),
            ("""{"locale":"fr"}""", "locale", "enum"),
            ("""{"pin":"12a4"}""", "pin", "format"),
        })
        {
            using var response = await agent.SendAsync(HttpMethod.Patch, path, body);
            var error = await Contract.ReadErrorAsync(response, 400, "validation");
            Assert.Equal((field, reason), (Details(error).GetProperty("field").GetString(), Details(error).GetProperty("reason").GetString()));
        }
    }

    [Fact]
    public async Task Stats_start_at_zero_and_follow_the_sessions()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var empty = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}/stats"), 200);
        Assert.Equal((0, 0, 0L), (empty.GetProperty("sessionsCount").GetInt32(), empty.GetProperty("rank").GetInt32(), empty.GetProperty("spent").GetProperty("amount").GetInt64()));

        var (_, created) = await Players.StartAsync(agent, player);
        Server.Clock.Advance(TimeSpan.FromMinutes(30));
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{created.GetProperty("id").GetGuid()}/end", new { reason = "user", secondsUsed = 1800 }), 200);
        var stats = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}/stats"), 200);
        Assert.Equal((1, 0.5, 1_200_000L), (stats.GetProperty("sessionsCount").GetInt32(), stats.GetProperty("totalHours").GetDouble(), stats.GetProperty("spent").GetProperty("amount").GetInt64()));
        Assert.True(stats.GetProperty("rank").GetInt32() >= 1);

        var achievements = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}/achievements"), 200);
        Assert.Equal(0, achievements.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Loyalty_is_the_clubs_level_by_lifetime_spend_with_its_discount_as_perk()
    {
        await Players.ExecuteAsync(Server, """
            UPDATE clubs SET settings = settings || '{"loyalty":[{"level":1,"name":"Новичок","minSpent":0,"discountPct":0},{"level":2,"name":"Игрок","minSpent":1000000,"discountPct":3},{"level":3,"name":"Про","minSpent":5000000,"discountPct":5}]}'::jsonb
            """);
        try
        {
            var (agent, player) = await Players.SignedInAsync(Server);
            await Players.StartAsync(agent, player); // 1 200 000 spent: level 2
            using var request = agent.Request(HttpMethod.Get, $"/api/v1/users/{player.Id}/loyalty");
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
            var loyalty = await Players.ReadAsync(await Server.Http.SendAsync(request), 200);
            Assert.Equal((1, 12_000, 50_000), (loyalty.GetProperty("level").GetInt32(), loyalty.GetProperty("points").GetInt32(), loyalty.GetProperty("nextLevelAt").GetInt32()));
            Assert.Equal("3% off play time", Assert.Single(loyalty.GetProperty("perks").EnumerateArray()).GetString());
            Assert.Equal(1, (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 200)).GetProperty("loyaltyLevel").GetInt32());
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'loyalty'");
        }
    }

    [Fact]
    public async Task Game_settings_list_is_empty_delete_is_204_and_the_rest_501()
    {
        // Not in the vendored contract (DESIGN §12.2 item 2), so these go around the contract-validating client.
        var (agent, player) = await Players.SignedInAsync(Server);
        using var http = Server.CreateClient();
        using (var list = await http.SendAsync(agent.Request(HttpMethod.Get, $"/api/v1/users/{player.Id}/game-settings")))
        {
            Assert.Equal(0, (await Players.ReadAsync(list, 200)).GetProperty("items").GetArrayLength());
        }

        var game = Guid.NewGuid();
        using (var delete = await http.SendAsync(agent.Request(HttpMethod.Delete, $"/api/v1/users/{player.Id}/game-settings/{game}")))
        {
            Assert.Equal(204, (int)delete.StatusCode);
        }

        using var get = await http.SendAsync(agent.Request(HttpMethod.Get, $"/api/v1/users/{player.Id}/game-settings/{game}"));
        Contract.AssertError(await Players.ReadAsync(get, 501), "notImplemented", "notImplemented");
    }

    [Fact]
    public async Task Tariffs_are_filtered_by_zone_and_always_answered_200()
    {
        var agent = await TestAgent.CreateAsync(Server);
        using var all = await agent.SendAsync(HttpMethod.Get, "/api/v1/tariffs?zone=");
        var names = (await Players.ReadAsync(all, 200)).GetProperty("items").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("Standard", names);
        Assert.Contains("VIP", names);
        var etag = all.Headers.ETag!.Tag;

        using var vip = await agent.SendAsync(HttpMethod.Get, "/api/v1/tariffs?zone=vip");
        var vipNames = (await Players.ReadAsync(vip, 200)).GetProperty("items").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("VIP", vipNames);
        Assert.Contains("Standard", vipNames); // no zones = every zone

        using var standardOnly = await agent.SendAsync(HttpMethod.Get, "/api/v1/tariffs?zone=Bootcamp");
        Assert.DoesNotContain("VIP", (await Players.ReadAsync(standardOnly, 200)).GetProperty("items").EnumerateArray().Select(t => t.GetProperty("name").GetString()));

        using var request = agent.Request(HttpMethod.Get, "/api/v1/tariffs?zone=");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        using var again = await Server.Http.SendAsync(request);
        Assert.Equal(200, (int)again.StatusCode);
        Assert.Equal(etag, again.Headers.ETag!.Tag);
        var night = (await Players.ReadAsync(again, 200)).GetProperty("items").EnumerateArray().Single(t => t.GetProperty("isPackage").GetBoolean());
        Assert.Equal(("22:00", 300), (night.GetProperty("timeWindows")[0].GetProperty("from").GetString(), night.GetProperty("packageMinutes").GetInt32()));
    }
}
