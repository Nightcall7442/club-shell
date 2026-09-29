using System.Text.Json;
using ClubShell.Server.Admin;
using ClubShell.Server.Agents;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The owner's "if → then" rules (slice S5, DESIGN §4.4, §8): <c>sessionStarted</c>/<c>visitCount</c> after a session opens
/// (the n-th visit counts the new one once), <c>topupAtLeast</c> after a top-up, <c>minutesLeft</c> from the club tick,
/// <c>pcIdleMinutes</c> from the PC status watcher; each fires once per target, recorded in <c>rule_firings</c>, so fresh
/// workers (a restart) do not fire it again; a <c>bonus</c> goes through the ledger; every firing counts
/// <c>fired</c>/<c>lastFiredAt</c> and raises <c>ruleFired</c>.
/// </summary>
public sealed class AutomationTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task Rules_fire_once_per_target_and_their_firings_survive_a_restart()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            automation = new object[]
            {
                Rule("start", "sessionStarted", null, new { kind = "lockPc" }),
                Rule("visit", "visitCount", 2, new { kind = "bonus", amount = 100_000 }),
                Rule("left", "minutesLeft", 5, new { kind = "message", text = "Осталось 5 минут" }),
                Rule("topup", "topupAtLeast", 1_000_000, new { kind = "bonus", amount = 50_000 }),
                Rule("idle", "pcIdleMinutes", 30, new { kind = "shutdownPc" }),
                Rule("off", "sessionStarted", null, new { kind = "notifyOwner", text = "никогда" }, enabled: false),
            },
            webhooks = new[] { new { id = "rf", url = "https://hooks.example.com/rf", events = new[] { "ruleFired" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null } },
        });
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);

        // Visit 1: sessionStarted locks the PC; visit 2: also the visitCount bonus.
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 30 });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId });
        Assert.Equal(0, await BonusesAsync(player.Id));
        var balance = await Players.BalanceAsync(Server, player.Id);
        var session = (await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 30 }))
            .GetProperty("session").GetProperty("id").GetGuid();
        Assert.Equal(100_000, await BonusesAsync(player.Id));
        Assert.Equal("Бонус: visit", await Players.ScalarAsync<string>(Server, "SELECT description FROM ledger_entries WHERE user_id = @Id AND type = 'bonus'", new { player.Id }));
        Assert.Equal(balance - 600_000 + 100_000, await Players.BalanceAsync(Server, player.Id));

        // minutesLeft: at 26 of 30 minutes the message goes out, once, also for a fresh tick worker and automation (a restart).
        Server.Clock.Advance(TimeSpan.FromMinutes(26));
        var tick = Server.Services.GetRequiredService<ClubTickWorker>();
        await tick.RunOnceAsync();
        await tick.RunOnceAsync();
        await ActivatorUtilities.CreateInstance<ClubTickWorker>(Server.Services, ActivatorUtilities.CreateInstance<AutomationService>(Server.Services)).RunOnceAsync();
        var commands = await CommandsAsync(agent);
        Assert.Equal(["lock", "endSession", "lock", "message"], commands.Select(c => c.GetProperty("name").GetString()));
        var message = commands[^1].GetProperty("payload");
        Assert.Equal(("Осталось 5 минут", false, "ClubShell"),
            (message.GetProperty("text").GetString(), message.GetProperty("requiresAck").GetBoolean(), message.GetProperty("from").GetString()));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM rule_firings WHERE rule_id = 'left' AND target_key = @key", new { key = $"session:{session}" }));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId });

        // topupAtLeast: once per top-up; the replay of the same key is not a new top-up.
        var key = Guid.NewGuid();
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = player.Id, amount = 2_000_000, method = "card" }, key);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = player.Id, amount = 2_000_000, method = "card" }, key);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = player.Id, amount = 500_000, method = "card" });
        Assert.Equal(150_000, await BonusesAsync(player.Id));

        // pcIdleMinutes: free for 30 minutes → shutdown {delaySec: 30}, once; a fresh watcher starts a new idle stretch.
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        var watcher = Server.Services.GetRequiredService<PcStatusWorker>();
        await watcher.RunOnceAsync();
        Server.Clock.Advance(TimeSpan.FromMinutes(31));
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();
        var fresh = ActivatorUtilities.CreateInstance<PcStatusWorker>(Server.Services);
        await fresh.RunOnceAsync();
        var shutdown = Assert.Single((await CommandsAsync(agent)).Where(c => c.GetProperty("name").GetString() == "shutdown"));
        Assert.Equal((30, false), (shutdown.GetProperty("payload").GetProperty("delaySec").GetInt32(), shutdown.GetProperty("payload").GetProperty("force").GetBoolean()));

        var rules = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("automation").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!, r => r.GetProperty("fired").GetInt32());
        Assert.Equal(new Dictionary<string, int> { ["start"] = 2, ["visit"] = 1, ["left"] = 1, ["topup"] = 1, ["idle"] = 1, ["off"] = 0 }, rules);
        Assert.Equal(6, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM webhook_outbox WHERE event = 'ruleFired'"));
        var fired = JsonElement.Parse(await Players.ScalarAsync<string>(Server,
            "SELECT payload::text FROM webhook_outbox WHERE event = 'ruleFired' AND payload -> 'data' ->> 'ruleId' = 'visit'"));
        Assert.Equal(player.Id, fired.GetProperty("data").GetProperty("userId").GetGuid());
        Contract.AssertMatches("AdminWebhookPayload", fired);
    }

    private static object Rule(string id, string trigger, long? value, object action, bool enabled = true) =>
        new { id, name = id, enabled, trigger = value is null ? (object)new { kind = trigger } : new { kind = trigger, value }, action };

    private Task<long> BonusesAsync(Guid userId) =>
        Players.ScalarAsync<long>(Server, "SELECT coalesce(sum(amount), 0)::bigint FROM ledger_entries WHERE user_id = @userId AND type = 'bonus'", new { userId });

    /// <summary>Every command queued for the PC, oldest first, as <c>{name, payload}</c> (the REST drain would drop expired ones).</summary>
    private async Task<List<JsonElement>> CommandsAsync(TestAgent agent) =>
        [.. JsonElement.Parse(await Players.ScalarAsync<string>(Server,
            "SELECT coalesce(jsonb_agg(jsonb_build_object('name', name, 'payload', payload) ORDER BY created_at, id), '[]')::text FROM agent_commands WHERE pc_id = @PcId",
            new { agent.PcId })).EnumerateArray()];
}
