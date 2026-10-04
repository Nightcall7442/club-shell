using System.Text.Json;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Who is signed in on a PC (cash desk part 2, D-26..D-29): no sign-in onto a PC that holds another player's open session
/// (<c>403 pcOccupied</c>; the player's own desk session still signs in); a desk open signs out anyone else signed in there
/// (<c>userRevoked seatTaken</c> before the session push), and a sign-in racing it never keeps a token; a desk end signs the
/// session's player out, whatever the role (<c>userRevoked sessionEnded</c> after the <c>endSession</c> command).
/// </summary>
public sealed class SignInSafetyTests(KestrelServerFixture server) : LedgerCheckedTest(server), IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task A_password_login_on_a_pc_holding_another_players_session_is_refused()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        var seated = await Players.CreateAsync(Server, card: "CARD-" + Guid.NewGuid().ToString("N")[..8]);
        var other = await Players.CreateAsync(Server, card: "CARD-" + Guid.NewGuid().ToString("N")[..8]);
        var opened = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = seated.Id, tariffId = Players.Standard, minutes = 60 });

        using (var password = await agent.PostAsync("/api/v1/auth/login", new { kind = "password", username = other.Username, password = Players.Password, pcId = agent.PcId, hwid = agent.Hwid }))
        {
            await Contract.ReadErrorAsync(password, 403, "forbidden", "pcOccupied");
        }

        var card = await Players.ScalarAsync<string>(Server, "SELECT card_id FROM users WHERE id = @Id", new { other.Id });
        using (var byCard = await agent.PostAsync("/api/v1/auth/login", new { kind = "card", cardId = card, pcId = agent.PcId, hwid = agent.Hwid }))
        {
            await Contract.ReadErrorAsync(byCard, 403, "forbidden", "pcOccupied");
        }

        // The seated player signs in to the desk's session.
        var auth = await agent.LoginAsync(seated);
        Assert.Equal(opened.GetProperty("session").GetProperty("id").GetGuid(), auth.GetProperty("session").GetProperty("id").GetGuid());
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE user_id = @Id", new { other.Id }));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
    }

    [Fact]
    public async Task Seating_on_a_pc_with_another_signed_in_player_revokes_that_player()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var (agent, signedIn) = await Players.SignedInAsync(Server);
        var seated = await Players.CreateAsync(Server);
        using var socket = await ConnectAsync(agent);

        var opened = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = seated.Id, tariffId = Players.Standard, minutes = 60 });
        var revoked = await PushAsync(socket, "userRevoked");
        Assert.Equal((signedIn.Id, "seatTaken"), (revoked.GetProperty("userId").GetGuid(), revoked.GetProperty("reason").GetString()));
        Assert.Equal(opened.GetProperty("session").GetProperty("id").GetGuid(), (await PushAsync(socket, "sessionUpdated")).GetProperty("id").GetGuid());
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE pc_id = @PcId", new { agent.PcId }));
        using var after = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{signedIn.Id}");
        await Contract.ReadErrorAsync(after, 401, "unauthorized", "userToken");
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
    }

    /// <summary>
    /// The PC's advisory lock orders the two: either the sign-in comes first and the desk open deletes its token, or the open
    /// comes first and the sign-in is 403. A token of the other player on a PC with the desk's session is never left behind.
    /// </summary>
    [Fact]
    public async Task A_sign_in_racing_a_desk_open_never_keeps_a_token()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        for (var i = 0; i < 8; i++)
        {
            var agent = await TestAgent.CreateAsync(Server);
            var seated = await Players.CreateAsync(Server);
            var other = await Players.CreateAsync(Server);
            var login = LoginStatusAsync(agent, other);
            var open = ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = seated.Id, tariffId = Players.Standard, minutes = 60 });
            await Task.WhenAll(login, open);
            Assert.Contains(await login, new[] { 200, 403 });
            Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE pc_id = @PcId AND user_id = @Id", new { agent.PcId, other.Id }));
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        }
    }

    [Fact]
    public async Task A_desk_end_signs_the_player_out_for_every_role()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);

        // A member signed in on the PC, seated at the desk, ended at the desk.
        var (agent, member) = await Players.SignedInAsync(Server);
        using (var socket = await ConnectAsync(agent))
        {
            var id = (await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = member.Id, tariffId = Players.Standard, minutes = 60 }))
                .GetProperty("session").GetProperty("id").GetGuid();
            Assert.Equal(id, (await PushAsync(socket, "sessionUpdated")).GetProperty("id").GetGuid());
            Assert.Equal(member.Id, (await PushAsync(socket, "walletUpdated")).GetProperty("userId").GetGuid());

            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
            var command = await socket.ReceiveAsync();
            Assert.Equal(("command", "endSession"), (command.GetProperty("type").GetString(), command.GetProperty("name").GetString()));
            var revoked = await PushAsync(socket, "userRevoked");
            Assert.Equal((member.Id, "sessionEnded"), (revoked.GetProperty("userId").GetGuid(), revoked.GetProperty("reason").GetString()));
        }

        using (var after = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{member.Id}"))
        {
            await Contract.ReadErrorAsync(after, 401, "unauthorized", "userToken");
        }

        // A walk-in guest signed in through «Гость».
        var guestPc = await TestAgent.CreateAsync(Server);
        var guestId = (await DeskGuests.SeatAsync(Server, cashier, guestPc.PcId)).GetProperty("user").GetProperty("id").GetGuid();
        await DeskGuests.PressAsync(guestPc);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = guestPc.PcId });
        using var guestAfter = await guestPc.SendAsync(HttpMethod.Get, $"/api/v1/users/{guestId}");
        await Contract.ReadErrorAsync(guestAfter, 401, "unauthorized", "userToken");
    }

    private async Task<int> LoginStatusAsync(TestAgent agent, TestPlayer player)
    {
        using var response = await agent.PostAsync("/api/v1/auth/login", new { kind = "password", username = player.Username, password = Players.Password, pcId = agent.PcId, hwid = agent.Hwid });
        return (int)response.StatusCode;
    }

    private async Task<WsTestSocket> ConnectAsync(TestAgent agent)
    {
        var socket = await WsTestSocket.ConnectAsync((KestrelServerFixture)Server, agent.AccessToken);
        await Wait.UntilAsync(() => Server.Services.GetRequiredService<AgentSocketHub>().IsConnected(agent.PcId));
        return socket;
    }

    private static async Task<JsonElement> PushAsync(WsTestSocket socket, string name)
    {
        var frame = await socket.ReceiveAsync();
        Assert.Equal(("push", name), (frame.GetProperty("type").GetString(), frame.GetProperty("name").GetString()));
        Contract.AssertMessage("push" + char.ToUpperInvariant(name[0]) + name[1..], frame);
        return frame.GetProperty("payload");
    }
}

