using System.Security.Cryptography;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Http;
using ClubShell.Server.Agents;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>A server that refuses a re-registration without proof (<c>Agents:RegisterProof=enforce</c>, D-78).</summary>
public sealed class EnforceServerFixture : ServerFixture
{
    public EnforceServerFixture() => Settings["Agents:RegisterProof"] = "enforce";
}

/// <summary><see cref="EnforceServerFixture"/> on a real socket, for the agent's live connection.</summary>
public sealed class EnforceKestrelServerFixture : KestrelServerFixture
{
    public EnforceKestrelServerFixture() => Settings["Agents:RegisterProof"] = "enforce";
}

/// <summary>
/// Re-registration proof (D-78): an approved PC that holds credentials gets new ones on the club key and its HWID only by
/// signing the registration with its current secret, which it then keeps. Enforce writes nothing without that proof unless
/// the owner's <c>maintenance:true</c> opened the re-enroll window and the PC is offline; audit (the default) and off
/// re-issue as before, audit with a warning.
/// </summary>
public sealed class RegisterProofTests(ServerFixture audit, EnforceServerFixture enforce)
    : IClassFixture<ServerFixture>, IClassFixture<EnforceServerFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_valid_proof_keeps_the_pc_and_its_secret_and_revokes_the_old_tokens(bool enforced)
    {
        var server = enforced ? enforce : audit;
        var agent = await TestAgent.CreateAsync(server);
        using var response = await agent.ReRegisterAsync();
        var again = new TestAgent(server, await Players.ReadAsync(response, 200), agent.Hwid);
        Assert.Equal(agent.PcId, again.PcId);
        Assert.Equal(agent.Secret, again.Secret);

        using (var stale = await agent.SendAsync(HttpMethod.Get, agent.Path("config")))
        {
            await Contract.ReadErrorAsync(stale, 401, "unauthorized", "revoked");
        }

        using (var staleRefresh = await agent.RefreshAsync())
        {
            await Contract.ReadErrorAsync(staleRefresh, 401, "unauthorized", "revoked");
        }

        using var fresh = await again.SendAsync(HttpMethod.Get, again.Path("config"));
        Assert.Equal(200, (int)fresh.StatusCode);
    }

    [Fact]
    public async Task Without_a_proof_enforce_refuses_and_writes_nothing()
    {
        var logs = LogRecorder.On(enforce);
        var agent = await TestAgent.CreateAsync(enforce);
        var before = await RowAsync(enforce, agent.PcId);
        using (var refused = await TestAgent.RegisterAsync(enforce, TestAgent.RegisterBody(agent.Hwid, TestAgent.RandomMac(), machineName: "INTRUDER", agentVersion: "9.9.9")))
        {
            var error = await Contract.ReadErrorAsync(refused, 403, "forbidden", "pendingApproval");
            Assert.Equal(agent.PcId, error.GetProperty("error").GetProperty("details").GetProperty("pcId").GetGuid());
        }

        // Inventory, agent version, cv, secret and refresh tokens as they were: the PC goes on with what it holds.
        Assert.Equal(before, await RowAsync(enforce, agent.PcId));
        Assert.Contains("refused without a valid proof (none)", Assert.Single(Warnings(logs, agent.PcId)), StringComparison.Ordinal);
        using (var config = await agent.SendAsync(HttpMethod.Get, agent.Path("config")))
        {
            Assert.Equal(200, (int)config.StatusCode);
        }

        using var refreshed = await agent.RefreshAsync();
        Assert.Equal(200, (int)refreshed.StatusCode);
    }

    [Theory]
    [InlineData("secret", 0, 403)]
    [InlineData("body", 0, 403)]
    [InlineData(null, -301, 403)]
    [InlineData(null, 301, 403)]
    [InlineData(null, -300, 200)]
    [InlineData(null, 300, 200)]
    public async Task Enforce_takes_only_a_proof_by_the_pcs_secret_over_this_body_within_300_seconds(string? forged, int offsetSec, int status)
    {
        var agent = await TestAgent.CreateAsync(enforce);
        var before = await RowAsync(enforce, agent.PcId);
        using var response = await TestAgent.RegisterSignedAsync(
            enforce,
            TestAgent.RegisterBody(agent.Hwid, TestAgent.RandomMac()),
            forged == "secret" ? RandomNumberGenerator.GetBytes(32) : agent.Secret,
            enforce.Clock.GetUtcNow().ToUnixTimeSeconds() + offsetSec,
            forged == "body" ? TestAgent.RegisterBody(agent.Hwid, TestAgent.RandomMac()) : null);
        if (status == 403)
        {
            await Contract.ReadErrorAsync(response, 403, "forbidden", "pendingApproval");
            Assert.Equal(before, await RowAsync(enforce, agent.PcId));
        }
        else
        {
            Assert.Equal(agent.Secret, new TestAgent(enforce, await Players.ReadAsync(response, 200), agent.Hwid).Secret);
        }
    }

    [Fact]
    public async Task Enforce_refuses_a_replayed_proof()
    {
        var agent = await TestAgent.CreateAsync(enforce);
        var body = TestAgent.RegisterBody(agent.Hwid, TestAgent.RandomMac());
        var timestamp = enforce.Clock.GetUtcNow().ToUnixTimeSeconds();
        using (var first = await TestAgent.RegisterSignedAsync(enforce, body, agent.Secret, timestamp))
        {
            await Players.ReadAsync(first, 200);
        }

        using var replayed = await TestAgent.RegisterSignedAsync(enforce, body, agent.Secret, timestamp);
        await Contract.ReadErrorAsync(replayed, 403, "forbidden", "pendingApproval");
    }

    [Fact]
    public async Task Audit_re_issues_without_a_proof_as_before_and_warns_without_the_headers()
    {
        var logs = LogRecorder.On(audit);
        var first = await TestAgent.CreateAsync(audit);
        var second = await TestAgent.CreateAsync(audit, first.Hwid);
        Assert.Equal(first.PcId, second.PcId);
        Assert.NotEqual(first.Secret, second.Secret);

        // A wrong proof is re-issued too and warned about; a valid one is not a warning.
        using (var wrong = await TestAgent.RegisterSignedAsync(audit, TestAgent.RegisterBody(first.Hwid, TestAgent.RandomMac()), RandomNumberGenerator.GetBytes(32)))
        {
            var third = new TestAgent(audit, await Players.ReadAsync(wrong, 200), first.Hwid);
            Assert.NotEqual(second.Secret, third.Secret);
            using var proven = await third.ReRegisterAsync();
            await Players.ReadAsync(proven, 200);
        }

        var warnings = Warnings(logs, first.PcId);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("re-registered without a valid proof (none)", warnings[0], StringComparison.Ordinal);
        Assert.Contains("re-registered without a valid proof (mismatch)", warnings[1], StringComparison.Ordinal);
        Assert.DoesNotMatch("[0-9a-f]{64}", string.Join('\n', About(logs, first.PcId)));
    }

    [Fact]
    public async Task Off_ignores_the_proof_as_before_D78()
    {
        var options = enforce.Services.GetRequiredService<AgentOptions>();
        var agent = await TestAgent.CreateAsync(enforce);
        options.RegisterProof = RegisterProofMode.Off;
        try
        {
            using var proven = await agent.ReRegisterAsync();
            Assert.NotEqual(agent.Secret, new TestAgent(enforce, await Players.ReadAsync(proven, 200), agent.Hwid).Secret);
            Assert.Equal(agent.PcId, (await TestAgent.CreateAsync(enforce, agent.Hwid)).PcId);
        }
        finally
        {
            options.RegisterProof = RegisterProofMode.Enforce;
        }
    }

    [Fact]
    public async Task The_first_credentials_after_approval_need_no_proof_and_the_next_registration_does()
    {
        // A new PC waits for the owner (D-7); approved, it has no secret yet to prove.
        var club = enforce.Services.GetRequiredService<ClubOptions>();
        var body = TestAgent.RegisterBody(TestAgent.RandomHwid(), TestAgent.RandomMac());
        Guid pcId;
        club.AutoApprovePcs = false;
        try
        {
            using var pending = await TestAgent.RegisterAsync(enforce, body);
            pcId = (await Contract.ReadErrorAsync(pending, 403, "forbidden", "pendingApproval")).GetProperty("error").GetProperty("details").GetProperty("pcId").GetGuid();
        }
        finally
        {
            club.AutoApprovePcs = true;
        }

        await ExpectAsync(enforce, 200, HttpMethod.Patch, $"/pcs/{pcId}", await LoginAsync(enforce, OwnerPin), new { maintenance = false });
        using (var first = await TestAgent.RegisterAsync(enforce, body))
        {
            Assert.Equal(pcId, (await Players.ReadAsync(first, 200)).GetProperty("pcId").GetGuid());
        }

        using var again = await TestAgent.RegisterAsync(enforce, body);
        await Contract.ReadErrorAsync(again, 403, "forbidden", "pendingApproval");
    }

    [Fact]
    public async Task The_owners_maintenance_lets_an_offline_pc_re_enroll_once()
    {
        // A reimaged PC: the same HWID and no tokens. It never sent a heartbeat and has no socket, so it is offline.
        var agent = await TestAgent.CreateAsync(enforce);
        await ExpectAsync(enforce, 200, HttpMethod.Patch, $"/pcs/{agent.PcId}", await LoginAsync(enforce, OwnerPin), new { maintenance = true });

        using (var reenrolled = await TestAgent.RegisterAsync(enforce, TestAgent.RegisterBody(agent.Hwid, TestAgent.RandomMac())))
        {
            var again = new TestAgent(enforce, await Players.ReadAsync(reenrolled, 200), agent.Hwid);
            Assert.Equal(agent.PcId, again.PcId);
            Assert.NotEqual(agent.Secret, again.Secret);
            Assert.Equal("maintenance", again.Registration.GetProperty("pc").GetProperty("status").GetString());
        }

        await AssertRefusedAsync(agent);
    }

    [Fact]
    public async Task The_window_needs_the_pc_offline_and_closes_after_its_time_or_with_maintenance_false()
    {
        var owner = await LoginAsync(enforce, OwnerPin);
        var cashier = await LoginAsync(enforce, CashierPin);

        // Online by its heartbeat: refused until 90 s of silence make it offline.
        var online = await TestAgent.CreateAsync(enforce);
        await Players.ReadAsync(await online.HeartbeatAsync(), 200);
        await ExpectAsync(enforce, 200, HttpMethod.Patch, $"/pcs/{online.PcId}", owner, new { maintenance = true });
        await AssertRefusedAsync(online);
        enforce.Clock.Advance(TimeSpan.FromSeconds(91));
        using (var offline = await TestAgent.RegisterAsync(enforce, TestAgent.RegisterBody(online.Hwid, TestAgent.RandomMac())))
        {
            await Players.ReadAsync(offline, 200);
        }

        // Agents:ReenrollWindowMin (60) after the owner's switch it is closed.
        var late = await TestAgent.CreateAsync(enforce);
        await ExpectAsync(enforce, 200, HttpMethod.Patch, $"/pcs/{late.PcId}", owner, new { maintenance = true });
        enforce.Clock.Advance(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(1));
        await AssertRefusedAsync(late);

        // maintenance:false closes it, and a cashier cannot open one.
        var closed = await TestAgent.CreateAsync(enforce);
        await ExpectAsync(enforce, 200, HttpMethod.Patch, $"/pcs/{closed.PcId}", owner, new { maintenance = true });
        await ExpectAsync(enforce, 200, HttpMethod.Patch, $"/pcs/{closed.PcId}", owner, new { maintenance = false });
        await AssertRefusedAsync(closed);
        Contract.AssertError(await ExpectAsync(enforce, 403, HttpMethod.Patch, $"/pcs/{closed.PcId}", cashier, new { maintenance = true }), "forbidden", "ownerOnly");
        await AssertRefusedAsync(closed);
    }

    [Fact]
    public async Task A_pc_without_persistent_storage_re_registers_without_a_proof_while_offline_and_only_an_info_is_logged()
    {
        // SHELL_CHANGES 25: a diskless seat boots a clean C: and has lost its secret at every boot.
        var logs = LogRecorder.On(enforce);
        var owner = await LoginAsync(enforce, OwnerPin);
        var agent = await TestAgent.CreateAsync(enforce);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await ExpectAsync(enforce, 200, HttpMethod.Patch, $"/pcs/{agent.PcId}", owner, new { stateless = true });
        var hall = await ExpectAsync(enforce, 200, HttpMethod.Get, "/pcs", owner);
        Assert.True(hall.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == agent.PcId).GetProperty("stateless").GetBoolean());

        // Still online by its heartbeat: an intruder with the club key and the HWID is refused like for any PC.
        await AssertRefusedAsync(agent);

        // Rebooted: offline after 90 s of silence, then the same PC with new credentials, and not put in maintenance
        // (the re-enroll window does that); its first heartbeat makes it free.
        enforce.Clock.Advance(TimeSpan.FromSeconds(91));
        using (var reboot = await TestAgent.RegisterAsync(enforce, TestAgent.RegisterBody(agent.Hwid, TestAgent.RandomMac())))
        {
            var again = new TestAgent(enforce, await Players.ReadAsync(reboot, 200), agent.Hwid);
            Assert.Equal(agent.PcId, again.PcId);
            Assert.NotEqual(agent.Secret, again.Secret);
            Assert.Equal("offline", again.Registration.GetProperty("pc").GetProperty("status").GetString());
            var beat = await Players.ReadAsync(await again.HeartbeatAsync(), 200);
            Assert.Equal("free", beat.GetProperty("pcStatus").GetString());
        }

        Assert.Contains(About(logs, agent.PcId, LogLevel.Information), m => m.Contains("without persistent storage re-registered", StringComparison.Ordinal));
        Assert.DoesNotContain(Warnings(logs, agent.PcId), m => m.Contains("re-registered without a valid proof", StringComparison.Ordinal));

        // Unmarked again: refused, and a cashier cannot mark it.
        enforce.Clock.Advance(TimeSpan.FromSeconds(91));
        await ExpectAsync(enforce, 200, HttpMethod.Patch, $"/pcs/{agent.PcId}", owner, new { stateless = false });
        await AssertRefusedAsync(agent);
        var cashier = await LoginAsync(enforce, CashierPin);
        Contract.AssertError(await ExpectAsync(enforce, 403, HttpMethod.Patch, $"/pcs/{agent.PcId}", cashier, new { stateless = true }), "forbidden", "ownerOnly");
    }

    [Fact]
    public async Task In_audit_a_pc_without_persistent_storage_re_registers_without_the_warning()
    {
        var logs = LogRecorder.On(audit);
        var agent = await TestAgent.CreateAsync(audit);
        await ExpectAsync(audit, 200, HttpMethod.Patch, $"/pcs/{agent.PcId}", await LoginAsync(audit, OwnerPin), new { stateless = true });
        var again = await TestAgent.CreateAsync(audit, agent.Hwid);
        Assert.Equal(agent.PcId, again.PcId);
        Assert.Empty(Warnings(logs, agent.PcId));
    }

    [Fact]
    public async Task After_a_reused_refresh_token_a_proven_registration_restores_the_pc()
    {
        // A lost refresh response: the agent comes back with the token the server already used, so every token of the PC
        // is revoked (cv + 1). The secret is untouched, and the registration that follows proves it.
        var agent = await TestAgent.CreateAsync(enforce);
        var original = agent.RefreshToken;
        using (var rotated = await agent.RefreshAsync())
        {
            Assert.Equal(200, (int)rotated.StatusCode);
        }

        using (var reused = await agent.RefreshAsync(original))
        {
            await Contract.ReadErrorAsync(reused, 401, "unauthorized", "reused");
        }

        using var response = await agent.ReRegisterAsync();
        var again = new TestAgent(enforce, await Players.ReadAsync(response, 200), agent.Hwid);
        Assert.Equal(agent.Secret, again.Secret);
        using var heartbeat = await again.HeartbeatAsync();
        Assert.Equal(200, (int)heartbeat.StatusCode);
    }

    private async Task AssertRefusedAsync(TestAgent agent)
    {
        using var response = await TestAgent.RegisterAsync(enforce, TestAgent.RegisterBody(agent.Hwid, TestAgent.RandomMac()));
        await Contract.ReadErrorAsync(response, 403, "forbidden", "pendingApproval");
    }

    /// <summary>The PC's warnings among the recorded lines, in order.</summary>
    private static List<string> Warnings(LogRecorder logs, Guid pcId) => [.. About(logs, pcId, LogLevel.Warning)];

    /// <summary>The recorded messages that name the PC (of one level, or all).</summary>
    private static IEnumerable<string> About(LogRecorder logs, Guid pcId, LogLevel? level = null) => logs.Lines
        .Where(l => level is null || l.Level == level)
        .Select(l => l.Message)
        .Where(m => m.Contains(pcId.ToString(), StringComparison.Ordinal));

    /// <summary>Everything a registration may write to the PC's row, and its live refresh tokens, as one hash.</summary>
    private static Task<string> RowAsync(ServerFixture server, Guid pcId) => Players.ScalarAsync<string>(
        server,
        """
        SELECT md5(concat_ws('|', machine_name, mac_address, ip_address, hardware::text, agent_version, credentials_version,
                             encode(signing_secret, 'hex'), updated_at, reenroll_until,
                             (SELECT string_agg(encode(token_hash, 'hex'), ',' ORDER BY token_hash)
                              FROM agent_refresh_tokens WHERE pc_id = pcs.id AND used_at IS NULL)))
        FROM pcs WHERE id = @pcId
        """,
        new { pcId });
}

