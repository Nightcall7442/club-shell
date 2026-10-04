using System.Text.Json;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>Admin calls from a PC: the route (<c>callAdmin</c>) and the telemetry the agent falls back to.</summary>
public static class Calls
{
    public static object Body(TestAgent pc, DateTimeOffset at, string category = "help", string? message = null, Guid? userId = null, Guid? pcId = null) =>
        new { pcId = pcId ?? pc.PcId, userId, category, message, at };

    /// <summary><c>POST /support/call-admin</c> through the contract-validating client.</summary>
    public static async Task<JsonElement> CallAsync(TestAgent pc, object body, Guid? key = null, int status = 201)
    {
        using var response = await pc.PostAsync("/api/v1/support/call-admin", body, key ?? Guid.NewGuid());
        return await Players.ReadAsync(response, status);
    }

    public static object CallEvent(TestAgent pc, DateTimeOffset at, string category = "help", string? message = null, Guid? pcId = null, object? when = null) =>
        new { kind = "callAdmin", at, data = new { pcId = pcId ?? pc.PcId, userId = (Guid?)null, category, message, at = when ?? at } };

    public static object ReportEvent(DateTimeOffset at, string message) =>
        new { kind = "shellClientError", at, data = new { level = "warn", message, stack = (string?)null, route = "/support" } };

    public static object Sample(DateTimeOffset at) => new
    {
        cpuPct = 12.5, gpuPct = 40.0, ramUsedMb = 8000, temps = new { cpu = 55.0, gpu = 60.0 }, fps = (double?)null, netMbps = new { up = 1.0, down = 20.0 },
        uptimeSec = 3600L, at,
    };

    public static async Task TelemetryAsync(TestAgent pc, IEnumerable<object> events, IEnumerable<object>? samples = null)
    {
        using var response = await pc.SendAsync(HttpMethod.Post, pc.Path("telemetry"), JsonSerializer.Serialize(new { samples = samples ?? [], events }));
        Assert.Equal(204, (int)response.StatusCode);
    }

    public static Task<JsonElement> AckAsync(ServerFixture server, string token, Guid callId, bool? notify = null) =>
        RawExpectAsync(server, 200, HttpMethod.Post, $"/calls/{callId}/ack", token, new { notify });

    public static Task<string> StatusAsync(ServerFixture server, Guid callId) =>
        Players.ScalarAsync<string>(server, "SELECT status || ' ' || repeat FROM admin_calls WHERE id = @callId", new { callId });

    public static Task<int> CountAsync(ServerFixture server, Guid pcId, string? where = null) =>
        Players.ScalarAsync<int>(server, $"SELECT count(*)::int FROM admin_calls WHERE pc_id = @pcId {(where is null ? "" : "AND " + where)}", new { pcId });
}

