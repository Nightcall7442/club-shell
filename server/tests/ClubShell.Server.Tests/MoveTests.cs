using System.Net.Http.Json;
using System.Text.Json;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>PCs and seated sessions of the move tests.</summary>
public static class Seats
{
    /// <summary>A heartbeat as agent 1.0.16 sends it: its open session (if any) and the size of its outbox.</summary>
    public static async Task BeatAsync(TestAgent agent, Guid? currentSessionId = null, int offlineQueue = 0)
    {
        var body = TestAgent.Heartbeat(offlineQueue)
            .Replace("\"status\":\"free\"", $"\"status\":\"free\",\"currentSessionId\":{(currentSessionId is { } id ? $"\"{id}\"" : "null")}", StringComparison.Ordinal);
        await Players.ReadAsync(await agent.SendAsync(HttpMethod.Post, agent.Path("heartbeat"), body), 200);
    }

    /// <summary>A registered PC that has sent a heartbeat (a registered PC without one is offline, as on the pilot).</summary>
    public static async Task<TestAgent> OnlineAsync(ServerFixture server, Guid? currentSessionId = null, int offlineQueue = 0)
    {
        var agent = await TestAgent.CreateAsync(server);
        await BeatAsync(agent, currentSessionId, offlineQueue);
        return agent;
    }

    /// <summary>A member signed in on a fresh online PC, seated there by the desk for 60 minutes of Standard (or postpaid).</summary>
    public static async Task<(TestAgent Pc, TestPlayer Player, Guid SessionId)> SeatedAsync(
        ServerFixture server, string token, bool prepaid = true, long balance = 10_000_000, Guid? tariff = null, TestAgent? pc = null)
    {
        pc ??= await OnlineAsync(server);
        var player = await Players.CreateAsync(server, balance);
        await pc.LoginAsync(player);
        var opened = await ExpectAsync(server, 201, HttpMethod.Post, "/sessions", token,
            new { pcId = pc.PcId, userId = player.Id, tariffId = tariff ?? Players.Standard, minutes = 60, prepaid });
        return (pc, player, opened.GetProperty("session").GetProperty("id").GetGuid());
    }

    public static Task<JsonElement> MoveAsync(
        ServerFixture server, string token, Guid from, Guid to, int status = 200, Guid? key = null, Guid? sessionId = null, Guid? tariffId = null) =>
        RawExpectAsync(server, status, HttpMethod.Post, "/sessions/move", token, new { fromPcId = from, toPcId = to, sessionId, tariffId }, key ?? Guid.NewGuid());

    public static Task<string> StateAsync(ServerFixture server, Guid sessionId) =>
        Players.ScalarAsync<string>(server, "SELECT state || ' ' || pc_id FROM sessions WHERE id = @sessionId", new { sessionId });
}