/// <summary>
/// D-78 through the real agent client (DESIGN §10.b): the start-up path of <see cref="AgentCredentials"/> (refresh first,
/// register only when the refresh is rejected), the signed re-registration and the server clock learned from a refresh.
/// </summary>
public sealed class RegisterProofHarnessTests(ServerFixture audit, EnforceServerFixture enforce)
    : IClassFixture<ServerFixture>, IClassFixture<EnforceServerFixture>
{
    [Fact]
    public async Task A_client_without_stored_tokens_still_re_registers_in_audit()
    {
        // An agent before 1.0.18, or one whose data was purged, sends no proof: audit re-issues as before.
        await using var agent = await AgentHarness.CreateAsync(audit);
        var first = await agent.Client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None);
        await agent.Tokens.ClearAsync(CancellationToken.None);
        var second = await agent.Client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None);
        Assert.Equal(first.PcId, second.PcId);
        Assert.NotEqual(first.SigningSecret, second.SigningSecret);
    }

    [Fact]
    public async Task A_start_after_the_access_token_expired_refreshes_and_does_not_register()
    {
        await using var agent = await AgentHarness.CreateAsync(enforce);
        var registered = await agent.Client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None);
        var cv = await Players.ScalarAsync<int>(enforce, "SELECT credentials_version FROM pcs WHERE id = @PcId", new { registered.PcId });
        enforce.Clock.Advance(TimeSpan.FromMinutes(61));
        agent.Clock.Advance(TimeSpan.FromMinutes(61));

        var registeredAgain = false;
        await EnsureAsync(agent, _ =>
        {
            registeredAgain = true;
            return Task.CompletedTask;
        });

        Assert.False(registeredAgain);
        Assert.Equal(cv, await Players.ScalarAsync<int>(enforce, "SELECT credentials_version FROM pcs WHERE id = @PcId", new { registered.PcId }));
        await agent.Client.HeartbeatAsync(registered.PcId, AgentHarness.Heartbeat(), CancellationToken.None);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task A_lost_refresh_response_is_recovered_with_proof_in_enforce_even_with_the_pc_clock_hours_ahead(int hoursAhead)
    {
        await using var agent = await AgentHarness.CreateAsync(enforce);
        var registered = await agent.Client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None);

        // The server rotated the refresh token, but its answer never arrived: the agent still holds the used one.
        var held = agent.Tokens.Agent!;
        await agent.Client.RefreshAsync(CancellationToken.None);
        await agent.Tokens.SetAgentAsync(held, CancellationToken.None);

        // The next morning, on a PC whose clock may be off: the refresh is refused as reused, and its answer's
        // X-Server-Time puts the registration's proof back into the window.
        enforce.Clock.Advance(TimeSpan.FromMinutes(61));
        agent.Clock.Advance(TimeSpan.FromMinutes(61) + TimeSpan.FromHours(hoursAhead));
        await EnsureAsync(agent, ct => agent.Client.RegisterAsync(agent.RegisterRequest(), ct));

        Assert.Equal(registered.PcId, agent.Tokens.Agent!.PcId);
        Assert.Equal(registered.SigningSecret, agent.Tokens.Agent.SigningSecret);
        await agent.Client.HeartbeatAsync(registered.PcId, AgentHarness.Heartbeat(), CancellationToken.None);
    }

    /// <summary>The agent's start-up path; a failure would retry for ever, so it is cut short.</summary>
    private static async Task EnsureAsync(AgentHarness agent, Func<CancellationToken, Task> register)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await AgentCredentials.EnsureAsync(
            agent.Client, agent.Tokens, register, agent.Services.GetRequiredService<IClock>(), TimeSpan.FromMinutes(2), NullLogger.Instance, timeout.Token);
    }
}