/// <summary>
/// «Позвать администратора» (cash desk part 3, D-62, D-63): the route answers <c>201 {ticketId, createdAt, queuePosition}</c>
/// with the player it can trust; a call and its telemetry copy are one; telemetry calls and «report a problem» texts reach
/// the inbox, a bad one is skipped without failing the batch; a press soon after «Иду» does not ring again; «Иду» acks the
/// PC's older calls, «Закрыть» resolves them; calls nobody answered close after 12 h.
/// </summary>
public sealed class CallAdminTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Creates_201_with_queuePosition_and_the_player_it_can_trust()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var a = await TestAgent.CreateAsync(server);
        server.Clock.Advance(TimeSpan.FromSeconds(1));
        var open = await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM admin_calls WHERE status = 'open'");
        var now = server.Clock.GetUtcNow();
        var first = await Calls.CallAsync(a, Calls.Body(a, now, message: "  мышь не работает  "));
        Assert.Equal((open + 1, now), (first.GetProperty("queuePosition").GetInt32(), first.GetProperty("createdAt").GetDateTimeOffset()));
        var ticket = first.GetProperty("ticketId").GetGuid();
        Assert.Equal("help мышь не работает direct open", await Players.ScalarAsync<string>(server,
            "SELECT category || ' ' || message || ' ' || source || ' ' || status FROM admin_calls WHERE id = @ticket AND user_id IS NULL", new { ticket }));

        // The token's player.
        server.Clock.Advance(TimeSpan.FromSeconds(1));
        var (b, player) = await Players.SignedInAsync(server);
        var second = await Calls.CallAsync(b, Calls.Body(b, server.Clock.GetUtcNow(), "technical"));
        Assert.Equal(open + 2, second.GetProperty("queuePosition").GetInt32());
        Assert.Equal(player.Username, await Players.ScalarAsync<string>(server, "SELECT user_name FROM admin_calls WHERE id = @callId AND user_id = @Id",
            new { callId = second.GetProperty("ticketId").GetGuid(), player.Id }));

        // A body userId counts only for a player signed in on that PC.
        var c = await TestAgent.CreateAsync(server);
        var stranger = await Players.CreateAsync(server);
        var untrusted = await Calls.CallAsync(c, Calls.Body(c, server.Clock.GetUtcNow(), userId: stranger.Id));
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM admin_calls WHERE id = @id AND user_id IS NULL", new { id = untrusted.GetProperty("ticketId").GetGuid() }));
        await c.LoginAsync(stranger);
        c.UserToken = null;
        var trusted = await Calls.CallAsync(c, Calls.Body(c, server.Clock.GetUtcNow().AddSeconds(1), userId: stranger.Id));
        Assert.Equal(stranger.Id, await Players.ScalarAsync<Guid>(server, "SELECT user_id FROM admin_calls WHERE id = @id", new { id = trusted.GetProperty("ticketId").GetGuid() }));

        // No token, no body user: the player of the PC's open session.
        await OpenShiftAsync(server, owner);
        var d = await TestAgent.CreateAsync(server);
        var seated = await Players.CreateAsync(server);
        await ExpectAsync(server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = d.PcId, userId = seated.Id, tariffId = Players.Standard, minutes = 60 });
        var bySession = await Calls.CallAsync(d, Calls.Body(d, server.Clock.GetUtcNow(), "order"));
        Assert.Equal(seated.Id, await Players.ScalarAsync<Guid>(server, "SELECT user_id FROM admin_calls WHERE id = @id", new { id = bySession.GetProperty("ticketId").GetGuid() }));
        await ExpectAsync(server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = d.PcId });
    }

    [Fact]
    public async Task A_replay_answers_the_same_ticket_a_foreign_pc_is_403_and_fields_are_checked()
    {
        var a = await TestAgent.CreateAsync(server);
        var key = Guid.NewGuid();
        var body = Calls.Body(a, server.Clock.GetUtcNow(), "other", "Нет звука");
        var first = await Calls.CallAsync(a, body, key);
        using (var again = await a.PostAsync("/api/v1/support/call-admin", body, key))
        {
            Assert.True(JsonElement.DeepEquals(first, await Players.ReadAsync(again, 201)));
            Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        }

        // The same press again under a new key (the agent sends one per call): the same ticket.
        Assert.Equal(first.GetProperty("ticketId").GetGuid(), (await Calls.CallAsync(a, body)).GetProperty("ticketId").GetGuid());
        Assert.Equal(1, await Calls.CountAsync(server, a.PcId));

        var other = await TestAgent.CreateAsync(server);
        Contract.AssertError(await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow(), pcId: other.PcId), status: 403), "forbidden", "pcMismatch");
        foreach (var (call, field, reason) in new (object, string, string)[]
        {
            (Calls.Body(a, server.Clock.GetUtcNow(), "noise"), "category", "enum"),
            (Calls.Body(a, server.Clock.GetUtcNow(), message: new string('m', 501)), "message", "max"),
            (new { pcId = a.PcId, category = "help" }, "at", "required"),
            (new { pcId = a.PcId, category = "help", at = "yesterday" }, "at", "format"),
            (new { category = "help", at = server.Clock.GetUtcNow() }, "pcId", "required"),
        })
        {
            Assert.Equal((field, reason), Bar.Details(await Calls.CallAsync(a, call, status: 400)));
        }

        Assert.Equal(1, await Calls.CountAsync(server, a.PcId));
    }

    [Fact]
    public async Task A_call_and_its_telemetry_copy_are_one_and_an_old_telemetry_call_is_stored_resolved()
    {
        var a = await TestAgent.CreateAsync(server);
        var at = server.Clock.GetUtcNow().AddSeconds(-5);
        var direct = await Calls.CallAsync(a, Calls.Body(a, at, "technical"));
        await Calls.TelemetryAsync(a, [Calls.CallEvent(a, at, "technical")]);
        Assert.Equal(1, await Calls.CountAsync(server, a.PcId));
        Assert.Equal("direct", await Players.ScalarAsync<string>(server, "SELECT source FROM admin_calls WHERE id = @id", new { id = direct.GetProperty("ticketId").GetGuid() }));

        // Telemetry first (the route was unreachable), the route later with the same press: the same ticket.
        var later = at.AddSeconds(2);
        await Calls.TelemetryAsync(a, [Calls.CallEvent(a, later, "help", "Помогите")]);
        var stored = await Players.ScalarAsync<Guid>(server, "SELECT id FROM admin_calls WHERE pc_id = @PcId AND source = 'telemetry'", new { a.PcId });
        Assert.Equal(stored, (await Calls.CallAsync(a, Calls.Body(a, later, "help", "Помогите"))).GetProperty("ticketId").GetGuid());
        Assert.Equal(2, await Calls.CountAsync(server, a.PcId));

        // Delivered by telemetry more than 30 minutes late: in the history, not ringing.
        var stale = await TestAgent.CreateAsync(server);
        await Calls.TelemetryAsync(stale, [Calls.CallEvent(stale, server.Clock.GetUtcNow().AddMinutes(-31))]);
        Assert.Equal(1, await Calls.CountAsync(server, stale.PcId, "status = 'resolved' AND resolved_by_name = 'auto'"));
    }

    [Fact]
    public async Task A_user_report_becomes_a_problem_call_and_the_sixth_in_an_hour_is_dropped()
    {
        var a = await TestAgent.CreateAsync(server);
        var now = server.Clock.GetUtcNow();
        await Calls.TelemetryAsync(a, Enumerable.Range(0, 6).Select(i => Calls.ReportEvent(now.AddSeconds(-i), i == 0 ? "[user report] " + new string('x', 600) : "[user report] мышь не работает")));
        Assert.Equal(5, await Calls.CountAsync(server, a.PcId, "category = 'problem' AND source = 'report' AND status = 'open'"));
        Assert.Equal(500, await Players.ScalarAsync<int>(server, "SELECT max(length(message)) FROM admin_calls WHERE pc_id = @PcId", new { a.PcId }));
        Assert.Equal(4, await Calls.CountAsync(server, a.PcId, "message = 'мышь не работает'"));
        await Calls.TelemetryAsync(a, [Calls.ReportEvent(now.AddSeconds(5), "[user report] ещё одна")]);
        Assert.Equal(5, await Calls.CountAsync(server, a.PcId));
    }

    [Fact]
    public async Task Invalid_telemetry_calls_are_skipped_and_the_batch_still_commits()
    {
        var a = await TestAgent.CreateAsync(server);
        var other = await TestAgent.CreateAsync(server);
        var now = server.Clock.GetUtcNow();
        object[] events =
        [
            Calls.CallEvent(a, now.AddSeconds(-1), "noise"),
            Calls.CallEvent(a, now.AddSeconds(-2), message: new string('m', 600)),
            Calls.CallEvent(a, now.AddSeconds(-3), pcId: other.PcId),
            Calls.CallEvent(a, now.AddSeconds(-4), when: "soon"),
            Calls.ReportEvent(now.AddSeconds(-5), "[user report]     "),
            Calls.ReportEvent(now.AddSeconds(-6), "TypeError: x is undefined"),
            new { kind = "callAdmin", at = now, data = "help" },
            Calls.CallEvent(a, now.AddSeconds(-7), "technical", "Синий экран"),
        ];
        await Calls.TelemetryAsync(a, events, [Calls.Sample(now)]);
        Assert.Equal(1, await Calls.CountAsync(server, a.PcId));
        Assert.Equal(1, await Calls.CountAsync(server, a.PcId, "message = 'Синий экран'"));
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM pc_metrics WHERE pc_id = @PcId", new { a.PcId }));
        Assert.Equal(events.Length, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM telemetry_events WHERE pc_id = @PcId", new { a.PcId }));
    }

    [Fact]
    public async Task A_call_within_10_minutes_after_an_ack_is_stored_acked_as_a_repeat()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var a = await TestAgent.CreateAsync(server);
        var first = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        Assert.False((await Calls.AckAsync(server, owner, first)).GetProperty("notified").GetBoolean());

        server.Clock.Advance(TimeSpan.FromMinutes(2));
        var repeat = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        Assert.Equal("acked true", await Calls.StatusAsync(server, repeat));
        var listed = (await ExpectAsync(server, 200, HttpMethod.Get, "/overview", owner)).GetProperty("calls").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == repeat);
        Assert.Equal(("acked", true, "Владелец"), (listed.GetProperty("status").GetString(), listed.GetProperty("repeat").GetBoolean(), listed.GetProperty("ackedBy").GetString()));

        // Long after «Иду» a call rings again; a second press while it is open joins it.
        server.Clock.Advance(TimeSpan.FromMinutes(11));
        var fresh = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        Assert.Equal("open false", await Calls.StatusAsync(server, fresh));
        server.Clock.Advance(TimeSpan.FromSeconds(30));
        var again = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        Assert.Equal("open false", await Calls.StatusAsync(server, again));
    }

    [Fact]
    public async Task A_call_after_the_answered_call_was_closed_rings_again()
    {
        var cashier = await LoginAsync(server, CashierPin);
        var a = await TestAgent.CreateAsync(server);
        var first = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        await Calls.AckAsync(server, cashier, first);
        server.Clock.Advance(TimeSpan.FromMinutes(3));
        await RawExpectAsync(server, 200, HttpMethod.Post, $"/calls/{first}/resolve", cashier, new { });

        // Closed: nobody is on the way any more, so a new press two minutes later rings and is nobody's yet.
        server.Clock.Advance(TimeSpan.FromMinutes(2));
        var fresh = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        Assert.Equal("open false", await Calls.StatusAsync(server, fresh));
        Assert.Equal(0, await Calls.CountAsync(server, a.PcId, $"id = '{fresh}' AND (acked_at IS NOT NULL OR acked_by_name IS NOT NULL)"));
    }

    [Fact]
    public async Task Repeats_never_stretch_the_ten_minutes_after_the_ack()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var a = await TestAgent.CreateAsync(server);
        var first = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        await Calls.AckAsync(server, owner, first);

        // A press 8 minutes after «Иду» is a repeat that carries that «Иду», not a new one.
        server.Clock.Advance(TimeSpan.FromMinutes(8));
        var repeat = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        Assert.Equal("acked true", await Calls.StatusAsync(server, repeat));
        Assert.True(await Players.ScalarAsync<bool>(server,
            "SELECT r.acked_at = f.acked_at FROM admin_calls r, admin_calls f WHERE r.id = @repeat AND f.id = @first", new { repeat, first }));

        // 16 minutes after «Иду»: the ten minutes are over whatever was pressed in between — it rings.
        server.Clock.Advance(TimeSpan.FromMinutes(8));
        var late = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        Assert.Equal("open false", await Calls.StatusAsync(server, late));
    }

    [Fact]
    public async Task The_overview_lists_open_and_acked_calls_ack_covers_older_ones_and_resolve_hides_them()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        var a = await TestAgent.CreateAsync(server);
        var b = await TestAgent.CreateAsync(server);
        var c1 = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow(), "help"))).GetProperty("ticketId").GetGuid();
        server.Clock.Advance(TimeSpan.FromSeconds(1));
        var c2 = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow(), "order", "Кола не пришла"))).GetProperty("ticketId").GetGuid();
        server.Clock.Advance(TimeSpan.FromSeconds(1));
        var c3 = (await Calls.CallAsync(b, Calls.Body(b, server.Clock.GetUtcNow(), "technical"))).GetProperty("ticketId").GetGuid();

        var calls = (await ExpectAsync(server, 200, HttpMethod.Get, "/overview", cashier)).GetProperty("calls").EnumerateArray().ToList();
        var ours = calls.Where(c => c.GetProperty("id").GetGuid() is var id && (id == c1 || id == c2 || id == c3)).ToList();
        Assert.Equal([c3, c2, c1], ours.Select(c => c.GetProperty("id").GetGuid()));
        var listed = ours[1];
        Assert.Equal((a.PcId, "order", "Кола не пришла", "direct", "open", false, JsonValueKind.Null, JsonValueKind.Null), (listed.GetProperty("pcId").GetGuid(),
            listed.GetProperty("category").GetString(), listed.GetProperty("message").GetString(), listed.GetProperty("source").GetString(), listed.GetProperty("status").GetString(),
            listed.GetProperty("repeat").GetBoolean(), listed.GetProperty("user").ValueKind, listed.GetProperty("ackedBy").ValueKind));

        // «Иду» from the cashier: this call and the PC's older one; the PC has no socket, so the player was not told.
        var acked = await Calls.AckAsync(server, cashier, c2);
        Assert.Equal(("acked", "Кассир Азиз", false), (acked.GetProperty("call").GetProperty("status").GetString(), acked.GetProperty("call").GetProperty("ackedBy").GetString(),
            acked.GetProperty("notified").GetBoolean()));
        Assert.Equal(("acked false", "acked false", "open false"), (await Calls.StatusAsync(server, c1), await Calls.StatusAsync(server, c2), await Calls.StatusAsync(server, c3)));
        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM agent_commands WHERE pc_id = @PcId", new { a.PcId }));
        var again = await Calls.AckAsync(server, owner, c1);
        Assert.Equal(("acked", "Кассир Азиз", JsonValueKind.Null), (again.GetProperty("call").GetProperty("status").GetString(),
            again.GetProperty("call").GetProperty("ackedBy").GetString(), again.GetProperty("notified").ValueKind));
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM audit_entries WHERE action = 'callAck' AND meta ->> 'callId' = @id", new { id = c2.ToString() }));

        // «Закрыть»: gone from the inbox on every console; again — the same answer.
        Assert.Equal("resolved", (await RawExpectAsync(server, 200, HttpMethod.Post, $"/calls/{c2}/resolve", cashier, new { })).GetProperty("call").GetProperty("status").GetString());
        Assert.Equal(("resolved false", "resolved false"), (await Calls.StatusAsync(server, c1), await Calls.StatusAsync(server, c2)));
        var left = (await ExpectAsync(server, 200, HttpMethod.Get, "/overview", owner)).GetProperty("calls").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.True(left.Contains(c3) && !left.Contains(c1) && !left.Contains(c2));
        Assert.Equal("resolved", (await RawExpectAsync(server, 200, HttpMethod.Post, $"/calls/{c2}/resolve", owner, new { })).GetProperty("call").GetProperty("status").GetString());
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM audit_entries WHERE action = 'callResolve' AND meta ->> 'callId' = @id", new { id = c2.ToString() }));

        foreach (var id in new[] { Guid.NewGuid().ToString(), "call" })
        {
            Assert.Equal("call", (await RawExpectAsync(server, 404, HttpMethod.Post, $"/calls/{id}/ack", owner, new { })).GetProperty("error").GetProperty("details").GetProperty("what").GetString());
            await RawExpectAsync(server, 404, HttpMethod.Post, $"/calls/{id}/resolve", owner, new { });
        }
    }

    [Fact]
    public async Task Maintenance_resolves_calls_after_12_hours_and_deletes_them_after_30_days()
    {
        var a = await TestAgent.CreateAsync(server);
        var id = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        var worker = server.Services.GetRequiredService<MaintenanceWorker>();
        server.Clock.Advance(TimeSpan.FromHours(11));
        await worker.RunOnceAsync();
        Assert.Equal("open false", await Calls.StatusAsync(server, id));
        server.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        await worker.RunOnceAsync();
        Assert.Equal("resolved false auto", await Players.ScalarAsync<string>(server,
            "SELECT status || ' ' || repeat || ' ' || resolved_by_name FROM admin_calls WHERE id = @id", new { id }));
        server.Clock.Advance(TimeSpan.FromDays(30));
        await worker.RunOnceAsync();
        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM admin_calls WHERE id = @id", new { id }));
    }
}