/// <summary>
/// «Пересадить» (cash desk part 3, D-59..D-61): the open session goes to another PC with its time and money — no ledger row,
/// the clock keeps running — only from the PC the desk saw it on, only to a live, online, free PC whose agent keeps no
/// session the server cannot see; prepaid keeps its tariff where it is valid, postpaid its frozen price. The old PC's late
/// end and events are answered from the move and never close the moved session; guests sign in on the new PC with «Гость».
/// </summary>
public sealed class MoveTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task Move_keeps_clock_money_warnings_and_frees_the_source()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var (a, player, id) = await Seats.SeatedAsync(Server, owner);
        var b = await Seats.OnlineAsync(Server);
        await Players.ExecuteAsync(Server, "UPDATE sessions SET warnings_sent = '{10}' WHERE id = @id", new { id });
        Server.Clock.Advance(TimeSpan.FromMinutes(5));
        await Seats.BeatAsync(b);
        var rows = await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id", new { player.Id });
        var balance = await Players.BalanceAsync(Server, player.Id);

        var key = Guid.NewGuid();
        var moved = await Seats.MoveAsync(Server, owner, a.PcId, b.PcId, key: key);
        var session = moved.GetProperty("session");
        Contract.AssertMatches("Session", session);
        Assert.Equal((id, b.PcId, "active", 3300, 1_200_000L, "[10]"), (session.GetProperty("id").GetGuid(), session.GetProperty("pcId").GetGuid(), session.GetProperty("state").GetString(),
            session.GetProperty("secondsLeft").GetInt32(), Amount(session.GetProperty("cost")), session.GetProperty("warningsSent").GetRawText()));
        Assert.Equal((a.PcId, b.PcId, false, player.Id, false), (moved.GetProperty("from").GetProperty("pcId").GetGuid(), moved.GetProperty("to").GetProperty("pcId").GetGuid(),
            moved.GetProperty("tariffChanged").GetBoolean(), moved.GetProperty("user").GetProperty("id").GetGuid(), moved.GetProperty("signedIn").GetBoolean()));
        var body = new { fromPcId = a.PcId, toPcId = b.PcId, sessionId = (Guid?)null, tariffId = (Guid?)null };
        var (status, replayed, wasReplayed) = await RawAsync(Server, HttpMethod.Post, "/sessions/move", owner, body, key);
        Assert.True(status == 200 && wasReplayed && JsonElement.DeepEquals(moved, replayed));

        // The same key with another body is refused (D-70): nothing is replayed or moved again.
        Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/sessions/move", owner, new { fromPcId = a.PcId, toPcId = b.PcId }, key),
            "conflict", "idempotencyKeyReused");

        // No money moved; the map shows the session on its new PC, waiting for the player to sign in there.
        Assert.Equal((rows, balance), (await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id", new { player.Id }),
            await Players.BalanceAsync(Server, player.Id)));
        var seats = (await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", owner)).GetProperty("seats").EnumerateArray().ToList();
        var seatA = seats.Single(s => s.GetProperty("pc").GetProperty("id").GetGuid() == a.PcId);
        var seatB = seats.Single(s => s.GetProperty("pc").GetProperty("id").GetGuid() == b.PcId);
        Assert.Equal(JsonValueKind.Null, seatA.GetProperty("session").ValueKind);
        Assert.Equal((id, false, player.Id), (seatB.GetProperty("session").GetProperty("id").GetGuid(), seatB.GetProperty("signedIn").GetBoolean(),
            seatB.GetProperty("user").GetProperty("id").GetGuid()));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE pc_id = @PcId", new { a.PcId }));
        Assert.Equal(a.PcId.ToString(), await Players.ScalarAsync<string>(Server,
            "SELECT data ->> 'fromPcId' FROM session_events WHERE session_id = @id AND source = 'staff' AND type = 'moved'", new { id }));

        var row = (await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?kinds=sessionMove", owner)).GetProperty("items")[0];
        Assert.Equal((a.PcId, b.PcId, player.Id, id, 0L), (row.GetProperty("fromPc").GetProperty("id").GetGuid(), row.GetProperty("pc").GetProperty("id").GetGuid(),
            row.GetProperty("client").GetProperty("id").GetGuid(), row.GetProperty("sessionId").GetGuid(), row.GetProperty("drawer").GetInt64()));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
    }

    [Fact]
    public async Task Prepaid_switches_tariff_only_when_the_zone_requires_and_postpaid_keeps_its_frozen_price()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        async Task<TestAgent> VipAsync()
        {
            var pc = await Seats.OnlineAsync(Server);
            await ExpectAsync(Server, 200, HttpMethod.Patch, $"/pcs/{pc.PcId}", owner, new { zone = "VIP" });
            return pc;
        }

        // Standard is sold in every zone: kept.
        var (a, _, standard) = await Seats.SeatedAsync(Server, owner);
        var vip = await VipAsync();
        Assert.False((await Seats.MoveAsync(Server, owner, a.PcId, vip.PcId)).GetProperty("tariffChanged").GetBoolean());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = standard });

        // VIP time leaves the VIP zone only with an hourly tariff of the target named.
        var (vipPc, _, id) = await Seats.SeatedAsync(Server, owner, tariff: Players.Vip, pc: await VipAsync());
        var std = await Seats.OnlineAsync(Server);
        var missing = await Seats.MoveAsync(Server, owner, vipPc.PcId, std.PcId, 409);
        Contract.AssertError(missing, "conflict", "tariffZone");
        Assert.Equal("", missing.GetProperty("error").GetProperty("details").GetProperty("zone").GetString());
        Assert.Equal("tariffZone", (await Seats.MoveAsync(Server, owner, vipPc.PcId, std.PcId, 403, tariffId: Players.Vip))
            .GetProperty("error").GetProperty("details").GetProperty("rule").GetString());
        Assert.Equal(("tariffId", "package"), Bar.Details(await Seats.MoveAsync(Server, owner, vipPc.PcId, std.PcId, 400, tariffId: Players.NightPack)));
        Assert.Equal("tariff", (await Seats.MoveAsync(Server, owner, vipPc.PcId, std.PcId, 404, tariffId: Guid.NewGuid()))
            .GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        var left = await Players.ScalarAsync<int>(Server, "SELECT purchased_sec FROM sessions WHERE id = @id", new { id });
        var moved = await Seats.MoveAsync(Server, owner, vipPc.PcId, std.PcId, tariffId: Players.Standard);
        Assert.Equal((true, Players.Standard), (moved.GetProperty("tariffChanged").GetBoolean(), moved.GetProperty("session").GetProperty("tariffId").GetGuid()));
        Assert.Equal(left, await Players.ScalarAsync<int>(Server, "SELECT purchased_sec FROM sessions WHERE id = @id", new { id }));
        Assert.Equal("VIP Standard", await Players.ScalarAsync<string>(Server,
            "SELECT (meta ->> 'tariffFrom') || ' ' || (meta ->> 'tariffTo') FROM audit_entries WHERE action = 'sessionMove' AND meta ->> 'sessionId' = @s", new { s = id.ToString() }));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });

        // Postpaid keeps its frozen price wherever it goes.
        var (post, _, postId) = await Seats.SeatedAsync(Server, owner, prepaid: false);
        var target = await VipAsync();
        Assert.Equal(("tariffId", "postpaid"), Bar.Details(await Seats.MoveAsync(Server, owner, post.PcId, target.PcId, 400, tariffId: Players.Vip)));
        var price = await Players.ScalarAsync<long>(Server, "SELECT price_per_hour_snapshot FROM sessions WHERE id = @postId", new { postId });
        await Seats.MoveAsync(Server, owner, post.PcId, target.PcId);
        Assert.Equal(price, await Players.ScalarAsync<long>(Server, "SELECT price_per_hour_snapshot FROM sessions WHERE id = @postId", new { postId }));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = postId });
    }

    [Fact]
    public async Task FromPcId_is_required_and_a_retry_to_another_target_is_409_sessionMoved()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var (a, _, id) = await Seats.SeatedAsync(Server, owner);
        var b = await Seats.OnlineAsync(Server);
        var c = await Seats.OnlineAsync(Server);
        foreach (var (body, field, reason) in new (object, string, string)[]
        {
            (new { toPcId = b.PcId }, "fromPcId", "required"), (new { fromPcId = a.PcId }, "toPcId", "required"),
            (new { fromPcId = "a", toPcId = b.PcId }, "fromPcId", "format"), (new { fromPcId = a.PcId, toPcId = a.PcId }, "toPcId", "same"),
        })
        {
            Assert.Equal((field, reason), Bar.Details(await RawExpectAsync(Server, 400, HttpMethod.Post, "/sessions/move", owner, body, Guid.NewGuid())));
        }

        Assert.Equal(("Idempotency-Key", "required"), Bar.Details(await RawExpectAsync(Server, 400, HttpMethod.Post, "/sessions/move", owner, new { fromPcId = a.PcId, toPcId = b.PcId })));
        Contract.AssertError(await Seats.MoveAsync(Server, await ApiKeyAsync(Server), a.PcId, b.PcId, 403), "forbidden", "staffOnly");
        await ClearApiKeyAsync(Server);
        Assert.Equal("pc", (await Seats.MoveAsync(Server, owner, a.PcId, Guid.NewGuid(), 404)).GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        Assert.Equal("session", (await Seats.MoveAsync(Server, owner, a.PcId, b.PcId, 404, sessionId: Guid.NewGuid()))
            .GetProperty("error").GetProperty("details").GetProperty("what").GetString());

        await Seats.MoveAsync(Server, owner, a.PcId, b.PcId, sessionId: id);
        Contract.AssertError(await Seats.MoveAsync(Server, owner, a.PcId, c.PcId, 409), "conflict", "sessionMoved");
        Contract.AssertError(await Seats.MoveAsync(Server, owner, a.PcId, c.PcId, 409, sessionId: id), "conflict", "sessionMoved");
        Assert.EndsWith(b.PcId.ToString(), await Seats.StateAsync(Server, id), StringComparison.Ordinal);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
    }

    [Fact]
    public async Task Refuses_a_busy_offline_maintenance_or_still_playing_target_and_allows_a_known_ended_session()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var (a, _, id) = await Seats.SeatedAsync(Server, owner);
        var (busy, _, busyId) = await Seats.SeatedAsync(Server, owner);
        var busyError = await Seats.MoveAsync(Server, owner, a.PcId, busy.PcId, 409);
        Contract.AssertError(busyError, "conflict", "pcBusy");
        Assert.Equal(busy.PcId, busyError.GetProperty("error").GetProperty("details").GetProperty("pcId").GetGuid());
        Contract.AssertError(await Seats.MoveAsync(Server, owner, a.PcId, (await TestAgent.CreateAsync(Server)).PcId, 409), "conflict", "targetOffline");
        var maintenance = await Seats.OnlineAsync(Server);
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/pcs/{maintenance.PcId}", owner, new { maintenance = true });
        Assert.Equal("pcMaintenance", (await Seats.MoveAsync(Server, owner, a.PcId, maintenance.PcId, 403)).GetProperty("error").GetProperty("details").GetProperty("rule").GetString());
        Contract.AssertError(await Seats.MoveAsync(Server, owner, a.PcId, (await Seats.OnlineAsync(Server, offlineQueue: 1)).PcId, 409), "conflict", "targetHasLocalSession");
        Contract.AssertError(await Seats.MoveAsync(Server, owner, a.PcId, (await Seats.OnlineAsync(Server, Guid.NewGuid())).PcId, 409), "conflict", "targetHasLocalSession");
        Assert.EndsWith(a.PcId.ToString(), await Seats.StateAsync(Server, id), StringComparison.Ordinal);

        // A PC still reporting a session the server knows and ended (a desk end 20 s ago) takes the move: «server wins».
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = busyId });
        await Seats.BeatAsync(busy, busyId);
        await Seats.MoveAsync(Server, owner, a.PcId, busy.PcId);
        Assert.Equal($"active {busy.PcId}", await Seats.StateAsync(Server, id));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
    }

    [Fact]
    public async Task Refuses_ending_resumes_paused_and_locked_becomes_active()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var (a, _, id) = await Seats.SeatedAsync(Server, owner);
        var b = await Seats.OnlineAsync(Server);
        await Players.ExecuteAsync(Server, "UPDATE sessions SET state = 'ending' WHERE id = @id", new { id });
        Contract.AssertError(await Seats.MoveAsync(Server, owner, a.PcId, b.PcId, 409), "conflict", "sessionEnding");
        await Players.ExecuteAsync(Server, "UPDATE sessions SET state = 'locked' WHERE id = @id", new { id });
        Assert.Equal("active", (await Seats.MoveAsync(Server, owner, a.PcId, b.PcId)).GetProperty("session").GetProperty("state").GetString());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });

        // Paused by the player: resumed on the new PC with the time it had.
        var (p, _, paused) = await Seats.SeatedAsync(Server, owner);
        Server.Clock.Advance(TimeSpan.FromMinutes(1));
        await Players.ReadAsync(await p.PostAsync($"/api/v1/sessions/{paused}/pause", new { }), 200);
        Server.Clock.Advance(TimeSpan.FromMinutes(3));
        var target = await Seats.OnlineAsync(Server);
        var resumed = (await Seats.MoveAsync(Server, owner, p.PcId, target.PcId)).GetProperty("session");
        Assert.Equal(("active", 3540), (resumed.GetProperty("state").GetString(), resumed.GetProperty("secondsLeft").GetInt32()));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = paused });

        // Paused postpaid whose next minute is not affordable: 402, nothing moved.
        var (post, _, postId) = await Seats.SeatedAsync(Server, owner, prepaid: false, balance: 20_000);
        Server.Clock.Advance(TimeSpan.FromSeconds(60));
        await Players.ReadAsync(await post.PostAsync($"/api/v1/sessions/{postId}/pause", new { }), 200);
        var other = await Seats.OnlineAsync(Server);
        Contract.AssertError(await Seats.MoveAsync(Server, owner, post.PcId, other.PcId, 402), "insufficientFunds");
        Assert.Equal($"paused {post.PcId}", await Seats.StateAsync(Server, postId));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = postId });
    }

    [Fact]
    public async Task The_old_pc_end_gets_the_ended_view_its_events_are_recorded_unapplied_and_its_pause_has_no_token()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var (a, player, id) = await Seats.SeatedAsync(Server, owner);
        var b = await Seats.OnlineAsync(Server);
        Server.Clock.Advance(TimeSpan.FromMinutes(2));
        await Seats.BeatAsync(b);
        await Seats.MoveAsync(Server, owner, a.PcId, b.PcId);
        var rows = await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id", new { player.Id });

        var end = await Contract.ReadErrorAsync(await a.PostAsync($"/api/v1/sessions/{id}/end", new { reason = "admin", secondsUsed = 120 }), 409, "sessionNotActive");
        var view = end.GetProperty("error").GetProperty("details").GetProperty("session");
        Contract.AssertMatches("Session", view);
        Assert.Equal((id, a.PcId, "ended", 0L, 120), (view.GetProperty("id").GetGuid(), view.GetProperty("pcId").GetGuid(), view.GetProperty("state").GetString(),
            Amount(view.GetProperty("cost")), view.GetProperty("secondsUsed").GetInt32()));
        await Contract.ReadErrorAsync(await a.PostAsync($"/api/v1/sessions/{id}/pause", new { }), 401, "unauthorized", "userToken");

        // Its queued events: 204 and recorded, never applied — an old «ended» does not close the session on its new PC.
        var key = Guid.NewGuid();
        var batch = new { events = new object[] { new { sessionId = id, type = "locked", at = Server.Clock.GetUtcNow().AddSeconds(-30) }, new { sessionId = id, type = "ended", at = Server.Clock.GetUtcNow(), data = new { reason = "user" } } } };
        using (var events = await a.PostAsync($"/api/v1/sessions/{id}/events", batch, key))
        {
            Assert.Equal(204, (int)events.StatusCode);
        }

        using (var again = await a.PostAsync($"/api/v1/sessions/{id}/events", batch, key))
        {
            Assert.Equal((204, "true"), ((int)again.StatusCode, again.Headers.GetValues("Idempotent-Replayed").Single()));
        }

        Assert.Equal($"active {b.PcId}", await Seats.StateAsync(Server, id));
        Assert.Equal(2, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM session_events WHERE session_id = @id AND source = 'agent' AND NOT applied", new { id }));
        Assert.Equal(rows, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id", new { player.Id }));

        // Another PC that never had it is still refused.
        var stranger = await Seats.OnlineAsync(Server);
        await Contract.ReadErrorAsync(await stranger.PostAsync($"/api/v1/sessions/{id}/end", new { reason = "admin", secondsUsed = 1 }), 403, "forbidden", "pcMismatch");
        await Contract.ReadErrorAsync(await stranger.PostAsync($"/api/v1/sessions/{id}/events", batch, Guid.NewGuid()), 403, "forbidden", "pcMismatch");

        // The member signs in on the new PC and gets the session; on the old one the server points to the new one.
        var login = await b.LoginAsync(player);
        Assert.Equal(id, login.GetProperty("session").GetProperty("id").GetGuid());
        using var raw = Server.CreateDefaultClient();
        using var elsewhere = await raw.SendAsync(a.Request(HttpMethod.Post, "/api/v1/auth/login",
            JsonSerializer.Serialize(new { kind = "password", username = player.Username, password = Players.Password, pcId = a.PcId, hwid = a.Hwid })));
        var refused = await elsewhere.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((409, "activeSessionElsewhere", b.PcId), ((int)elsewhere.StatusCode, refused.GetProperty("error").GetProperty("details").GetProperty("reason").GetString(),
            refused.GetProperty("error").GetProperty("details").GetProperty("pcId").GetGuid()));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
    }

    [Fact]
    public async Task Desk_and_kiosk_guests_sign_in_with_guest_on_the_new_pc()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var a = await Seats.OnlineAsync(Server);
        var seated = await DeskGuests.SeatAsync(Server, owner, a.PcId);
        var (guest, id) = (seated.GetProperty("user").GetProperty("id").GetGuid(), seated.GetProperty("session").GetProperty("id").GetGuid());
        var b = await Seats.OnlineAsync(Server);
        await Seats.MoveAsync(Server, owner, a.PcId, b.PcId);
        var signedIn = await GuestAsync(b);
        Assert.Equal((guest, id), (signedIn.GetProperty("user").GetProperty("id").GetGuid(), signedIn.GetProperty("session").GetProperty("id").GetGuid()));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });

        // A kiosk guest: «Гость» on the PC, topped up at the desk, bought at the kiosk.
        var kiosk = await Seats.OnlineAsync(Server);
        var kioskGuest = (await GuestAsync(kiosk)).GetProperty("user").GetProperty("id").GetGuid();
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = kioskGuest, amount = 2_000_000, method = "cash" });
        var (status, created) = await Players.StartAsync(kiosk, new TestPlayer(kioskGuest, ""));
        Assert.Equal(201, status);
        var kioskSession = created.GetProperty("id").GetGuid();
        var c = await Seats.OnlineAsync(Server);
        await Seats.MoveAsync(Server, owner, kiosk.PcId, c.PcId);
        var moved = await GuestAsync(c);
        Assert.Equal((kioskGuest, kioskSession), (moved.GetProperty("user").GetProperty("id").GetGuid(), moved.GetProperty("session").GetProperty("id").GetGuid()));

        // The kiosk guest's PC without the session is free again: «Гость» there makes a new guest.
        Assert.NotEqual(kioskGuest, (await GuestAsync(kiosk)).GetProperty("user").GetProperty("id").GetGuid());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = kioskSession });
    }

    [Fact]
    public async Task A_desk_end_racing_a_move_is_409_sessionMoved_or_ends_on_the_new_pc_and_sign_ins_do_not_deadlock()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        for (var i = 0; i < 4; i++)
        {
            var (a, _, id) = await Seats.SeatedAsync(Server, owner);
            var b = await Seats.OnlineAsync(Server);
            var results = await Task.WhenAll(
                RawAsync(Server, HttpMethod.Post, "/sessions/move", owner, new { fromPcId = a.PcId, toPcId = b.PcId }, Guid.NewGuid()),
                RawAsync(Server, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id }, Guid.NewGuid()));
            foreach (var (status, body, _) in results)
            {
                Assert.True(status == 200 || (status == 409 && body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString() == "sessionMoved"), body.ToString());
            }

            Assert.Contains(results, r => r.Status == 200);
            if (results[1].Status == 200)
            {
                Assert.StartsWith("ended", await Seats.StateAsync(Server, id), StringComparison.Ordinal);
            }
            else
            {
                await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
            }
        }

        for (var i = 0; i < 4; i++)
        {
            var (a, player, id) = await Seats.SeatedAsync(Server, owner);
            var b = await Seats.OnlineAsync(Server);
            var stranger = await Players.CreateAsync(Server);
            using var raw = Server.CreateDefaultClient();
            HttpRequestMessage Login(TestAgent pc, TestPlayer who) => pc.Request(HttpMethod.Post, "/api/v1/auth/login",
                JsonSerializer.Serialize(new { kind = "password", username = who.Username, password = Players.Password, pcId = pc.PcId, hwid = pc.Hwid }));
            var move = RawAsync(Server, HttpMethod.Post, "/sessions/move", owner, new { fromPcId = a.PcId, toPcId = b.PcId }, Guid.NewGuid());
            var onTarget = raw.SendAsync(Login(b, stranger));
            var onSource = raw.SendAsync(Login(a, player));
            await Task.WhenAll(move, onTarget, onSource);
            Assert.Equal(200, (await move).Status);
            Assert.Contains((int)(await onTarget).StatusCode, new[] { 200, 403 });
            Assert.Contains((int)(await onSource).StatusCode, new[] { 200, 409 });
            Assert.Equal($"active {b.PcId}", await Seats.StateAsync(Server, id));
            Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE pc_id = @PcId", new { b.PcId }));
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
        }
    }

    [Fact]
    public async Task The_tick_judges_the_target_heartbeat_and_a_postpaid_end_charges_with_the_new_pc()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var (a, _, id) = await Seats.SeatedAsync(Server, owner);
        var b = await Seats.OnlineAsync(Server);
        await Seats.MoveAsync(Server, owner, a.PcId, b.PcId);

        // An hour and a grace later only the new PC beats: the tick ends the session there.
        Server.Clock.Advance(TimeSpan.FromSeconds(3600 + 61));
        await Seats.BeatAsync(b, id);
        await Server.Services.GetRequiredService<SessionTickWorker>().RunOnceAsync();
        Assert.Equal($"ended {b.PcId}", await Seats.StateAsync(Server, id));
        Assert.Equal("timeUp", await Players.ScalarAsync<string>(Server, "SELECT end_reason FROM sessions WHERE id = @id", new { id }));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server,
            "SELECT count(*)::int FROM agent_commands WHERE pc_id = @PcId AND name = 'endSession' AND payload ->> 'sessionId' = @s", new { b.PcId, s = id.ToString() }));

        owner = await LoginAsync(Server, OwnerPin);
        var (post, _, postId) = await Seats.SeatedAsync(Server, owner, prepaid: false);
        var target = await Seats.OnlineAsync(Server);
        Server.Clock.Advance(TimeSpan.FromMinutes(3));
        await Seats.BeatAsync(target);
        await Seats.MoveAsync(Server, owner, post.PcId, target.PcId);
        Server.Clock.Advance(TimeSpan.FromMinutes(3));
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = postId });
        Assert.True(Amount(ended.GetProperty("charged")) > 0);
        Assert.Equal(target.PcId, await Players.ScalarAsync<Guid>(Server, "SELECT pc_id FROM ledger_entries WHERE session_id = @postId AND type = 'charge'", new { postId }));
    }

    /// <summary>«Гость» on <paramref name="pc"/> (200 expected); keeps the guest's token on the agent.</summary>
    private static async Task<JsonElement> GuestAsync(TestAgent pc)
    {
        using var response = await pc.PostAsync("/api/v1/auth/guest", new { pcId = pc.PcId, hwid = pc.Hwid });
        var body = await Players.ReadAsync(response, 200);
        pc.UserToken = body.GetProperty("accessToken").GetString();
        return body;
    }
}

