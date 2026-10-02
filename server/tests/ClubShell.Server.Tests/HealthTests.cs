using System.Text.Json;
using ClubShell.Server.Admin;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// PC health (slice S5, DESIGN §8) on the fixture's clock with metrics seeded as the agent's telemetry stores them: the
/// <see cref="HealthWorker"/> opens one ticket per PC and problem with the <c>hardware</c> event, escalates a medium one in
/// place, takes a free PC with a serious problem out of service when <c>autoMaintenance</c> is on, and resolving that
/// ticket puts the PC back; a problem fixed less than 6 h ago is not reopened.
/// </summary>
public sealed class HealthTests(LongClockServerFixture server) : IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task Metrics_open_tickets_that_escalate_take_the_pc_out_and_resolving_puts_it_back()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        var worker = server.Services.GetRequiredService<HealthWorker>();
        await ExpectAsync(server, 200, HttpMethod.Patch, "/club", owner, new
        {
            webhooks = new[] { new { id = "hw", url = "https://hooks.example.com/hw", events = new[] { "hardware" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null } },
        });

        // A: a normal week, a normal day, then a GPU running hot now. B: offline, hotter today than its week, lost frames,
        // dropped off the network three times.
        var hot = await TestAgent.CreateAsync(server);
        var worn = await TestAgent.CreateAsync(server);
        var now = server.Clock.GetUtcNow();
        await SeedAsync(hot.PcId, now, week: (55, 60, 200), day: (56, 61, 198));
        await SeedAsync(worn.PcId, now, week: (50, 55, 200), day: (65, 56, 120));
        await SampleAsync(hot.PcId, now.AddMinutes(-5), 57, 82, null);
        for (var i = 1; i <= 3; i++)
        {
            await Players.ExecuteAsync(server,
                "INSERT INTO telemetry_events (club_id, pc_id, kind, at, received_at) SELECT club_id, id, 'pcOffline', @at, @at FROM pcs WHERE id = @PcId",
                new { worn.PcId, at = now.AddHours(-i) });
        }

        await Players.ReadAsync(await hot.HeartbeatAsync(), 200);
        await worker.RunOnceAsync();

        var report = await ExpectAsync(server, 200, HttpMethod.Get, "/health", cashier);
        var a = Pc(report, hot.PcId);
        Assert.Equal((85, 82, 60), (a.GetProperty("score").GetInt32(), a.GetProperty("live").GetProperty("gpu").GetInt32(), a.GetProperty("baseline").GetProperty("gpu").GetInt32()));
        Assert.Equal(24, a.GetProperty("hourly").GetProperty("cpu").GetArrayLength());
        var issue = Assert.Single(a.GetProperty("issues").EnumerateArray());
        Assert.Equal(("gpuHot", "medium", 82, 85), (issue.GetProperty("kind").GetString(), issue.GetProperty("severity").GetString(),
            issue.GetProperty("params").GetProperty("temp").GetInt32(), issue.GetProperty("params").GetProperty("limit").GetInt32()));
        var ticket = a.GetProperty("ticket");
        var ticketId = ticket.GetProperty("id").GetGuid();
        Assert.Equal(("open", "medium", false), (ticket.GetProperty("status").GetString(), ticket.GetProperty("severity").GetString(), ticket.GetProperty("autoMaintenance").GetBoolean()));

        var b = Pc(report, worn.PcId);
        Assert.Equal("offline", b.GetProperty("status").GetString());
        Assert.Equal(["cpuTrend", "fpsDrop", "unstable"], b.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(55, b.GetProperty("score").GetInt32());
        var fps = b.GetProperty("issues")[1].GetProperty("params");
        Assert.Equal((40, 120, 200), (fps.GetProperty("drop").GetInt32(), fps.GetProperty("today").GetInt32(), fps.GetProperty("before").GetInt32()));
        Assert.Equal(4, report.GetProperty("tickets").GetArrayLength());
        Assert.Equal(4, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM webhook_outbox WHERE event = 'hardware'"));

        // Another pass changes nothing; then the GPU gets worse: the same ticket becomes high.
        await worker.RunOnceAsync();
        Assert.Equal(4, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM health_tickets"));
        server.Clock.Advance(TimeSpan.FromMinutes(16));
        await SampleAsync(hot.PcId, server.Clock.GetUtcNow().AddMinutes(-1), 58, 95, null);
        await Players.ReadAsync(await hot.HeartbeatAsync(), 200);
        await worker.RunOnceAsync();
        ticket = Pc(await ExpectAsync(server, 200, HttpMethod.Get, "/health", cashier), hot.PcId).GetProperty("ticket");
        Assert.Equal((ticketId, "high", 95), (ticket.GetProperty("id").GetGuid(), ticket.GetProperty("severity").GetString(), ticket.GetProperty("params").GetProperty("temp").GetInt32()));

        // The map's wrench: the worst open ticket per PC reaches the cash desk overview.
        var marks = (await ExpectAsync(server, 200, HttpMethod.Get, "/overview", cashier)).GetProperty("repairs").EnumerateArray()
            .ToDictionary(m => m.GetProperty("pcId").GetGuid(), m => m.GetProperty("severity").GetString());
        Assert.Equal("high", marks[hot.PcId]);
        Assert.True(marks.ContainsKey(worn.PcId));

        // autoMaintenance: the free PC is taken out of service, and resolving its ticket puts it back.
        await ExpectAsync(server, 200, HttpMethod.Patch, "/health/settings", owner, new { autoMaintenance = true });
        await worker.RunOnceAsync();
        report = await ExpectAsync(server, 200, HttpMethod.Get, "/health", cashier);
        Assert.Equal(("maintenance", true), (Pc(report, hot.PcId).GetProperty("status").GetString(), Pc(report, hot.PcId).GetProperty("ticket").GetProperty("autoMaintenance").GetBoolean()));
        Assert.Equal("offline", Pc(report, worn.PcId).GetProperty("status").GetString()); // offline, not free: left alone

        var inWork = (await ExpectAsync(server, 200, HttpMethod.Patch, $"/health/tickets/{ticketId}", cashier, new { status = "inWork", note = "Чистка кулера" })).GetProperty("ticket");
        Assert.Equal(("inWork", "Чистка кулера", JsonValueKind.Null), (inWork.GetProperty("status").GetString(), inWork.GetProperty("note").GetString(), inWork.GetProperty("resolvedAt").ValueKind));
        Assert.True(await Players.ScalarAsync<bool>(server, "SELECT maintenance FROM pcs WHERE id = @PcId", new { hot.PcId }));
        var resolved = (await ExpectAsync(server, 200, HttpMethod.Patch, $"/health/tickets/{ticketId}", cashier, new { status = "resolved", note = (string?)null })).GetProperty("ticket");
        Assert.Equal(("resolved", "Чистка кулера"), (resolved.GetProperty("status").GetString(), resolved.GetProperty("note").GetString()));
        Assert.False(await Players.ScalarAsync<bool>(server, "SELECT maintenance FROM pcs WHERE id = @PcId", new { hot.PcId }));
        Assert.Equal("inWork,resolved", await Players.ScalarAsync<string>(server,
            "SELECT string_agg(meta ->> 'status', ',' ORDER BY meta ->> 'status') FROM audit_entries WHERE action = 'pcCommand' AND pc_id = @PcId AND meta ->> 'kind' = 'repair'",
            new { hot.PcId }));

        // Still hot, but fixed less than 6 h ago: no new ticket; after 6 h it is opened again.
        await worker.RunOnceAsync();
        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM health_tickets WHERE pc_id = @PcId AND status <> 'resolved'", new { hot.PcId }));
        server.Clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        await SampleAsync(hot.PcId, server.Clock.GetUtcNow().AddMinutes(-1), 58, 96, null);
        await Players.ReadAsync(await hot.HeartbeatAsync(), 200);
        await ExpectAsync(server, 200, HttpMethod.Patch, "/health/settings", owner, new { autoMaintenance = false });
        await worker.RunOnceAsync();
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM health_tickets WHERE pc_id = @PcId AND status = 'open'", new { hot.PcId }));
    }

    [Fact]
    public async Task One_pc_posting_absurd_samples_cannot_fail_the_report_or_the_worker_of_the_club()
    {
        var cashier = await LoginAsync(server, CashierPin);
        var worker = server.Services.GetRequiredService<HealthWorker>();
        var broken = await TestAgent.CreateAsync(server);
        var fine = await TestAgent.CreateAsync(server);
        var now = server.Clock.GetUtcNow();

        // Two samples of 1e308 each (the telemetry takes any double) overflow avg(); sane ones of the same PC and of another still count.
        foreach (var minutes in new[] { 5, 6 })
        {
            await Players.ExecuteAsync(server, "INSERT INTO pc_metrics (pc_id, at, data) VALUES (@pcId, @at, @data::jsonb)",
                new { pcId = broken.PcId, at = now.AddMinutes(-minutes), data = """{"cpuPct":10,"gpuPct":10,"ramUsedMb":4000,"temps":{"cpu":1e308,"gpu":1e308},"fps":1e308}""" });
        }

        await SampleAsync(broken.PcId, now.AddMinutes(-7), 61, 62, 100);
        await SampleAsync(fine.PcId, now.AddMinutes(-5), 41, 42, 90);

        var report = await ExpectAsync(server, 200, HttpMethod.Get, "/health", cashier);
        var live = Pc(report, broken.PcId).GetProperty("live");
        Assert.Equal((61, 62, 100), (live.GetProperty("cpu").GetInt32(), live.GetProperty("gpu").GetInt32(), live.GetProperty("fps").GetInt32()));
        Assert.Equal(41, Pc(report, fine.PcId).GetProperty("live").GetProperty("cpu").GetInt32());
        await worker.RunOnceAsync();
        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM health_tickets WHERE pc_id = @PcId", new { broken.PcId }));
    }

    [Fact]
    public async Task Ticket_updates_check_the_status_the_note_and_the_ticket()
    {
        var cashier = await LoginAsync(server, CashierPin);
        var id = Guid.NewGuid();
        foreach (var (body, field, reason) in new (object, string, string)[]
        {
            (new { status = "closed" }, "status", "unknown"),
            (new { note = "x" }, "status", "required"),
            (new { status = "open", note = new string('n', 501) }, "note", "max"),
        })
        {
            Assert.Equal((field, reason), StaffAdminTests.Details(await ExpectAsync(server, 400, HttpMethod.Patch, $"/health/tickets/{id}", cashier, body)));
        }

        foreach (var path in new[] { $"/health/tickets/{id}", "/health/tickets/not-a-uuid" })
        {
            var error = await ExpectAsync(server, 404, HttpMethod.Patch, path, cashier, new { status = "open" });
            Assert.Equal("ticket", error.GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        }
    }

    private static JsonElement Pc(JsonElement report, Guid pcId) => report.GetProperty("pcs").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == pcId);

    /// <summary>Hourly samples: <paramref name="week"/> from 160 h to 25 h ago, <paramref name="day"/> from 23 h to 1 h ago.</summary>
    private async Task SeedAsync(Guid pcId, DateTimeOffset now, (int Cpu, int Gpu, int Fps) week, (int Cpu, int Gpu, int Fps) day)
    {
        for (var h = 160; h >= 25; h--)
        {
            await SampleAsync(pcId, now.AddHours(-h), week.Cpu, week.Gpu, week.Fps);
        }

        for (var h = 23; h >= 1; h--)
        {
            await SampleAsync(pcId, now.AddHours(-h), day.Cpu, day.Gpu, day.Fps);
        }
    }

    private Task SampleAsync(Guid pcId, DateTimeOffset at, int cpu, int gpu, int? fps) =>
        Players.ExecuteAsync(server, "INSERT INTO pc_metrics (pc_id, at, data) VALUES (@pcId, @at, @data::jsonb)", new
        {
            pcId, at,
            data = JsonSerializer.Serialize(new { cpuPct = 10, gpuPct = 10, ramUsedMb = 4000, temps = new { cpu, gpu }, fps, netMbps = new { up = 1, down = 1 }, uptimeSec = 60, at }),
        });
}