/// <summary>A refused re-registration (D-78) leaves the real agent's live socket alone, even in the owner's window.</summary>
public sealed class RegisterProofSocketTests(EnforceKestrelServerFixture server) : IClassFixture<EnforceKestrelServerFixture>
{
    [Fact]
    public async Task A_refused_re_registration_leaves_the_live_socket_open_and_a_connected_pc_gets_no_window()
    {
        await using var agent = await AgentHarness.CreateAsync(server);
        var pcId = (await agent.Client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None)).PcId;
        var hub = server.Services.GetRequiredService<AgentSocketHub>();
        using var stop = new CancellationTokenSource();
        var run = agent.Realtime.RunAsync(null, stop.Token);
        await Wait.UntilAsync(() => hub.IsConnected(pcId));
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/pcs/{pcId}", await LoginAsync(server, OwnerPin), new { maintenance = true });

        // Someone with the club key and the HWID but not the secret, while the PC is connected.
        using (var refused = await TestAgent.RegisterAsync(server, TestAgent.RegisterBody(agent.Hwid, TestAgent.RandomMac())))
        {
            await Contract.ReadErrorAsync(refused, 403, "forbidden", "pendingApproval");
        }

        // Pings every second here: a few rounds later the socket is still the PC's.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.True(agent.Realtime.IsConnected);
        Assert.True(hub.IsConnected(pcId));
        await agent.Client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
