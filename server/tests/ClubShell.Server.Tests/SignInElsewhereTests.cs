using System.Text.Json;
using ClubShell.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// One player, one PC (SERVER_API.md §4.3): a sign-in signs the player out of every other PC (<c>userRevoked
/// {reason: signedInElsewhere}</c>); an open session on another PC is <c>409 activeSessionElsewhere</c> until the player
/// confirms <c>endElsewhere</c>, which ends it like a sign-out there (settled with <c>user</c>, <c>endSession</c> queued, the
/// token deleted, <c>userRevoked</c> pushed) and signs in here — or ends nothing when this sign-in is refused after all.
/// Standard costs 20 000 tiyin a minute.
/// </summary>
public sealed class SignInElsewhereTests(KestrelServerFixture server) : LedgerCheckedTest(server), IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task End_elsewhere_ends_the_other_session_like_a_sign_out_there_and_signs_that_pc_out()
    {
        var (b, player) = await Players.SignedInAsync(Server);
        using var socket = await ConnectAsync(b);
        var (_, created) = await Players.StartAsync(b, player, prepaid: false);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("sessionUpdated", (await socket.ReceiveAsync()).GetProperty("name").GetString());
        Server.Clock.Advance(TimeSpan.FromMinutes(10));

        // Without the confirmation: 409 naming the other PC, nothing changes.
        var a = await TestAgent.CreateAsync(Server);
        var conflict = await Contract.ReadErrorAsync(await a.PostAsync("/api/v1/auth/login", Login(a, player)), 409, "conflict", "activeSessionElsewhere");
        var details = conflict.GetProperty("error").GetProperty("details");
        Assert.Equal(b.PcId, details.GetProperty("pcId").GetGuid());
        Assert.False(string.IsNullOrEmpty(details.GetProperty("pcName").GetString()));
        Assert.Equal("active", await StateAsync(id));

        // Confirmed: the session on B is settled as the player's own end (10 minutes), B is signed out, A signed in.
        var auth = await Players.ReadAsync(await a.PostAsync("/api/v1/auth/login", Login(a, player, endElsewhere: true)), 200);
        Assert.Equal(player.Id, auth.GetProperty("user").GetProperty("id").GetGuid());
        Assert.False(auth.TryGetProperty("session", out var session) && session.ValueKind != JsonValueKind.Null);
        Assert.Equal(("ended", "user"), (await StateAsync(id), await Players.ScalarAsync<string>(Server, "SELECT end_reason FROM sessions WHERE id = @id", new { id })));
        Assert.Equal(10_000_000 - 200_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(a.PcId, await Players.ScalarAsync<Guid>(Server, "SELECT pc_id FROM user_tokens WHERE user_id = @Id", new { player.Id }));

        // B hears it as a sign-out: endSession {user} (not a pushed «ended», which reads as the desk's), then userRevoked.
        var command = await socket.ReceiveAsync();
        Assert.Equal(("command", "endSession", id, "user"), (command.GetProperty("type").GetString(), command.GetProperty("name").GetString(),
            command.GetProperty("payload").GetProperty("sessionId").GetGuid(), command.GetProperty("payload").GetProperty("reason").GetString()));
        var revoked = await socket.ReceiveAsync();
        Contract.AssertMessage("pushUserRevoked", revoked);
        Assert.Equal(("userRevoked", player.Id, "signedInElsewhere"), (revoked.GetProperty("name").GetString(),
            revoked.GetProperty("payload").GetProperty("userId").GetGuid(), revoked.GetProperty("payload").GetProperty("reason").GetString()));

        // B's agent then ends its local session: 409 with the ended session, nothing settled twice; its token is gone.
        await Contract.ReadErrorAsync(await b.PostAsync($"/api/v1/sessions/{id}/end", new { reason = "admin", secondsUsed = 600 }), 409, "sessionNotActive");
        await Contract.ReadErrorAsync(await b.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 401, "unauthorized", "userToken");
        Assert.Equal(10_000_000 - 200_000, await Players.BalanceAsync(Server, player.Id));

        // A plays on with the token it got.
        a.UserToken = auth.GetProperty("accessToken").GetString();
        Assert.Equal(201, (await Players.StartAsync(a, player)).Status);
    }

    [Fact]
    public async Task A_sign_in_refused_after_all_ends_nothing_elsewhere()
    {
        var (b, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(b, player);
        var id = created.GetProperty("id").GetGuid();

        // A holds someone else's session: pcOccupied, and B's session and token stay.
        var (a, other) = await Players.SignedInAsync(Server);
        await Players.StartAsync(a, other);
        await Contract.ReadErrorAsync(await a.PostAsync("/api/v1/auth/login", Login(a, player, endElsewhere: true)), 403, "forbidden", "pcOccupied");
        Assert.Equal("active", await StateAsync(id));
        await Players.ReadAsync(await b.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 200);

        // Blacklisted meanwhile: banned, nothing ended.
        var free = await TestAgent.CreateAsync(Server);
        await Players.ProfileAsync(Server, player, blacklisted: true);
        await Contract.ReadErrorAsync(await free.PostAsync("/api/v1/auth/login", Login(free, player, endElsewhere: true)), 403, "forbidden", "banned");
        Assert.Equal("active", await StateAsync(id));
        await Players.ProfileAsync(Server, player, blacklisted: false);

        // A wrong password with the confirmation: 401, nothing ended.
        var wrong = await free.PostAsync("/api/v1/auth/login",
            new { kind = "password", username = player.Username, password = "wrong", pcId = free.PcId, hwid = free.Hwid, endElsewhere = true });
        await Contract.ReadErrorAsync(wrong, 401, "unauthorized", "badCredentials");
        Assert.Equal("active", await StateAsync(id));
        var owner = await LoginAsync(Server, OwnerPin);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = a.PcId });
    }

    /// <summary>
    /// Signed in on B with no session, the player signs in on A: B's token is deleted and B told <c>userRevoked
    /// signedInElsewhere</c> — a stale token there would let B's kiosk start a session on the next wallet push.
    /// </summary>
    [Fact]
    public async Task Tokens_of_other_pcs_are_revoked_at_sign_in()
    {
        var (b, player) = await Players.SignedInAsync(Server);
        using var socket = await ConnectAsync(b);
        var a = await TestAgent.CreateAsync(Server);
        await a.LoginAsync(player);

        var revoked = await socket.ReceiveAsync();
        Assert.Equal(("push", "userRevoked", player.Id, "signedInElsewhere"), (revoked.GetProperty("type").GetString(), revoked.GetProperty("name").GetString(),
            revoked.GetProperty("payload").GetProperty("userId").GetGuid(), revoked.GetProperty("payload").GetProperty("reason").GetString()));
        var refused = await Contract.ReadErrorAsync(await b.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 401, "unauthorized", "userToken");
        Assert.Equal("invalid", refused.GetProperty("error").GetProperty("details").GetProperty("problem").GetString());
        Assert.Equal(a.PcId, await Players.ScalarAsync<Guid>(Server, "SELECT pc_id FROM user_tokens WHERE user_id = @Id", new { player.Id }));

        // Signing in again on the same PC revokes nothing (B has no token left; A's is replaced).
        await a.LoginAsync(player);
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE user_id = @Id", new { player.Id }));
    }

    private static object Login(TestAgent agent, TestPlayer player, bool? endElsewhere = null) =>
        new { kind = "password", username = player.Username, password = Players.Password, pcId = agent.PcId, hwid = agent.Hwid, endElsewhere };

    private async Task<WsTestSocket> ConnectAsync(TestAgent agent)
    {
        var socket = await WsTestSocket.ConnectAsync((KestrelServerFixture)Server, agent.AccessToken);
        await Wait.UntilAsync(() => Server.Services.GetRequiredService<AgentSocketHub>().IsConnected(agent.PcId));
        return socket;
    }

    private Task<string> StateAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id });
}

