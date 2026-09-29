using System.Net.Http.Json;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Server.Agents;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>
/// Command queue (DESIGN §6.4): expiry, <c>supersedes</c>, repeated acks, 404 for unknown or foreign commands, and the
/// N1 rule — <c>pendingCommands</c> leaves out what the live socket already carries.
/// </summary>
public sealed class CommandQueueTests(KestrelServerFixture server) : IClassFixture<KestrelServerFixture>
{
    private CommandDispatcher Dispatcher => server.Services.GetRequiredService<CommandDispatcher>();

    private CommandRepository Commands => server.Services.GetRequiredService<CommandRepository>();

    [Fact]
    public async Task Expired_command_is_neither_listed_nor_counted()
    {
        var agent = await TestAgent.CreateAsync(server);
        var queued = await Dispatcher.EnqueueAsync(await ClubOfAsync(agent), agent.PcId, NewCommand.Lock(new LockCommand("staff")));
        Assert.Equal(queued.Ts.AddMinutes(10), queued.ExpiresAt);
        Assert.Single(await ListAsync(agent));

        server.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(await ListAsync(agent));
        Assert.Equal(0, await PendingCountAsync(agent));
    }

    [Fact]
    public async Task Superseded_undelivered_command_is_never_delivered()
    {
        var agent = await TestAgent.CreateAsync(server);
        var club = await ClubOfAsync(agent);
        var locked = await Dispatcher.EnqueueAsync(club, agent.PcId, NewCommand.Lock(new LockCommand("staff")));
        var unlocked = await Dispatcher.EnqueueAsync(club, agent.PcId, NewCommand.Unlock(), supersedes: locked.Id);

        var items = await ListAsync(agent);
        var only = Assert.Single(items);
        Assert.Equal(unlocked.Id, only.GetProperty("id").GetGuid());
        Assert.Equal(locked.Id, only.GetProperty("supersedes").GetGuid());
        Assert.Equal("unlock", only.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Ack_is_accepted_again_and_unknown_or_foreign_commands_are_404()
    {
        var agent = await TestAgent.CreateAsync(server);
        var other = await TestAgent.CreateAsync(server);
        var message = await Dispatcher.EnqueueAsync(await ClubOfAsync(agent), agent.PcId,
            NewCommand.Message(new MessageCommand(Guid.NewGuid(), "Администратор", "Подойдите к стойке", NotificationLevel.Info, RequiresAck: true)));
        var now = server.Clock.GetUtcNow();

        // First ack when shown, the second, out-of-band one when the player pressed OK: both 204, the last one is kept.
        foreach (var result in new object[] { new { deliveredAt = now }, new { deliveredAt = now, ackedAt = now.AddSeconds(5) } })
        {
            using var ack = await agent.SendAsync(HttpMethod.Post, agent.Path($"commands/{message.Id}/ack"), JsonSerializer.Serialize(new { ok = true, result }));
            Assert.Equal(204, (int)ack.StatusCode);
        }

        var stored = await Commands.WaitForAckAsync(message.Id, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(stored!.Ok);
        Assert.True(stored.Result!.Value.TryGetProperty("ackedAt", out _));
        Assert.Empty(await ListAsync(agent));

        using (var unknown = await agent.SendAsync(HttpMethod.Post, agent.Path($"commands/{Guid.NewGuid()}/ack"), """{"ok":true}"""))
        {
            var error = await Contract.ReadErrorAsync(unknown, 404, "notFound");
            Assert.Equal("command", error.GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        }

        using var foreign = await other.SendAsync(HttpMethod.Post, other.Path($"commands/{message.Id}/ack"), """{"ok":true}""");
        await Contract.ReadErrorAsync(foreign, 404, "notFound");
    }

    [Fact]
    public async Task Pending_count_leaves_out_what_the_live_socket_carries()
    {
        var agent = await TestAgent.CreateAsync(server);
        var hub = server.Services.GetRequiredService<AgentSocketHub>();
        Guid id;
        using (var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken))
        {
            await Wait.UntilAsync(() => hub.IsConnected(agent.PcId));
            id = (await Dispatcher.EnqueueAsync(await ClubOfAsync(agent), agent.PcId, NewCommand.Lock(new LockCommand("staff")))).Id;
            var frame = await socket.ReceiveAsync();
            Contract.AssertMessage("commandLock", frame);
            Assert.Equal(id, frame.GetProperty("id").GetGuid());

            // Delivered over WS and not acked yet: the REST drain must not pick it up, but GET /commands still lists it.
            Assert.Equal(0, await PendingCountAsync(agent));
            Assert.Single(await ListAsync(agent));
        }

        await Wait.UntilAsync(() => !hub.IsConnected(agent.PcId));
        Assert.Equal(1, await PendingCountAsync(agent));
    }

    [Fact]
    public async Task Waiting_for_an_ack_that_never_comes_times_out_with_null()
    {
        var agent = await TestAgent.CreateAsync(server);
        var queued = await Dispatcher.EnqueueAsync(await ClubOfAsync(agent), agent.PcId, NewCommand.Unlock());
        Assert.Null(await Commands.WaitForAckAsync(queued.Id, TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    private async Task<Guid> ClubOfAsync(TestAgent agent)
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        return await c.QuerySingleAsync<Guid>("SELECT club_id FROM pcs WHERE id = @id", new { id = agent.PcId });
    }

    private async Task<List<JsonElement>> ListAsync(TestAgent agent)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, agent.Path("commands"));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
    }

    private async Task<int> PendingCountAsync(TestAgent agent)
    {
        using var response = await agent.SendAsync(HttpMethod.Post, agent.Path("heartbeat"), TestAgent.Heartbeat());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pendingCommands").GetInt32();
    }
}

/// <summary>
/// The command catalogue (DESIGN §6.4 table): every one of the ten AsyncAPI commands with <c>x-server-status:
/// required</c> has a builder whose frame matches its AsyncAPI message; the nine <c>notImplemented</c> ones are refused.
/// </summary>
public sealed class CommandCatalogTests
{
    public static TheoryData<string> Builders => new(Build().Select(c => c.Type.ToWireName()));

    [Fact]
    public void Required_commands_are_exactly_the_asyncapi_ones()
    {
        var required = Contract.AsyncApi.GetProperty("operations").EnumerateObject()
            .Where(o => o.Name.StartsWith("sendCommand", StringComparison.Ordinal) && o.Value.GetProperty("x-server-status").GetString() == "required")
            .Select(o => JsonNamingPolicy.CamelCase.ConvertName(o.Name["sendCommand".Length..]))
            .Order();
        Assert.Equal(required, CommandDispatcher.Required.Select(t => t.ToWireName()).Order());
        Assert.Equal(CommandDispatcher.Required.Order(), Build().Select(c => c.Type).Order());
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void Builder_makes_a_frame_of_its_asyncapi_message(string name)
    {
        var command = Build().Single(c => c.Type.ToWireName() == name);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var envelope = new ServerCommandEnvelope(Guid.CreateVersion7(), now, name, command.Payload, null, Guid.CreateVersion7(), now.AddMinutes(10));
        var frame = JsonElement.Parse(JsonDefaults.Serialize(AgentSocketHub.CommandFrame(envelope)));
        Contract.AssertMessage("command" + char.ToUpperInvariant(name[0]) + name[1..], frame);
        Assert.True(frame.TryGetProperty("payload", out _), "payload is always present");
    }

    [Fact]
    public async Task Not_implemented_commands_are_refused()
    {
        var dispatcher = new CommandDispatcher(null!, null!, new AgentOptions());
        var refused = Enum.GetValues<ServerCommandType>().Where(t => !CommandDispatcher.Required.Contains(t)).ToList();
        Assert.Equal(9, refused.Count);
        foreach (var type in refused)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.EnqueueAsync(Guid.NewGuid(), Guid.NewGuid(), new NewCommand(type, null)));
        }
    }

    [Fact]
    public void Keepalive_frames_match_their_messages()
    {
        var ping = WsFrame.Ping(DateTimeOffset.UtcNow);
        Contract.AssertMessage("ping", JsonElement.Parse(JsonDefaults.Serialize(ping)));
        Contract.AssertMessage("serverPong", JsonElement.Parse(JsonDefaults.Serialize(WsFrame.PongFor(ping, DateTimeOffset.UtcNow))));
    }

    private static List<NewCommand> Build()
    {
        var policy = JsonDefaults.Deserialize<Policy>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "seed", "policies.example.json")))!;
        return
        [
            NewCommand.Lock(new LockCommand("staff", "Подойдите к стойке")),
            NewCommand.Unlock(),
            NewCommand.Message(new MessageCommand(Guid.NewGuid(), "Администратор", "Подойдите к стойке", NotificationLevel.Info, RequiresAck: true)),
            NewCommand.Reboot(new PowerCommand(5, Force: false)),
            NewCommand.Shutdown(new PowerCommand(30, Force: false, "Клуб закрывается")),
            NewCommand.EndSession(new EndSessionCommand(Guid.NewGuid(), SessionEndReason.Admin)),
            NewCommand.ExtendSession(new ExtendSessionCommand(Guid.NewGuid(), 30, Charge: false)),
            NewCommand.SetPolicy(policy),
            NewCommand.ReloadPolicy(),
            NewCommand.RefreshConfig(new RefreshConfigCommand(Config: true, Games: true, Tariffs: true)),
        ];
    }
}
