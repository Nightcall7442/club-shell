using System.Text.Json;
using ClubShell.Server.Agents;
using ClubShell.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// One command to many PCs (cash desk part 3, D-65): each PC answered on its own — done, queued, skipped (offline for power
/// and lock, a busy PC for power without <c>includeBusy</c>, not in the club), failed (rolled back alone in its savepoint);
/// with <c>includeBusy</c> a busy PC's session is ended first as the desk ends it; a replay answers the stored results and
/// queues nothing; the strict key and the fields are checked. The ack wait is 2 s here.
/// </summary>
public sealed class BulkCommandTests(ShortAckServerFixture server) : LedgerCheckedTest(server), IClassFixture<ShortAckServerFixture>
{
    [Fact]
    public async Task Each_pc_gets_its_own_result_and_one_journal_entry()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var offline = await TestAgent.CreateAsync(Server);
        var idle = await Seats.OnlineAsync(Server);
        var (live, socket) = await LiveAsync();
        using (socket)
        {
            var foreign = Guid.NewGuid();
            var call = RawAsync(Server, HttpMethod.Post, "/pcs/commands", owner,
                new { pcIds = new[] { offline.PcId, idle.PcId, live.PcId, foreign }, kind = "lock", text = "Перерыв" }, Guid.NewGuid());
            await AckAsync(socket, "lock");
            var (status, body, _) = await call;
            Assert.Equal(200, status);
            var results = body.GetProperty("results").EnumerateArray().ToList();
            Assert.Equal(
                [(offline.PcId, "skipped", "offline"), (idle.PcId, "queued", null), (live.PcId, "done", null), (foreign, "skipped", "notFound")],
                results.Select(r => (r.GetProperty("pcId").GetGuid(), r.GetProperty("outcome").GetString(), r.GetProperty("skipped").GetString())));
            Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, true, JsonValueKind.Null), (results[3].GetProperty("pcName").ValueKind, results[1].GetProperty("ack").ValueKind,
                results[2].GetProperty("ack").GetProperty("ok").GetBoolean(), results[2].GetProperty("ended").ValueKind));
            Assert.Equal(2, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'pcCommand' AND meta ->> 'batchId' = @id",
                new { id = body.GetProperty("batchId").GetGuid().ToString() }));
            Assert.Equal((0, 1), (await CommandsAsync(offline), await CommandsAsync(idle)));

            // A message or an unlock to an offline PC stays queued for it.
            var message = await RawExpectAsync(Server, 200, HttpMethod.Post, "/pcs/commands", owner,
                new { pcIds = new[] { offline.PcId, idle.PcId }, kind = "message", text = "Клуб закрывается через 10 минут", level = "warning" }, Guid.NewGuid());
            Assert.Equal(["queued", "queued"], message.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("outcome").GetString()));
            Assert.Equal("warning", await Players.ScalarAsync<string>(Server,
                "SELECT payload ->> 'level' FROM agent_commands WHERE pc_id = @PcId AND name = 'message'", new { offline.PcId }));

            // unlock supersedes the PC's pending lock, as the single-PC command does.
            await RawExpectAsync(Server, 200, HttpMethod.Post, "/pcs/commands", owner, new { pcIds = new[] { idle.PcId }, kind = "unlock" }, Guid.NewGuid());
            Assert.Equal(["message", "unlock"], (await Players.ReadAsync(await idle.SendAsync(HttpMethod.Get, idle.Path("commands")), 200))
                .GetProperty("items").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        }
    }

    [Fact]
    public async Task A_busy_pc_is_skipped_for_power_unless_includeBusy_ends_its_session_first()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var busy = await Seats.OnlineAsync(Server);
        var seated = await DeskGuests.SeatAsync(Server, owner, busy.PcId);
        var (guest, id) = (seated.GetProperty("user").GetProperty("id").GetGuid(), seated.GetProperty("session").GetProperty("id").GetGuid());
        using (var signIn = await busy.PostAsync("/api/v1/auth/guest", new { pcId = busy.PcId, hwid = busy.Hwid, code = DeskGuests.Code(seated) }))
        {
            Assert.Equal(guest, (await Players.ReadAsync(signIn, 200)).GetProperty("user").GetProperty("id").GetGuid());
        }

        Assert.Equal("queued", (await RawExpectAsync(Server, 200, HttpMethod.Post, "/pcs/commands", owner, new { pcIds = new[] { busy.PcId }, kind = "lock" }, Guid.NewGuid()))
            .GetProperty("results")[0].GetProperty("outcome").GetString());
        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        var skipped = (await RawExpectAsync(Server, 200, HttpMethod.Post, "/pcs/commands", owner, new { pcIds = new[] { busy.PcId }, kind = "reboot" }, Guid.NewGuid()))
            .GetProperty("results")[0];
        Assert.Equal(("skipped", "sessionOpen"), (skipped.GetProperty("outcome").GetString(), skipped.GetProperty("skipped").GetString()));
        Assert.Equal($"active {busy.PcId}", await Seats.StateAsync(Server, id));

        // The desk's confirm did not list this session (the player sat down after it opened): it is not ended unseen.
        var unseen = (await RawExpectAsync(Server, 200, HttpMethod.Post, "/pcs/commands", owner,
                new { pcIds = new[] { busy.PcId }, kind = "reboot", includeBusy = true, sessionIds = new[] { Guid.NewGuid() } }, Guid.NewGuid()))
            .GetProperty("results")[0];
        Assert.Equal(("skipped", "sessionOpen", JsonValueKind.Null), (unseen.GetProperty("outcome").GetString(), unseen.GetProperty("skipped").GetString(),
            unseen.GetProperty("ended").ValueKind));
        Assert.Equal($"active {busy.PcId}", await Seats.StateAsync(Server, id));

        Server.Clock.Advance(TimeSpan.FromMinutes(5));
        await Seats.BeatAsync(busy, id);
        var result = (await RawExpectAsync(Server, 200, HttpMethod.Post, "/pcs/commands", owner,
                new { pcIds = new[] { busy.PcId }, kind = "shutdown", includeBusy = true, sessionIds = new[] { id } }, Guid.NewGuid()))
            .GetProperty("results")[0];
        Assert.Equal("queued", result.GetProperty("outcome").GetString());
        var ended = result.GetProperty("ended");
        Assert.Equal((id, guest, "guest", 0L), (ended.GetProperty("sessionId").GetGuid(), ended.GetProperty("user").GetProperty("id").GetGuid(),
            ended.GetProperty("user").GetProperty("role").GetString(), Amount(ended.GetProperty("charged"))));
        var refunded = Amount(ended.GetProperty("refunded"));
        Assert.True(refunded > 0);

        // As the desk's «Завершить»: ended with the refund, journaled, the guest signed out; endSession goes before the power command.
        Assert.StartsWith("ended", await Seats.StateAsync(Server, id), StringComparison.Ordinal);
        Assert.Equal(refunded, await Players.ScalarAsync<long>(Server, "SELECT amount FROM audit_entries WHERE action = 'sessionEnd' AND meta ->> 'sessionId' = @s", new { s = id.ToString() }));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE pc_id = @PcId", new { busy.PcId }));
        Assert.Equal(["lock", "endSession", "shutdown"], (await Players.ReadAsync(await busy.SendAsync(HttpMethod.Get, busy.Path("commands")), 200))
            .GetProperty("items").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Contains((await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", owner)).GetProperty("guestRefunds").EnumerateArray(), r => r.GetProperty("userId").GetGuid() == guest);

        // The agent ends the session itself before powering off: answered as ended, nothing refunded twice.
        busy.UserToken = null;
        await Contract.ReadErrorAsync(await busy.PostAsync($"/api/v1/sessions/{id}/end", new { reason = "admin", secondsUsed = 300 }), 409, "sessionNotActive");
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE session_id = @id AND type = 'refund'", new { id }));
    }

    [Fact]
    public async Task One_pc_failing_inside_its_savepoint_does_not_roll_back_the_others()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var refused = await Seats.OnlineAsync(Server);
        var fine = await Seats.OnlineAsync(Server);
        await Players.ExecuteAsync(Server,
            $"""
            CREATE FUNCTION test_refuse_command() RETURNS trigger LANGUAGE plpgsql AS $f$
            BEGIN
                IF NEW.pc_id = '{refused.PcId}'::uuid THEN RAISE EXCEPTION 'refused for the test'; END IF;
                RETURN NEW;
            END $f$;
            CREATE TRIGGER test_refuse_command BEFORE INSERT ON agent_commands FOR EACH ROW EXECUTE FUNCTION test_refuse_command();
            """);
        try
        {
            var body = await RawExpectAsync(Server, 200, HttpMethod.Post, "/pcs/commands", owner, new { pcIds = new[] { refused.PcId, fine.PcId }, kind = "lock" }, Guid.NewGuid());
            var results = body.GetProperty("results").EnumerateArray().ToList();
            Assert.Equal(("failed", false, "internal"), (results[0].GetProperty("outcome").GetString(), results[0].GetProperty("ack").GetProperty("ok").GetBoolean(),
                results[0].GetProperty("ack").GetProperty("error").GetProperty("code").GetString()));
            Assert.Equal("queued", results[1].GetProperty("outcome").GetString());
            Assert.Equal((0, 1), (await CommandsAsync(refused), await CommandsAsync(fine)));
            Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'pcCommand' AND meta ->> 'batchId' = @id",
                new { id = body.GetProperty("batchId").GetGuid().ToString() }));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "DROP TRIGGER test_refuse_command ON agent_commands; DROP FUNCTION test_refuse_command();");
        }
    }

    [Fact]
    public async Task A_replay_answers_the_stored_results_and_queues_nothing_new_another_body_is_409()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var idle = await Seats.OnlineAsync(Server);
        var (live, socket) = await LiveAsync();
        using (socket)
        {
            var key = Guid.NewGuid();
            var body = new { pcIds = new[] { live.PcId, idle.PcId }, kind = "reboot", text = "Обновление" };
            var call = RawAsync(Server, HttpMethod.Post, "/pcs/commands", owner, body, key);
            await AckAsync(socket, "reboot");
            var first = (await call).Body;
            Assert.Equal(["done", "queued"], first.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("outcome").GetString()));

            var (status, again, replayed) = await RawAsync(Server, HttpMethod.Post, "/pcs/commands", owner, body, key);
            Assert.Equal((200, true), (status, replayed));
            Assert.Equal(first.GetProperty("batchId").GetGuid(), again.GetProperty("batchId").GetGuid());
            Assert.Equal(["queued", "queued"], again.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("outcome").GetString()));
            Assert.Equal((1, 1), (await CommandsAsync(live), await CommandsAsync(idle)));

            Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/pcs/commands", owner, new { pcIds = new[] { idle.PcId }, kind = "reboot" }, key),
                "conflict", "idempotencyKeyReused");
        }
    }

    [Fact]
    public async Task Fields_and_the_key_are_checked()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var pc = await Seats.OnlineAsync(Server);
        var id = pc.PcId;
        foreach (var (body, field, reason) in new (object, string, string)[]
        {
            (new { kind = "lock" }, "pcIds", "required"), (new { pcIds = Array.Empty<Guid>(), kind = "lock" }, "pcIds", "required"),
            (new { pcIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()), kind = "lock" }, "pcIds", "max"),
            (new { pcIds = new[] { id, id }, kind = "lock" }, "pcIds", "duplicate"), (new { pcIds = new[] { id } }, "kind", "required"),
            (new { pcIds = new[] { id }, kind = "dance" }, "kind", "unknown"), (new { pcIds = new[] { id }, kind = "message" }, "text", "required"),
            (new { pcIds = new[] { id }, kind = "message", text = new string('t', 501) }, "text", "max"),
            (new { pcIds = new[] { id }, kind = "message", text = "Привет", level = "loud" }, "level", "enum"),
            (new { pcIds = new[] { id }, kind = "lock", includeBusy = true }, "includeBusy", "kind"),
            (new { pcIds = new[] { id }, kind = "reboot", sessionIds = new[] { Guid.NewGuid() } }, "sessionIds", "includeBusy"),
        })
        {
            Assert.Equal((field, reason), Bar.Details(await RawExpectAsync(Server, 400, HttpMethod.Post, "/pcs/commands", owner, body, Guid.NewGuid())));
        }

        Assert.Equal(("Idempotency-Key", "required"), Bar.Details(await RawExpectAsync(Server, 400, HttpMethod.Post, "/pcs/commands", owner, new { pcIds = new[] { id }, kind = "lock" })));
        Assert.Equal(0, await CommandsAsync(pc));
    }

    /// <summary>An online PC whose agent socket is connected.</summary>
    [Fact]
    public async Task Wake_powers_an_off_pc_on_through_a_pc_that_is_on_and_skips_one_that_is_on()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var off = await TestAgent.CreateAsync(Server, mac: "AA:BB:CC:00:11:22");
        var (relay, socket) = await LiveAsync();
        using (socket)
        {
            // Both in a network of their own: the relay of the off PC is the one on beside it, not any other PC of the club.
            var net = $"10.{Random.Shared.Next(100, 250)}.{Random.Shared.Next(0, 250)}";
            await Players.ScalarAsync<int>(Server, "UPDATE pcs SET ip_address = CASE WHEN id = @a THEN @ipA ELSE @ipB END WHERE id IN (@a, @b) RETURNING 1",
                new { a = off.PcId, b = relay.PcId, ipA = $"{net}.20", ipB = $"{net}.21" });
            var call = RawAsync(Server, HttpMethod.Post, "/pcs/commands", owner, new { pcIds = new[] { off.PcId, relay.PcId }, kind = "wake" }, Guid.NewGuid());
            var frame = await socket.ReceiveAsync();
            Assert.Equal(("command", "wake", "aa:bb:cc:00:11:22"),
                (frame.GetProperty("type").GetString(), frame.GetProperty("name").GetString(), frame.GetProperty("payload").GetProperty("targetMac").GetString()));
            await socket.SendAsync(new
            {
                type = "ack", id = Guid.NewGuid(), ts = DateTimeOffset.UtcNow, payload = (object?)null,
                ack = new { id = frame.GetProperty("id").GetGuid(), ok = true, result = (object?)null },
            });
            var (status, body, _) = await call;
            Assert.Equal(200, status);
            var results = body.GetProperty("results").EnumerateArray().ToList();
            var relayName = await Players.ScalarAsync<string>(Server, "SELECT name FROM pcs WHERE id = @PcId", new { relay.PcId });
            Assert.Equal(("done", relayName), (results[0].GetProperty("outcome").GetString(), results[0].GetProperty("via").GetString()));
            Assert.Equal(("skipped", "online"), (results[1].GetProperty("outcome").GetString(), results[1].GetProperty("skipped").GetString()));

            // The command is the relay's; the journal names the PC that was woken.
            Assert.Equal((0, 1), (await CommandsAsync(off), await CommandsAsync(relay)));
            Assert.Equal(1, await Players.ScalarAsync<int>(Server,
                "SELECT count(*)::int FROM audit_entries WHERE action = 'pcCommand' AND pc_id = @PcId AND meta ->> 'kind' = 'wake'", new { off.PcId }));
        }
    }

    [Fact]
    public void A_wake_relay_is_on_in_the_targets_network_and_the_least_used()
    {
        PcRow Pc(int n, string ip) => new() { Id = Guid.NewGuid(), Number = n, Name = $"PC-{n}", IpAddress = ip };
        var target = Pc(1, "192.168.1.20");
        var far = Pc(2, "192.168.7.5");
        var near = Pc(3, "192.168.1.30");
        var near2 = Pc(4, "192.168.1.31");
        var used = new Dictionary<Guid, int> { [near.Id] = 2 };
        Assert.Same(near2, ClubShell.Server.Admin.CounterEndpoints.WakeRelay(target, [far, near, near2], used));
        Assert.Same(far, ClubShell.Server.Admin.CounterEndpoints.WakeRelay(target, [far], used));
        Assert.Null(ClubShell.Server.Admin.CounterEndpoints.WakeRelay(target, [target], used));
    }

    private async Task<(TestAgent Agent, WsTestSocket Socket)> LiveAsync()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var socket = await WsTestSocket.ConnectAsync((KestrelServerFixture)Server, agent.AccessToken);
        await Wait.UntilAsync(() => Server.Services.GetRequiredService<AgentSocketHub>().IsConnected(agent.PcId));
        return (agent, socket);
    }

    /// <summary>The PC receives <paramref name="name"/> and acknowledges it over the socket.</summary>
    private static async Task AckAsync(WsTestSocket socket, string name)
    {
        var frame = await socket.ReceiveAsync();
        Assert.Equal(("command", name), (frame.GetProperty("type").GetString(), frame.GetProperty("name").GetString()));
        await socket.SendAsync(new
        {
            type = "ack", id = Guid.NewGuid(), ts = DateTimeOffset.UtcNow, payload = (object?)null,
            ack = new { id = frame.GetProperty("id").GetGuid(), ok = true, result = (object?)null },
        });
    }

    private Task<int> CommandsAsync(TestAgent pc) =>
        Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM agent_commands WHERE pc_id = @PcId", new { pc.PcId });
}