/// <summary>
/// The pushes of a move (D-61) over <c>/ws/agent</c> and the real agent client: the target's other player is signed out
/// first (<c>seatTaken</c>), then the session goes to the target; the old PC gets the ended view and then <c>userRevoked
/// seatMoved</c>; the real <c>ServerClient</c>'s end of the moved session on the old PC is a clean end, and its events go through.
/// </summary>
public sealed class MovePushTests(KestrelServerFixture server) : LedgerCheckedTest(server), IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task Tokens_and_pushes_follow_the_move()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var (a, player, id) = await Seats.SeatedAsync(Server, owner);
        var b = await Seats.OnlineAsync(Server);
        var stranger = await Players.CreateAsync(Server);
        await b.LoginAsync(stranger);
        using var sa = await WsTestSocket.ConnectAsync((KestrelServerFixture)Server, a.AccessToken);
        using var sb = await WsTestSocket.ConnectAsync((KestrelServerFixture)Server, b.AccessToken);
        var hub = Server.Services.GetRequiredService<AgentSocketHub>();
        await Wait.UntilAsync(() => hub.IsConnected(a.PcId) && hub.IsConnected(b.PcId));

        await Seats.MoveAsync(Server, owner, a.PcId, b.PcId);
        var revoked = await PushAsync(sb, "userRevoked");
        Assert.Equal((stranger.Id, "seatTaken"), (revoked.GetProperty("userId").GetGuid(), revoked.GetProperty("reason").GetString()));
        var adopted = await PushAsync(sb, "sessionUpdated");
        Assert.Equal((id, b.PcId, "active"), (adopted.GetProperty("id").GetGuid(), adopted.GetProperty("pcId").GetGuid(), adopted.GetProperty("state").GetString()));
        var ended = await PushAsync(sa, "sessionUpdated");
        Assert.Equal((id, a.PcId, "ended", 0L), (ended.GetProperty("id").GetGuid(), ended.GetProperty("pcId").GetGuid(), ended.GetProperty("state").GetString(), Amount(ended.GetProperty("cost"))));
        var moved = await PushAsync(sa, "userRevoked");
        Assert.Equal((player.Id, "seatMoved"), (moved.GetProperty("userId").GetGuid(), moved.GetProperty("reason").GetString()));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM user_tokens WHERE pc_id IN (@a, @b)", new { a = a.PcId, b = b.PcId }));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
    }

    [Fact]
    public async Task The_real_agent_on_the_old_pc_ends_cleanly_and_its_events_go_through()
    {
        var ct = CancellationToken.None;
        await using var agent = await AgentHarness.CreateAsync(Server);
        var client = agent.Client;
        var pcId = (await client.RegisterAsync(agent.RegisterRequest(), ct)).PcId;
        var player = await Players.CreateAsync(Server, balance: 5_000_000);
        await client.LoginAsync(new Contracts.Users.AuthRequest(Contracts.Users.AuthKind.Password, player.Username, Players.Password, null, null, null, pcId, agent.Hwid), ct);
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var id = (await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 }))
            .GetProperty("session").GetProperty("id").GetGuid();
        var b = await Seats.OnlineAsync(Server);
        await Seats.MoveAsync(Server, owner, pcId, b.PcId);

        var result = await client.EndSessionAsync(id, new SessionEndReport(SessionEndReason.User, 30, agent.Clock.GetUtcNow()), ct);
        Assert.Equal((id, pcId, SessionState.Ended, Money.Zero), (result.Session.Id, result.Session.PcId, result.Session.State, result.Charged));
        await client.PostSessionEventsAsync(id, new SessionEventsBatch([new SessionEvent(id, SessionEventType.Ended, agent.Clock.GetUtcNow())]), Guid.NewGuid(), ct);
        Assert.Null(await client.GetCurrentSessionAsync(pcId, ct));
        Assert.Equal($"active {b.PcId}", await Seats.StateAsync(Server, id));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
    }

    private static async Task<JsonElement> PushAsync(WsTestSocket socket, string name)
    {
        var frame = await socket.ReceiveAsync();
        Assert.Equal(("push", name), (frame.GetProperty("type").GetString(), frame.GetProperty("name").GetString()));
        Contract.AssertMessage("push" + char.ToUpperInvariant(name[0]) + name[1..], frame);
        return frame.GetProperty("payload");
    }
}