/// <summary>
/// «Иду» to a connected PC (D-63): one non-blocking <c>message</c> «Администратор идёт к вам» in the caller's language that
/// expires in 2 minutes, sent once; with <c>notify: false</c> nothing is sent.
/// </summary>
public sealed class CallAdminPushTests(KestrelServerFixture server) : IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task Ack_of_a_connected_pc_queues_one_two_minute_message_in_the_callers_language()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var (a, player) = await Players.SignedInAsync(server);
        await Players.ExecuteAsync(server, "UPDATE users SET locale = 'uz' WHERE id = @Id", new { player.Id });
        using var socket = await WsTestSocket.ConnectAsync(server, a.AccessToken);
        await Wait.UntilAsync(() => server.Services.GetRequiredService<AgentSocketHub>().IsConnected(a.PcId));
        var older = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        server.Clock.Advance(TimeSpan.FromSeconds(1));
        var newer = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow(), "order"))).GetProperty("ticketId").GetGuid();

        var acked = await Calls.AckAsync(server, owner, newer);
        Assert.Equal((true, "acked", player.Id), (acked.GetProperty("notified").GetBoolean(), acked.GetProperty("call").GetProperty("status").GetString(),
            acked.GetProperty("call").GetProperty("user").GetProperty("id").GetGuid()));
        var frame = await socket.ReceiveAsync();
        Contract.AssertMessage("commandMessage", frame);
        Assert.Equal(("message", "Администратор", "Administrator yoningizga kelmoqda", false), (frame.GetProperty("name").GetString(),
            frame.GetProperty("payload").GetProperty("from").GetString(), frame.GetProperty("payload").GetProperty("text").GetString(),
            frame.GetProperty("payload").GetProperty("requiresAck").GetBoolean()));
        Assert.Equal(120, await Players.ScalarAsync<int>(server,
            "SELECT extract(epoch FROM expires_at - created_at)::int FROM agent_commands WHERE pc_id = @PcId AND name = 'message'", new { a.PcId }));
        Assert.Equal("acked false", await Calls.StatusAsync(server, older));

        // Again (another console, a stale list): nothing is sent, and the answer says this request answered nothing — not that
        // the PC is offline; a call acked without notify sends nothing either.
        Assert.Equal(JsonValueKind.Null, (await Calls.AckAsync(server, owner, newer)).GetProperty("notified").ValueKind);
        server.Clock.Advance(TimeSpan.FromMinutes(11));
        var quiet = (await Calls.CallAsync(a, Calls.Body(a, server.Clock.GetUtcNow()))).GetProperty("ticketId").GetGuid();
        Assert.False((await Calls.AckAsync(server, owner, quiet, notify: false)).GetProperty("notified").GetBoolean());
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM agent_commands WHERE pc_id = @PcId AND name = 'message'", new { a.PcId }));
    }
}