/// <summary>
/// The player token's sliding lifetime (SERVER_API.md §2.3): 12 hours, counted again from each heartbeat of the PC while
/// the player's session is open there; without an open session it expires as issued.
/// </summary>
public sealed class TokenSlidingTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task A_token_lives_on_while_its_players_session_is_open_on_that_pc()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);

        Server.Clock.Advance(TimeSpan.FromHours(11));
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        Server.Clock.Advance(TimeSpan.FromHours(2));
        await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 200); // 13 h after the sign-in

        // Ended at 13 h: the heartbeats no longer extend it, the last one (11 h) holds until 23 h.
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{created.GetProperty("id").GetGuid()}/end", new { reason = "user", secondsUsed = 0 }), 200);
        Server.Clock.Advance(TimeSpan.FromHours(9));
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 200); // 22 h
        Server.Clock.Advance(TimeSpan.FromHours(1));
        var expired = await Contract.ReadErrorAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 401, "unauthorized", "userToken");
        Assert.Equal("expired", expired.GetProperty("error").GetProperty("details").GetProperty("problem").GetString());
    }

    [Fact]
    public async Task A_token_without_a_session_expires_as_issued_and_is_not_revived()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        Server.Clock.Advance(TimeSpan.FromHours(11));
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        Server.Clock.Advance(TimeSpan.FromHours(1));
        await Contract.ReadErrorAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 401, "unauthorized", "userToken");

        // An expired token stays expired, even if a session opens on that PC (the desk seats the player there).
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 });
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Contract.ReadErrorAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"), 401, "unauthorized", "userToken");
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId });
    }
}
