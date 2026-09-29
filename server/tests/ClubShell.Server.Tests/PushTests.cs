using System.Text.Json;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace ClubShell.Server.Tests;

/// <summary>
/// The required pushes over <c>/ws/agent</c> (DESIGN §6.5), each frame checked against its AsyncAPI message:
/// <c>sessionUpdated</c> and <c>walletUpdated</c> after a purchase; no <c>userRevoked</c> for a logout the PC asked for;
/// a session the tick ends is announced by the <c>endSession</c> command alone.
/// </summary>
public sealed class PushTests(KestrelServerFixture server) : LedgerCheckedTest(server), IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task Purchase_pushes_session_and_wallet_and_logout_revokes_nothing()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        using var socket = await ConnectAsync(agent);

        var (_, created) = await Players.StartAsync(agent, player);
        var session = await PushAsync(socket, "sessionUpdated");
        Assert.Equal(created.GetProperty("id").GetGuid(), session.GetProperty("id").GetGuid());
        var wallet = await PushAsync(socket, "walletUpdated");
        Assert.Equal((player.Id, 8_800_000L), (wallet.GetProperty("userId").GetGuid(), wallet.GetProperty("amount").GetProperty("amount").GetInt64()));

        await Players.ReadAsync(await agent.PostAsync("/api/v1/auth/logout", new { reason = "user" }), 204);
        Assert.Equal("ended", (await PushAsync(socket, "sessionUpdated")).GetProperty("state").GetString());

        // userRevoked would make the agent report auth.expired{revoked} after a normal logout: the next frame is the next purchase.
        await agent.LoginAsync(player);
        var (_, next) = await Players.StartAsync(agent, player);
        Assert.Equal(next.GetProperty("id").GetGuid(), (await PushAsync(socket, "sessionUpdated")).GetProperty("id").GetGuid());
    }

    /// <summary>
    /// A pushed <c>ended</c> would reach the agent first and close the session as <c>admin</c> ("ended by an administrator");
    /// the command carries <c>timeUp</c>. Postpaid on 20 000 at 20 000/min: stopped at 60 s by the tick 61 s in.
    /// </summary>
    [Fact]
    public async Task A_session_the_tick_ends_is_announced_by_the_end_session_command_only()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 20_000);
        using var socket = await ConnectAsync(agent);
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        await PushAsync(socket, "sessionUpdated");

        Server.Clock.Advance(TimeSpan.FromSeconds(61));
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Server.Services.GetRequiredService<SessionTickWorker>().RunOnceAsync();

        Assert.Equal(0, (await PushAsync(socket, "walletUpdated")).GetProperty("amount").GetProperty("amount").GetInt64());
        var command = await socket.ReceiveAsync();
        Assert.Equal(("command", "endSession"), (command.GetProperty("type").GetString(), command.GetProperty("name").GetString()));
        Assert.Equal((created.GetProperty("id").GetGuid(), "timeUp"),
            (command.GetProperty("payload").GetProperty("sessionId").GetGuid(), command.GetProperty("payload").GetProperty("reason").GetString()));
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
