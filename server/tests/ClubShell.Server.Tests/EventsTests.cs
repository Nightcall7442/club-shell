using System.Text.Json;
using static ClubShell.Server.Tests.PurchaseRulesTests;

namespace ClubShell.Server.Tests;

/// <summary>
/// Late agent events (DESIGN §5.12): paused/resumed/extended/ended in the past change the clock and the money at their
/// time, a repeat is deduplicated by (type, at) and by the key, an event older than the last server transition is only
/// recorded, and the batch is validated with the contract's field names.
/// </summary>
public sealed class EventsTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task Pause_and_resume_in_the_past_are_not_billed()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        var t0 = Server.Clock.GetUtcNow();
        Server.Clock.Advance(TimeSpan.FromMinutes(30));

        await PostAsync(agent, id, Guid.NewGuid(), ("paused", t0.AddMinutes(10), null), ("resumed", t0.AddMinutes(20), null));
        var current = await CurrentAsync(agent);
        Assert.Equal(("active", 1200, 2400), (current.GetProperty("state").GetString(), current.GetProperty("secondsUsed").GetInt32(), current.GetProperty("secondsLeft").GetInt32()));
    }

    [Fact]
    public async Task Extended_offline_is_charged_at_the_club_price_even_into_overdraft()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 1_200_000);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        Server.Clock.Advance(TimeSpan.FromMinutes(5));

        await PostAsync(agent, id, Guid.NewGuid(), ("extended", Server.Clock.GetUtcNow().AddMinutes(-1), new { minutes = 30, cost = new { amount = 1, currency = "UZS" } }));
        var current = await CurrentAsync(agent);
        Assert.Equal(3300 + 1800, current.GetProperty("secondsLeft").GetInt32());
        Assert.Equal(-600_000, await Players.BalanceAsync(Server, player.Id));
    }

    [Fact]
    public async Task Ended_in_the_past_settles_at_that_time_and_later_events_are_only_recorded()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 20_000);
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        var id = created.GetProperty("id").GetGuid();
        var t0 = Server.Clock.GetUtcNow();
        Server.Clock.Advance(TimeSpan.FromMinutes(30));

        await PostAsync(agent, id, Guid.NewGuid(), ("ended", t0.AddMinutes(10), new { reason = "user" }), ("locked", t0.AddMinutes(11), null));
        using (var none = await agent.SendAsync(HttpMethod.Get, $"/api/v1/sessions/current?pcId={agent.PcId}"))
        {
            Assert.Equal(204, (int)none.StatusCode);
        }

        Assert.Equal(20_000 - 200_000, await Players.BalanceAsync(Server, player.Id)); // 10 min postpaid, overdraft
        Assert.Equal(t0.AddMinutes(10), await Players.ScalarAsync<DateTimeOffset>(Server, "SELECT ended_at FROM sessions WHERE id = @id", new { id }));
        Assert.False(await Players.ScalarAsync<bool>(Server, "SELECT applied FROM session_events WHERE session_id = @id AND type = 'locked'", new { id }));
    }

    [Fact]
    public async Task A_repeated_batch_changes_nothing_and_the_same_key_is_replayed()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 5_000_000);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        var at = Server.Clock.GetUtcNow();
        Server.Clock.Advance(TimeSpan.FromMinutes(1));
        var key = Guid.NewGuid();
        (string, DateTimeOffset, object?)[] batch = [("extended", at, new { minutes = 10, cost = new { amount = 200_000, currency = "UZS" } }), ("warning", at.AddSeconds(1), new { minutesLeft = 5 })];

        await PostAsync(agent, id, key, batch);
        var after = await Players.BalanceAsync(Server, player.Id);
        using (var replay = await agent.PostAsync($"/api/v1/sessions/{id}/events", Body(id, batch), key))
        {
            await Players.ReadAsync(replay, 204);
            Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        }

        await PostAsync(agent, id, Guid.NewGuid(), batch); // the agent re-sent it under a new key: deduplicated by (type, at)
        Assert.Equal(after, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(2, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM session_events WHERE session_id = @id", new { id }));
        var current = await CurrentAsync(agent);
        Assert.Equal(new[] { 5 }, current.GetProperty("warningsSent").EnumerateArray().Select(w => w.GetInt32()));
    }

    [Fact]
    public async Task An_event_older_than_the_last_transition_is_only_recorded()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        var t0 = Server.Clock.GetUtcNow();
        Server.Clock.Advance(TimeSpan.FromMinutes(10));
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/pause", null), 200);

        await PostAsync(agent, id, Guid.NewGuid(), ("resumed", t0.AddMinutes(5), null));
        Assert.Equal("paused", (await CurrentAsync(agent)).GetProperty("state").GetString());
        Assert.False(await Players.ScalarAsync<bool>(Server, "SELECT applied FROM session_events WHERE session_id = @id", new { id }));
    }

    /// <summary>The agent closed it: an <c>ended</c> older than the last server transition (agent clock behind) closes the session at that transition instead of leaving a zombie.</summary>
    [Fact]
    public async Task An_ended_older_than_the_last_transition_closes_the_session_at_it()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        Server.Clock.Advance(TimeSpan.FromMinutes(10));
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/pause", null), 200);
        var paused = Server.Clock.GetUtcNow();
        Server.Clock.Advance(TimeSpan.FromMinutes(5));

        await PostAsync(agent, id, Guid.NewGuid(), ("ended", paused.AddSeconds(-20), new { reason = "user" }));
        Assert.Equal(("ended", paused), (await Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id }),
            await Players.ScalarAsync<DateTimeOffset>(Server, "SELECT ended_at FROM sessions WHERE id = @id", new { id })));
    }

    /// <summary>minutes is bounded like the online extend (1..1440): 71 582 789 would charge ~1.4 trillion tiyin for 44 s (int overflow), 35 791 395 would fail the batch forever.</summary>
    [Fact]
    public async Task An_extended_event_outside_1_to_1440_minutes_is_only_recorded()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        var at = Server.Clock.GetUtcNow();
        Server.Clock.Advance(TimeSpan.FromMinutes(1));
        var cost = new { amount = 0, currency = "UZS" };

        await PostAsync(agent, id, Guid.NewGuid(), ("extended", at, new { minutes = 71_582_789, cost }), ("extended", at.AddSeconds(1), new { minutes = 35_791_395, cost }));
        Assert.Equal(8_800_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(3540, (await CurrentAsync(agent)).GetProperty("secondsLeft").GetInt32());
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM session_events WHERE session_id = @id AND applied", new { id }));
    }

    /// <summary>An online extend committed but its answer lost: the agent applies it locally and queues <c>extended</c>; the server does not charge it twice.</summary>
    [Fact]
    public async Task An_extended_event_repeating_an_online_extend_is_not_charged_twice()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/extend", new { minutes = 60 }, Guid.NewGuid()), 200);
        Assert.Equal(7_600_000, await Players.BalanceAsync(Server, player.Id));
        Server.Clock.Advance(TimeSpan.FromSeconds(30)); // the agent's retries ran out

        await PostAsync(agent, id, Guid.NewGuid(), ("extended", Server.Clock.GetUtcNow(), new { minutes = 60, cost = new { amount = 1_200_000, currency = "UZS" } }));
        Assert.Equal(7_600_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(7200 - 30, (await CurrentAsync(agent)).GetProperty("secondsLeft").GetInt32());
    }

    [Fact]
    public async Task Batch_is_validated_with_the_contract_field_names()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        var now = Server.Clock.GetUtcNow();
        foreach (var (body, field, reason) in new (object, string, string)[]
        {
            (new { }, "events", "required"),
            (new { events = Enumerable.Range(0, 101).Select(_ => new { sessionId = id, type = "warning", at = now }).ToArray() }, "events", "max"),
            (new { events = new object[] { 1 } }, "events[0]", "format"),
            (new { events = new[] { new { sessionId = id, type = "bogus", at = now } } }, "type", "enum"),
            (new { events = new[] { new { sessionId = id, type = "locked,unlocked", at = now } } }, "type", "enum"), // Enum.TryParse: Ended
            (new { events = new[] { new { sessionId = id, type = "paused ", at = now } } }, "type", "enum"),
            (new { events = new[] { new { sessionId = id, type = "Locked", at = now } } }, "type", "enum"),
            (new { events = new[] { new { sessionId = id, type = "locked", at = "" } } }, "at", "required"),
            (new { events = new[] { new { sessionId = id, type = "locked", at = "yesterday" } } }, "at", "format"),
        })
        {
            using var response = await agent.PostAsync($"/api/v1/sessions/{id}/events", body, Guid.NewGuid());
            var error = await Contract.ReadErrorAsync(response, 400, "validation");
            Assert.Equal((field, reason), (Details(error).GetProperty("field").GetString(), Details(error).GetProperty("reason").GetString()));
        }

        using var keyless = await agent.PostAsync($"/api/v1/sessions/{id}/events", Body(id, [("locked", now, null)]));
        await Contract.ReadErrorAsync(keyless, 400, "validation");
    }

    internal static object Body(Guid sessionId, params (string Type, DateTimeOffset At, object? Data)[] events) =>
        new { events = events.Select(e => new { sessionId, type = e.Type, at = e.At, data = e.Data }).ToArray() };

    internal static async Task PostAsync(TestAgent agent, Guid sessionId, Guid key, params (string Type, DateTimeOffset At, object? Data)[] events)
    {
        using var response = await agent.PostAsync($"/api/v1/sessions/{sessionId}/events", Body(sessionId, events), key);
        await Players.ReadAsync(response, 204);
    }

    private static async Task<JsonElement> CurrentAsync(TestAgent agent)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/sessions/current?pcId={agent.PcId}");
        return await Players.ReadAsync(response, 200);
    }
}