/// <summary>
/// The tick's time-up (D-28): a transient guest is signed out of the PC with its session (a throwaway account has nothing
/// more to play for there), a member whose prepaid time ran out stays signed in (the kiosk may start more on the balance).
/// </summary>
public sealed class TimeUpSignOutTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task A_time_up_signs_out_a_transient_guest_but_not_a_member()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var guestPc = await TestAgent.CreateAsync(Server);
        var guestId = (await DeskGuests.SeatAsync(Server, cashier, guestPc.PcId, minutes: 30)).GetProperty("user").GetProperty("id").GetGuid();
        await DeskGuests.PressAsync(guestPc);
        var (memberPc, member) = await Players.SignedInAsync(Server);
        var (status, _) = await Players.StartAsync(memberPc, member, minutes: 30);
        Assert.Equal(201, status);

        Server.Clock.Advance(TimeSpan.FromMinutes(31) + TimeSpan.FromSeconds(1));
        await Players.ReadAsync(await guestPc.HeartbeatAsync(), 200);
        await Players.ReadAsync(await memberPc.HeartbeatAsync(), 200);
        await Server.Services.GetRequiredService<SessionTickWorker>().RunOnceAsync();

        Assert.Equal("timeUp timeUp", await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(end_reason, ' ') FROM sessions WHERE user_id IN (@guestId, @Id)", new { guestId, member.Id }));
        using (var guest = await guestPc.SendAsync(HttpMethod.Get, $"/api/v1/users/{guestId}"))
        {
            await Contract.ReadErrorAsync(guest, 401, "unauthorized", "userToken");
        }

        using var stays = await memberPc.SendAsync(HttpMethod.Get, $"/api/v1/users/{member.Id}");
        await Players.ReadAsync(stays, 200);
    }
}
