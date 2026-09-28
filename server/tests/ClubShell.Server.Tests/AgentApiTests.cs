using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>
/// Registration, refresh, heartbeat and telemetry (DESIGN §3.1–3.2, §11 S1; port of club-server <c>AgentApiTests</c>).
/// Every response passes the contract check of <see cref="ServerFixture.Http"/>.
/// </summary>
public sealed class AgentApiTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Register_issues_credentials_and_the_pc_with_its_hwid()
    {
        var agent = await TestAgent.CreateAsync(server);
        var pc = agent.Registration.GetProperty("pc");
        Assert.Equal(agent.PcId, pc.GetProperty("id").GetGuid());
        Assert.Equal(agent.Hwid, pc.GetProperty("hwid").GetString());
        Assert.Equal(32, agent.Secret.Length);
        Assert.Equal("default", agent.Registration.GetProperty("config").GetProperty("shell").GetProperty("theme").GetString());

        using var heartbeat = await agent.SendAsync(HttpMethod.Post, agent.Path("heartbeat"), TestAgent.Heartbeat());
        Assert.Equal(200, (int)heartbeat.StatusCode);
    }

    [Theory]
    [InlineData("hwid")]
    [InlineData("macAddress")]
    [InlineData("hardware")]
    public async Task Register_requires_its_fields(string field)
    {
        var body = JsonElement.Parse(TestAgent.RegisterBody(TestAgent.RandomHwid(), TestAgent.RandomMac()));
        var without = JsonSerializer.Serialize(body.EnumerateObject().Where(p => p.Name != field).ToDictionary(p => p.Name, p => p.Value));
        using var response = await TestAgent.RegisterAsync(server, without);
        var error = await Contract.ReadErrorAsync(response, 400, "validation");
        Assert.Equal(field, error.GetProperty("error").GetProperty("details").GetProperty("field").GetString());
    }

    [Fact]
    public async Task Unknown_hwid_with_the_mac_of_a_live_pc_is_pending_with_its_seat()
    {
        // Disk replacement (DESIGN §9): the HWID changed, the MAC did not. The new PC gets the old seat but waits for the
        // owner even though the fixture auto-approves; no silent rebinding by MAC.
        var mac = TestAgent.RandomMac();
        var old = await TestAgent.CreateAsync(server, mac: mac);
        using var response = await TestAgent.RegisterAsync(server, TestAgent.RegisterBody(TestAgent.RandomHwid(), mac.ToLowerInvariant().Replace(':', '-'), old.PcId));
        var error = await Contract.ReadErrorAsync(response, 403, "forbidden", "pendingApproval");
        var pending = error.GetProperty("error").GetProperty("details").GetProperty("pcId").GetGuid();
        Assert.NotEqual(old.PcId, pending);

        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var rows = (await c.QueryAsync<(Guid Id, int Number, bool Approved, bool Maintenance, byte[]? Secret)>(
            "SELECT id, number, approved, maintenance, signing_secret FROM pcs WHERE id = ANY(@ids)", new { ids = new[] { old.PcId, pending } })).ToDictionary(r => r.Id);
        Assert.Equal(rows[old.PcId].Number, rows[pending].Number);
        Assert.False(rows[pending].Approved);
        Assert.True(rows[pending].Maintenance);
        Assert.Null(rows[pending].Secret);

        // A repeat keeps answering pendingApproval with the same PC.
        using var again = await TestAgent.RegisterAsync(server, TestAgent.RegisterBody(await c.QuerySingleAsync<string>("SELECT hwid FROM pcs WHERE id = @pending", new { pending }), mac));
        var repeat = await Contract.ReadErrorAsync(again, 403, "forbidden", "pendingApproval");
        Assert.Equal(pending, repeat.GetProperty("error").GetProperty("details").GetProperty("pcId").GetGuid());
    }

    [Fact]
    public async Task Same_hwid_keeps_the_pc_and_revokes_the_previous_credentials()
    {
        var first = await TestAgent.CreateAsync(server);
        var second = await TestAgent.CreateAsync(server, first.Hwid);
        Assert.Equal(first.PcId, second.PcId);
        Assert.NotEqual(first.Secret, second.Secret);

        using (var stale = await first.SendAsync(HttpMethod.Get, first.Path("config")))
        {
            await Contract.ReadErrorAsync(stale, 401, "unauthorized", "revoked");
        }

        using (var staleRefresh = await first.RefreshAsync())
        {
            await Contract.ReadErrorAsync(staleRefresh, 401, "unauthorized", "revoked");
        }

        using var fresh = await second.SendAsync(HttpMethod.Get, second.Path("config"));
        Assert.Equal(200, (int)fresh.StatusCode);
    }

    [Fact]
    public async Task Hwid_of_another_club_is_409()
    {
        var hwid = TestAgent.RandomHwid();
        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync(
                """
                WITH club AS (
                    INSERT INTO clubs (id, network_id, name) SELECT gen_random_uuid(), network_id, 'Other' FROM clubs LIMIT 1 RETURNING id)
                INSERT INTO pcs (id, club_id, number, name, hwid) SELECT gen_random_uuid(), id, 1, 'PC-01', @hwid FROM club
                """,
                new { hwid });
        }

        using var response = await TestAgent.RegisterAsync(server, TestAgent.RegisterBody(hwid, TestAgent.RandomMac()));
        await Contract.ReadErrorAsync(response, 409, "conflict");
    }

    [Fact]
    public async Task Refresh_rotates_once_and_reuse_revokes_everything()
    {
        var agent = await TestAgent.CreateAsync(server);
        var original = agent.RefreshToken;

        using var rotated = await agent.RefreshAsync();
        var body = await rotated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(200, (int)rotated.StatusCode);
        Assert.False(body.TryGetProperty("signingSecret", out _));
        var next = body.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(original, next);
        agent.AccessToken = body.GetProperty("accessToken").GetString()!;
        using (var ok = await agent.SendAsync(HttpMethod.Get, agent.Path("commands")))
        {
            Assert.Equal(200, (int)ok.StatusCode);
        }

        using (var reused = await agent.RefreshAsync(original))
        {
            await Contract.ReadErrorAsync(reused, 401, "unauthorized", "reused");
        }

        using (var afterReuse = await agent.RefreshAsync(next))
        {
            await Contract.ReadErrorAsync(afterReuse, 401, "unauthorized", "revoked");
        }

        using var access = await agent.SendAsync(HttpMethod.Get, agent.Path("commands"));
        await Contract.ReadErrorAsync(access, 401, "unauthorized", "revoked");
    }

    [Fact]
    public async Task Refresh_rejects_another_hwid_an_expired_token_and_a_deleted_pc()
    {
        var agent = await TestAgent.CreateAsync(server);
        using (var otherHwid = await agent.RefreshAsync(hwid: TestAgent.RandomHwid()))
        {
            await Contract.ReadErrorAsync(otherHwid, 401, "unauthorized", "revoked");
        }

        var expiring = await TestAgent.CreateAsync(server);
        server.Clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1));
        using (var expired = await expiring.RefreshAsync())
        {
            await Contract.ReadErrorAsync(expired, 401, "unauthorized", "expired");
        }

        var deleted = await TestAgent.CreateAsync(server);
        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE pcs SET deleted_at = now() WHERE id = @id", new { id = deleted.PcId });
        }

        using (var gone = await deleted.RefreshAsync())
        {
            await Contract.ReadErrorAsync(gone, 401, "unauthorized", "revoked");
        }

        using (var heartbeat = await deleted.SendAsync(HttpMethod.Post, deleted.Path("heartbeat"), TestAgent.Heartbeat()))
        {
            // Never 404 (DESIGN §3.1): the agent re-registers after the refresh is refused.
            await Contract.ReadErrorAsync(heartbeat, 401, "unauthorized", "revoked");
        }

        using var unknown = await agent.RefreshAsync(token: "never-issued");
        await Contract.ReadErrorAsync(unknown, 401, "unauthorized", "revoked");
    }

    [Theory]
    [InlineData("heartbeat", "POST")]
    [InlineData("telemetry", "POST")]
    [InlineData("config", "GET")]
    [InlineData("policies", "GET")]
    [InlineData("commands", "GET")]
    [InlineData("commands/0197a0b0-0000-7000-8000-000000000001/ack", "POST")]
    public async Task Another_pcs_path_is_403_pcMismatch(string rest, string method)
    {
        var agent = await TestAgent.CreateAsync(server);
        var other = await TestAgent.CreateAsync(server);
        var body = method == "POST" ? rest == "heartbeat" ? TestAgent.Heartbeat() : """{"samples":[],"events":[],"ok":true}""" : null;
        using var response = await agent.SendAsync(new HttpMethod(method), other.Path(rest), body);
        await Contract.ReadErrorAsync(response, 403, "forbidden", "pcMismatch");
    }

    [Fact]
    public async Task Heartbeat_is_stored_and_answers_the_versions()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var response = await agent.SendAsync(HttpMethod.Post, agent.Path("heartbeat"), TestAgent.Heartbeat());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("free", body.GetProperty("pcStatus").GetString());
        Assert.Equal(1, body.GetProperty("configVersion").GetInt32());
        Assert.Equal(1, body.GetProperty("policyVersion").GetInt32());
        Assert.Equal("1", body.GetProperty("catalogVersion").GetString());
        Assert.Equal(0, body.GetProperty("pendingCommands").GetInt32());

        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var (at, shell) = await c.QuerySingleAsync<(DateTimeOffset, string)>("SELECT last_heartbeat_at, last_heartbeat->>'shellVersion' FROM pcs WHERE id = @id", new { id = agent.PcId });
        Assert.Equal(server.Clock.GetUtcNow(), at);
        Assert.Equal("1.4.2", shell);

        using var invalid = await agent.SendAsync(HttpMethod.Post, agent.Path("heartbeat"), "{}");
        await Contract.ReadErrorAsync(invalid, 400, "validation");
    }

    [Fact]
    public async Task Telemetry_takes_120_samples_and_1024_events_and_keeps_callAdmin()
    {
        var agent = await TestAgent.CreateAsync(server);
        var now = server.Clock.GetUtcNow();
        var samples = Enumerable.Range(0, 120).Select(i => new
        {
            cpuPct = 12.5, gpuPct = 40.0, ramUsedMb = 8000, temps = new { cpu = 55.0, gpu = 60.0 }, fps = (double?)null,
            netMbps = new { up = 1.0, down = 20.0 }, uptimeSec = 3600L + i, at = now.AddSeconds(-i),
        }).ToList();
        var callAdmin = new { kind = "callAdmin", at = now, data = new { pcId = agent.PcId, userId = (Guid?)null, category = "other", message = "help", at = now } };
        var events = Enumerable.Range(0, 1023).Select(i => (object)new { kind = "shellCrash", at = now, data = new { attempt = i } }).Append(callAdmin).ToList();
        var hardware = JsonElement.Parse(TestAgent.RegisterBody("x", "aa:bb:cc:dd:ee:ff")).GetProperty("hardware");
        var batch = JsonSerializer.Serialize(new { samples, events, hardware, logsTail = new[] { "WRN something" } });

        using (var response = await agent.SendAsync(HttpMethod.Post, agent.Path("telemetry"), batch))
        {
            Assert.Equal(204, (int)response.StatusCode);
        }

        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            Assert.Equal(120, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM pc_metrics WHERE pc_id = @id", new { id = agent.PcId }));
            Assert.Equal(1024, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM telemetry_events WHERE pc_id = @id", new { id = agent.PcId }));
            Assert.Equal("help", await c.ExecuteScalarAsync<string>("SELECT data->>'message' FROM telemetry_events WHERE pc_id = @id AND kind = 'callAdmin'", new { id = agent.PcId }));
            Assert.Equal(16384, await c.ExecuteScalarAsync<int>("SELECT (hardware->>'ramMb')::int FROM pcs WHERE id = @id", new { id = agent.PcId }));
        }

        var tooMany = JsonSerializer.Serialize(new { samples = samples.Append(samples[0]), events = Array.Empty<object>() });
        using var rejected = await agent.SendAsync(HttpMethod.Post, agent.Path("telemetry"), tooMany);
        var error = await Contract.ReadErrorAsync(rejected, 400, "validation");
        Assert.Equal("samples", error.GetProperty("error").GetProperty("details").GetProperty("field").GetString());
    }
}
