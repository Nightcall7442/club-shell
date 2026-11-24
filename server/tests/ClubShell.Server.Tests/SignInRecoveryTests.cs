using System.Text.Json;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace ClubShell.Server.Tests;

/// <summary>
/// D-100, sign-ins that must not end under the player: a PC that signed its player in from its offline cache gets a player
/// token for the player of its own open session only (<c>POST /agents/{pcId}/players/{userId}/token</c>); the heartbeat
/// reports the PC's player token as slid, so the agent's copy of the expiry follows; a temporary guest's prepaid clock
/// stops behind a lock their ended sign-in caused (the agent's <c>locked</c> event with <c>clockStopped</c>), so their paid
/// time neither runs out nor signs them out, while a member's lock keeps the clock running (D-12).
/// </summary>
public sealed class SignInRecoveryTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private SessionTickWorker Tick => Server.Services.GetRequiredService<SessionTickWorker>();

    [Fact]
    public async Task A_player_token_is_issued_only_for_the_player_of_the_pcs_own_open_session()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var other = await Players.CreateAsync(Server);

        // No session of theirs here: the PC's credentials alone sign nobody in.
        await RefusedAsync(agent, player.Id, 403, "forbidden", "notSessionPlayer");

        await agent.LoginAsync(player);
        var (status, created) = await Players.StartAsync(agent, player);
        Assert.Equal(201, status);
        agent.UserToken = null;

        await RefusedAsync(agent, other.Id, 403, "forbidden", "notSessionPlayer");
        await RefusedAsync(await TestAgent.CreateAsync(Server), player.Id, 403, "forbidden", "notSessionPlayer");
        await RefusedAsync(agent, Guid.NewGuid(), 404, "notFound");

        {
            var (issued, auth) = await agent.RawPostAsync(agent.Path($"players/{player.Id}/token"), "{}");
            Assert.Equal(200, issued);
            Assert.Equal(player.Id, auth.GetProperty("user").GetProperty("id").GetGuid());
            Assert.Equal(created.GetProperty("id").GetGuid(), auth.GetProperty("session").GetProperty("id").GetGuid());
            Assert.True(!auth.TryGetProperty("offlineHash", out var hash) || hash.ValueKind == JsonValueKind.Null);
            agent.UserToken = auth.GetProperty("accessToken").GetString();
        }

        // The token works for the player's calls.
        using (var profile = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"))
        {
            await Players.ReadAsync(profile, 200);
        }

        await Players.ProfileAsync(Server, player, blacklisted: true);
        agent.UserToken = null;
        await RefusedAsync(agent, player.Id, 403, "forbidden", "banned");
        await Players.ProfileAsync(Server, player, blacklisted: false);
    }

    [Fact]
    public async Task The_heartbeat_reports_the_pcs_player_token_as_slid()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        Assert.Equal(201, (await Players.StartAsync(agent, player, minutes: 240)).Status);
        Server.Clock.Advance(TimeSpan.FromHours(3));

        var beat = await Players.ReadAsync(await agent.HeartbeatAsync(), 200);

        var token = beat.GetProperty("playerToken");
        Assert.Equal(player.Id, token.GetProperty("userId").GetGuid());
        Assert.Equal(Server.Clock.GetUtcNow().AddHours(12), token.GetProperty("expiresAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));

        using (var logout = await agent.PostAsync("/api/v1/auth/logout", new { reason = "user" }))
        {
            Assert.True(logout.IsSuccessStatusCode);
        }

        var after = await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        Assert.True(!after.TryGetProperty("playerToken", out var none) || none.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_guests_prepaid_clock_stops_behind_a_lock_their_ended_sign_in_caused()
    {
        var (agent, guest) = await Players.SignedInAsync(Server);
        await Players.ExecuteAsync(Server, "UPDATE users SET transient = true WHERE id = @Id", new { guest.Id });
        var (_, created) = await Players.StartAsync(agent, guest, minutes: 30);
        var id = created.GetProperty("id").GetGuid();
        await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(10));

        await EventsTests.PostAsync(agent, id, Guid.NewGuid(), ("locked", Server.Clock.GetUtcNow(), new { origin = "signIn", clockStopped = true }));
        await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(90));

        Assert.Equal("locked", await StateAsync(id));
        Assert.Equal(600, await Players.ScalarAsync<int>(Server, "SELECT used_before_sec FROM sessions WHERE id = @id", new { id }));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE user_id = @Id", new { guest.Id }));

        // The unlock starts the clock again where it stopped.
        await EventsTests.PostAsync(agent, id, Guid.NewGuid(), ("unlocked", Server.Clock.GetUtcNow(), null));
        Assert.Equal("active", await StateAsync(id));
        Assert.Equal(Server.Clock.GetUtcNow().AddMinutes(20), await Players.ScalarAsync<DateTimeOffset>(Server, "SELECT ends_at FROM sessions WHERE id = @id", new { id }), TimeSpan.FromSeconds(1));
        await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(21));
        Assert.Equal("ended", await StateAsync(id));
    }

    [Fact]
    public async Task A_members_lock_keeps_the_clock_running_whatever_the_agent_asks()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player, minutes: 30);
        var id = created.GetProperty("id").GetGuid();

        await EventsTests.PostAsync(agent, id, Guid.NewGuid(), ("locked", Server.Clock.GetUtcNow(), new { origin = "signIn", clockStopped = true }));
        await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(31));

        Assert.Equal("ended", await StateAsync(id));
        Assert.Equal("timeUp", await Players.ScalarAsync<string>(Server, "SELECT end_reason FROM sessions WHERE id = @id", new { id }));
    }

    private static async Task RefusedAsync(TestAgent agent, Guid userId, int status, string code, string? reason = null)
    {
        using var response = await agent.PostAsync(agent.Path($"players/{userId}/token"), null);
        await Contract.ReadErrorAsync(response, status, code, reason);
    }

    private async Task AdvanceOnlineAsync(TestAgent agent, TimeSpan by)
    {
        Server.Clock.Advance(by);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Tick.RunOnceAsync();
    }

    private Task<string> StateAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id });
}
