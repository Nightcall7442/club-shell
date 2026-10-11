using System.Net.Http.Json;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Server.Admin;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Staff authentication (DESIGN §3.5, §3.6, S4): <c>invalidPin</c> and 429 after five wrong PINs, logout always 200 and
/// revoking, deactivation killing the tokens at once, <c>ck_</c> = owner, <c>ownerOnly</c> (also before a 501), sliding expiry.
/// </summary>
public sealed class StaffAuthTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Wrong_pin_is_401_invalidPin_and_five_in_the_window_make_it_429()
    {
        server.Clock.Advance(TimeSpan.FromMinutes(6)); // earlier failures of this fixture leave the window
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await server.Http.PostAsync("/api/v1/admin/login", JsonBody(new { pin = "9999" }));
            await Contract.ReadErrorAsync(wrong, 401, "unauthorized", "invalidPin");
        }

        // 429 on adminLogin is not in the vendored contract yet (OQ-4, DESIGN §12.2 item 4): a client without the validator.
        using var raw = server.CreateDefaultClient();
        using (var limited = await raw.PostAsync("/api/v1/admin/login", JsonBody(new { pin = OwnerPin })))
        {
            var body = await Players.ReadAsync(limited, 429);
            Contract.AssertError(body, "rateLimited");
            Assert.Equal(body.GetProperty("error").GetProperty("details").GetProperty("retryAfterSec").GetInt32().ToString(), limited.Headers.RetryAfter!.ToString());
        }

        server.Clock.Advance(TimeSpan.FromSeconds(301));
        await LoginAsync(server, OwnerPin);
    }

    [Fact]
    public async Task Parallel_wrong_pins_get_only_the_allowed_attempts_and_a_right_pin_costs_none()
    {
        server.Clock.Advance(TimeSpan.FromMinutes(6));
        using var raw = server.CreateDefaultClient(); // 429 is not in the vendored contract yet
        var answers = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            using var response = await raw.PostAsync("/api/v1/admin/login", JsonBody(new { pin = "9999" }));
            var details = (await Players.ReadAsync(response, (int)response.StatusCode)).GetProperty("error").GetProperty("details");
            return $"{(int)response.StatusCode} {(details.TryGetProperty("reason", out var reason) ? reason.GetString() : "")}";
        }));
        Assert.Equal([("401 invalidPin", 5), ("429 ", 35)], answers.GroupBy(a => a).Select(g => (g.Key, g.Count())).OrderBy(g => g.Key));

        // After the window: a right PIN gives its attempt back, so five wrong ones still get 401 and only then 429.
        server.Clock.Advance(TimeSpan.FromSeconds(301));
        await LoginAsync(server, OwnerPin);
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await server.Http.PostAsync("/api/v1/admin/login", JsonBody(new { pin = "9999" }));
            await Contract.ReadErrorAsync(wrong, 401, "unauthorized", "invalidPin");
        }

        using (var limited = await raw.PostAsync("/api/v1/admin/login", JsonBody(new { pin = OwnerPin })))
        {
            Assert.Equal(429, (int)limited.StatusCode);
        }

        server.Clock.Advance(TimeSpan.FromSeconds(301));
    }

    [Fact]
    public void Pin_limit_counts_an_ipv6_client_by_its_64_and_a_mapped_ipv4_as_ipv4()
    {
        static string Key(string ip) => StaffTokens.PinLimitKey(System.Net.IPAddress.Parse(ip));
        Assert.Equal(Key("2001:db8:1:2::1"), Key("2001:db8:1:2:ffff:ffff:ffff:fffe"));
        Assert.NotEqual(Key("2001:db8:1:2::1"), Key("2001:db8:1:3::1"));
        Assert.Equal(("10.0.0.5", "10.0.0.5", ""), (Key("::ffff:10.0.0.5"), Key("10.0.0.5"), StaffTokens.PinLimitKey(null)));
    }

    [Fact]
    public async Task Logout_is_always_200_and_revokes_the_token()
    {
        var token = await LoginAsync(server, OwnerPin);
        var me = await ExpectAsync(server, 200, HttpMethod.Get, "/me", token);
        Assert.Equal(("Владелец", "owner", true), (me.GetProperty("staff").GetProperty("name").GetString(), me.GetProperty("staff").GetProperty("role").GetString(),
            me.GetProperty("staff").GetProperty("active").GetBoolean()));

        foreach (var presented in new[] { null, "garbage", token, token })
        {
            Assert.True((await ExpectAsync(server, 200, HttpMethod.Post, "/logout", presented, new { })).GetProperty("ok").GetBoolean());
        }

        using var response = await server.Http.SendAsync(Request(HttpMethod.Get, "/me", token));
        await Contract.ReadErrorAsync(response, 401, "unauthorized", "invalid");
    }

    [Fact]
    public async Task Deactivation_kills_the_members_tokens_at_once()
    {
        var hmac = server.Services.GetRequiredService<StaffTokens>().PinHmac("4242");
        var id = Guid.NewGuid();
        await Players.ExecuteAsync(server,
            "INSERT INTO staff (id, network_id, club_id, name, role, pin_hmac) SELECT @id, network_id, id, 'Temp', 'cashier', @hmac FROM clubs",
            new { id, hmac });
        var token = await LoginAsync(server, "4242");
        await ExpectAsync(server, 200, HttpMethod.Get, "/me", token);

        await Players.ExecuteAsync(server, "UPDATE staff SET active = false WHERE id = @id", new { id });
        using (var response = await server.Http.SendAsync(Request(HttpMethod.Get, "/me", token)))
        {
            await Contract.ReadErrorAsync(response, 401, "unauthorized", "invalid");
        }

        using var login = await server.Http.PostAsync("/api/v1/admin/login", JsonBody(new { pin = "4242" }));
        await Contract.ReadErrorAsync(login, 401, "unauthorized", "invalidPin");
    }

    [Fact]
    public async Task Club_api_key_is_the_owner_and_owner_only_routes_refuse_a_cashier()
    {
        var key = await ApiKeyAsync(server);
        try
        {
            var me = (await ExpectAsync(server, 200, HttpMethod.Get, "/me", key)).GetProperty("staff");
            Assert.Equal(("apiKey", StaffTokens.ApiKeyName, "owner"), (me.GetProperty("id").GetString(), me.GetProperty("name").GetString(), me.GetProperty("role").GetString()));
            await ExpectAsync(server, 200, HttpMethod.Post, "/pcs", key, new { zone = "VIP", number = 7001 });
            await ExpectAsync(server, 200, HttpMethod.Post, "/logout", key, new { });
            await ExpectAsync(server, 200, HttpMethod.Get, "/me", key); // logout does not revoke ck_

            using (var wrong = await server.Http.SendAsync(Request(HttpMethod.Get, "/me", key + "x")))
            {
                await Contract.ReadErrorAsync(wrong, 401, "unauthorized", "invalid");
            }

            var cashier = await LoginAsync(server, CashierPin);
            foreach (var (method, path) in new[] { (HttpMethod.Post, "/pcs"), (HttpMethod.Patch, "/club") })
            {
                using var refused = await server.Http.SendAsync(Request(method, path, cashier, new { zone = "VIP", number = 7002 }));
                await Contract.ReadErrorAsync(refused, 403, "forbidden", "ownerOnly");
            }

            // For the owner a not-implemented owner route (the network page, D-19) is its 501; a cashier got 403 before it.
            using var owner = await server.Http.SendAsync(Request(HttpMethod.Get, "/network", key));
            await Contract.ReadErrorAsync(owner, 501, "notImplemented");
            using var cashierNetwork = await server.Http.SendAsync(Request(HttpMethod.Get, "/network", cashier));
            await Contract.ReadErrorAsync(cashierNetwork, 403, "forbidden", "ownerOnly");
        }
        finally
        {
            await ClearApiKeyAsync(server);
        }
    }

    [Fact]
    public async Task Staff_token_slides_12_hours_since_its_last_use()
    {
        var token = await LoginAsync(server, CashierPin);
        server.Clock.Advance(TimeSpan.FromHours(11));
        await ExpectAsync(server, 200, HttpMethod.Get, "/me", token);
        server.Clock.Advance(TimeSpan.FromHours(11));
        await ExpectAsync(server, 200, HttpMethod.Get, "/me", token);
        server.Clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        using var response = await server.Http.SendAsync(Request(HttpMethod.Get, "/me", token));
        await Contract.ReadErrorAsync(response, 401, "unauthorized", "invalid");
    }
}

/// <summary>CORS of the console (DESIGN §3.8): preflight 204 without authentication, headers on errors too, only for <c>Cors:AllowedOrigins</c> and <c>/admin/*</c>.</summary>
public sealed class CorsTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Theory]
    [InlineData(ServerFixture.AdminOrigin, true)]
    [InlineData("http://evil.example", false)]
    public async Task Preflight_is_204_without_authentication(string origin, bool allowed)
    {
        using var raw = server.CreateDefaultClient(); // OPTIONS is not a contract operation
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/admin/overview");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization, content-type, idempotency-key");
        using var response = await raw.SendAsync(request);
        Assert.Equal(204, (int)response.StatusCode);
        Assert.Contains("Origin", response.Headers.Vary);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed)
        {
            Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("Idempotency-Key", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
            Assert.Contains("PATCH", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        }
    }

    [Fact]
    public async Task Errors_carry_the_headers_and_other_routes_have_none()
    {
        using (var request = Request(HttpMethod.Get, "/me", null))
        {
            request.Headers.Add("Origin", ServerFixture.AdminOrigin);
            using var response = await server.Http.SendAsync(request);
            await Contract.ReadErrorAsync(response, 401, "unauthorized", "invalid");
            Assert.Equal(ServerFixture.AdminOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("X-Trace-Id", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        }

        using var agentRoute = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tariffs");
        agentRoute.Headers.Add("Origin", ServerFixture.AdminOrigin);
        using var other = await server.Http.SendAsync(agentRoute);
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

/// <summary>
/// The counter (DESIGN §5.1–§5.7, S4): the kiosk's rules in the order of §5.2 and <c>{session, charged, balance}</c>;
/// extend/end through the same <c>SessionService</c> (pro-rata refund, postpaid refused), top-up with the tier bonus half-up
/// to 100 and <c>transaction</c>, idempotent retries, the price preview of <c>console.spec.ts</c>, the hall snapshot.
/// </summary>
public sealed class CounterTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Open_session_checks_the_rules_in_order_and_answers_session_charged_balance()
    {
        var token = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, token);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 2_000_000);
        object Body(Guid? pc = null, Guid? user = null, Guid? tariff = null, int minutes = 60) =>
            new { pcId = pc ?? agent.PcId, userId = user ?? player.Id, tariffId = tariff ?? Players.Standard, minutes };

        foreach (var (body, what) in new[] { (Body(pc: Guid.NewGuid()), "pc"), (Body(user: Guid.NewGuid()), "user"), (Body(tariff: Guid.NewGuid()), "tariff") })
        {
            var error = await ExpectAsync(Server, 404, HttpMethod.Post, "/sessions", token, body);
            Assert.Equal(what, error.GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        }

        await Players.ExecuteAsync(Server, "UPDATE pcs SET maintenance = true WHERE id = @PcId", new { agent.PcId });
        Contract.AssertError(await ExpectAsync(Server, 403, HttpMethod.Post, "/sessions", token, Body()), "policyDenied");
        await Players.ExecuteAsync(Server, "UPDATE pcs SET maintenance = false WHERE id = @PcId", new { agent.PcId });

        var banned = await Players.CreateAsync(Server);
        await Players.ProfileAsync(Server, banned, blacklisted: true);
        Assert.Equal("blacklisted", (await ExpectAsync(Server, 403, HttpMethod.Post, "/sessions", token, Body(user: banned.Id))).GetProperty("error").GetProperty("details").GetProperty("rule").GetString());
        var poor = await Players.CreateAsync(Server, balance: 100);
        Contract.AssertError(await ExpectAsync(Server, 402, HttpMethod.Post, "/sessions", token, Body(user: poor.Id)), "insufficientFunds");
        Assert.Equal("min", (await ExpectAsync(Server, 400, HttpMethod.Post, "/sessions", token, Body(minutes: 3))).GetProperty("error").GetProperty("details").GetProperty("reason").GetString());

        var opened = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", token, Body());
        Assert.Equal((3600, 1_200_000L, 800_000L), (opened.GetProperty("session").GetProperty("secondsLeft").GetInt32(),
            opened.GetProperty("charged").GetProperty("amount").GetInt64(), opened.GetProperty("balance").GetProperty("amount").GetInt64()));
        var id = opened.GetProperty("session").GetProperty("id").GetGuid();
        Assert.Equal("cashier:true", await Players.ScalarAsync<string>(Server, "SELECT origin || ':' || (created_by_staff_id = @staff) FROM sessions WHERE id = @id",
            new { id, staff = DevSeed.Sid("staff:cashier-1") }));
        Contract.AssertError(await ExpectAsync(Server, 409, HttpMethod.Post, "/sessions", token, Body()), "sessionAlreadyActive");
        Assert.Equal(1_200_000, await Players.ScalarAsync<long>(Server, "SELECT amount FROM audit_entries WHERE action = 'sessionOpen' AND pc_id = @PcId", new { agent.PcId }));
    }

    [Fact]
    public async Task Extend_and_end_reuse_the_kiosk_rules_and_refund_what_was_paid()
    {
        var token = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, token);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 5_000_000);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", token, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 });

        var extended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/extend", token, new { pcId = agent.PcId, minutes = 30 });
        Assert.Equal((5400, 600_000L, 3_200_000L), (extended.GetProperty("session").GetProperty("secondsLeft").GetInt32(),
            extended.GetProperty("charged").GetProperty("amount").GetInt64(), extended.GetProperty("balance").GetProperty("amount").GetInt64()));
        var idle = await TestAgent.CreateAsync(Server);
        Assert.Equal("session", (await ExpectAsync(Server, 404, HttpMethod.Post, "/sessions/extend", token, new { pcId = idle.PcId, minutes = 30 }))
            .GetProperty("error").GetProperty("details").GetProperty("what").GetString());

        // 30 of 90 bought minutes used: the extension (600 000) and half of the first hour (600 000) come back.
        Server.Clock.Advance(TimeSpan.FromMinutes(30));
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", token, new { pcId = agent.PcId });
        Assert.Equal(("ended", 0L, 1_200_000L), (ended.GetProperty("session").GetProperty("state").GetString(),
            ended.GetProperty("charged").GetProperty("amount").GetInt64(), ended.GetProperty("refunded").GetProperty("amount").GetInt64()));
        // Beyond the contract (cash desk part 2): the balance after the settlement and the player; payable only for guests.
        Assert.Equal(4_400_000, ended.GetProperty("balance").GetProperty("amount").GetInt64());
        Assert.Equal((player.Id, "member"), (ended.GetProperty("user").GetProperty("id").GetGuid(), ended.GetProperty("user").GetProperty("role").GetString()));
        Assert.False(ended.TryGetProperty("payable", out _));
        Assert.Equal(4_400_000, await Players.BalanceAsync(Server, player.Id));
        await ExpectAsync(Server, 404, HttpMethod.Post, "/sessions/end", token, new { pcId = agent.PcId });

        // Extend refuses an unknown tariff, a short balance and a player deleted meanwhile.
        var poor = await Players.CreateAsync(Server, balance: 1_500_000);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", token, new { pcId = idle.PcId, userId = poor.Id, tariffId = Players.Standard, minutes = 60 });
        Assert.Equal("tariff", (await ExpectAsync(Server, 404, HttpMethod.Post, "/sessions/extend", token, new { pcId = idle.PcId, minutes = 30, tariffId = Guid.NewGuid() }))
            .GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        Contract.AssertError(await ExpectAsync(Server, 402, HttpMethod.Post, "/sessions/extend", token, new { pcId = idle.PcId, minutes = 30 }), "insufficientFunds");
        await Players.ExecuteAsync(Server, "UPDATE users SET deleted_at = now() WHERE id = @Id", new { poor.Id });
        Assert.Equal("user", (await ExpectAsync(Server, 404, HttpMethod.Post, "/sessions/extend", token, new { pcId = idle.PcId, minutes = 5 }))
            .GetProperty("error").GetProperty("details").GetProperty("what").GetString());

        // A kiosk postpaid session: no time is bought for it at the counter; ending it charges the minutes played.
        await agent.LoginAsync(player);
        var (_, postpaid) = await Players.StartAsync(agent, player, prepaid: false);
        await ExpectAsync(Server, 409, HttpMethod.Post, "/sessions/extend", token, new { sessionId = postpaid.GetProperty("id").GetGuid(), minutes = 30 });
        Server.Clock.Advance(TimeSpan.FromMinutes(10));
        var settled = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", token, new { sessionId = postpaid.GetProperty("id").GetGuid() });
        Assert.Equal((200_000L, 0L), (settled.GetProperty("charged").GetProperty("amount").GetInt64(), settled.GetProperty("refunded").GetProperty("amount").GetInt64()));
    }

    [Fact]
    public async Task Open_extend_and_end_replay_by_key_without_a_second_effect()
    {
        var token = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, token);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 5_000_000);
        await ReplayedAsync(Server, 201, HttpMethod.Post, "/sessions", token, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 });
        await ReplayedAsync(Server, 200, HttpMethod.Post, "/sessions/extend", token, new { pcId = agent.PcId, minutes = 30 });
        await ReplayedAsync(Server, 200, HttpMethod.Post, "/sessions/end", token, new { pcId = agent.PcId });
        Assert.Equal("sessionEnd,sessionExtend,sessionOpen", await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(action, ',' ORDER BY action) FROM audit_entries WHERE user_id = @Id", new { player.Id }));
        Assert.Equal("charge,charge,refund", await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(type, ',' ORDER BY type) FROM ledger_entries WHERE user_id = @Id AND type <> 'adjustment'", new { player.Id }));
    }

    /// <summary>
    /// <c>adminDeletePc</c> racing a create that already passed its PC check (agent auth or <c>LivePcAsync</c>): the create
    /// holds a stale row of a PC that is now deleted and must answer <c>404 pc</c> before it charges anything.
    /// </summary>
    [Fact]
    public async Task A_pc_deleted_after_the_callers_check_gets_no_session_and_no_charge()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var pc = (await Server.Services.GetRequiredService<Agents.PcRepository>().FindAsync(agent.PcId))!;
        await Players.ExecuteAsync(Server, "UPDATE pcs SET deleted_at = now() WHERE id = @PcId", new { agent.PcId });
        var sessions = Server.Services.GetRequiredService<Sessions.SessionService>();
        var network = await Players.ScalarAsync<Guid>(Server, "SELECT network_id FROM clubs WHERE id = @ClubId", new { pc.ClubId });
        foreach (var staff in new StaffContext?[] { null, new(null, StaffTokens.ApiKeyName, "owner", pc.ClubId, network) })
        {
            var (c, tx) = await Sessions.SessionService.BeginAsync(Server.Services.GetRequiredService<Npgsql.NpgsqlDataSource>());
            await using (c)
            await using (tx)
            {
                var error = await Assert.ThrowsAsync<ApiException>(() => sessions.CreateAsync(
                    c, tx, pc, new SessionCreateRequest(pc.Id, player.Id, Players.Standard, 60, true), replay: false, new Sessions.SessionEffects(), staff));
                Assert.Equal((404, "pc"), (error.Status, JsonSerializer.SerializeToElement(error.Details).GetProperty("what").GetString()));
                Assert.Equal(0, await Dapper.SqlMapper.ExecuteScalarAsync<int>(c, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id AND type = 'charge'", new { player.Id }, tx));
            }
        }
    }

    [Fact]
    public async Task Top_up_adds_the_tier_bonus_half_up_to_100_once_per_key()
    {
        var token = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, token);
        var player = await Players.CreateAsync(Server, balance: 0);
        var key = Guid.NewGuid();
        var body = new { userId = player.Id, amount = 5_001_000 };
        var first = await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", token, body, key);
        using (var again = await Server.Http.SendAsync(Request(HttpMethod.Post, "/wallet/topup", token, body, key)))
        {
            Assert.Equal(first.GetProperty("transaction").GetProperty("id").GetGuid(), (await Players.ReadAsync(again, 200)).GetProperty("transaction").GetProperty("id").GetGuid());
            Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        }

        // Tier 5 % from 50 000 sum: 250 050 tiyin → 250 100 (half-up to a whole sum).
        Assert.Equal((250_100L, 5_251_100L), (first.GetProperty("bonus").GetProperty("amount").GetInt64(), first.GetProperty("balance").GetProperty("amount").GetInt64()));
        var transaction = first.GetProperty("transaction");
        Assert.Equal(("topUp", 5_001_000L, 5_001_000L), (transaction.GetProperty("type").GetString(), transaction.GetProperty("amount").GetProperty("amount").GetInt64(),
            transaction.GetProperty("balanceAfter").GetProperty("amount").GetInt64()));
        Assert.Equal(5_251_100, await Players.BalanceAsync(Server, player.Id));

        Assert.Equal(3_000, CounterEndpoints.Bonus(30_000, """[{"minAmount":10000,"bonusPct":10}]""")); // 3 000 exactly
        Assert.Equal(0, CounterEndpoints.Bonus(1_000_000, null));
        foreach (var (bad, field) in new (object, string)[] { (new { userId = player.Id, amount = 0 }, "amount"), (new { userId = player.Id, amount = 100, method = "bitcoin" }, "method") })
        {
            Assert.Equal(field, (await ExpectAsync(Server, 400, HttpMethod.Post, "/wallet/topup", token, bad)).GetProperty("error").GetProperty("details").GetProperty("field").GetString());
        }

        await ExpectAsync(Server, 404, HttpMethod.Post, "/wallet/topup", token, new { userId = Guid.NewGuid(), amount = 100 });
    }

    [Fact]
    public async Task Quote_takes_the_day_rate_and_only_the_single_best_discount()
    {
        // console.spec.ts: every day +20 %, a −30 % happy hour over the whole clock; group staff −50 % wins, student −15 % loses.
        var token = await LoginAsync(Server, OwnerPin);
        var agent = await TestAgent.CreateAsync(Server);
        await Players.ExecuteAsync(Server, """
            UPDATE clubs SET settings = settings || '{"pricing":{"weekdayPct":[120,120,120,120,120,120,120],"holidays":[],"holidayPct":120},
              "happyHours":[{"name":"Весь день","days":[0,1,2,3,4,5,6],"from":"00:00","to":"12:00","discountPct":30,"zones":[]},
                            {"name":"Весь день","days":[0,1,2,3,4,5,6],"from":"12:00","to":"00:00","discountPct":30,"zones":[]}]}'::jsonb
            """);
        try
        {
            var staff = await Players.CreateAsync(Server);
            await Players.ProfileAsync(Server, staff, groupId: "staff");
            var student = await Players.CreateAsync(Server);
            await Players.ProfileAsync(Server, student, groupId: "student");
            async Task<JsonElement> QuoteAsync(Guid? userId) =>
                await ExpectAsync(Server, 200, HttpMethod.Post, "/quote", token, new { tariffId = Players.Standard, pcId = agent.PcId, minutes = 60, userId });

            var walkIn = await QuoteAsync(null);
            Assert.Equal((120, 30, 1_008_000L), (walkIn.GetProperty("dayPct").GetInt32(), walkIn.GetProperty("discountPct").GetInt32(), walkIn.GetProperty("total").GetProperty("amount").GetInt64()));
            var group = await QuoteAsync(staff.Id);
            Assert.Equal((50, "Сотрудник", 720_000L), (group.GetProperty("discountPct").GetInt32(), group.GetProperty("discountReason").GetString(), group.GetProperty("total").GetProperty("amount").GetInt64()));
            Assert.Equal(30, (await QuoteAsync(student.Id)).GetProperty("discountPct").GetInt32());

            var package = await ExpectAsync(Server, 200, HttpMethod.Post, "/quote", token, new { tariffId = Players.NightPack, pcId = agent.PcId });
            Assert.Equal(4_000_000, package.GetProperty("base").GetProperty("amount").GetInt64());
            await ExpectAsync(Server, 400, HttpMethod.Post, "/quote", token, new { tariffId = Players.Standard, pcId = agent.PcId });
            await ExpectAsync(Server, 404, HttpMethod.Post, "/quote", token, new { tariffId = Guid.NewGuid(), pcId = agent.PcId, minutes = 60 });
            await ExpectAsync(Server, 404, HttpMethod.Post, "/quote", token, new { tariffId = Players.Standard, pcId = Guid.NewGuid(), minutes = 60 });
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'pricing' - 'happyHours'");
        }
    }

    [Fact]
    public async Task Overview_draws_seats_with_sessions_tariffs_members_and_zones()
    {
        var token = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, token);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var hidden = await Players.CreateAsync(Server);
        await Players.ProfileAsync(Server, hidden, blacklisted: true);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", token, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 });

        var overview = await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", token);
        var seat = overview.GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("pc").GetProperty("id").GetGuid() == agent.PcId);
        Assert.Equal((player.Id, player.Id), (seat.GetProperty("session").GetProperty("userId").GetGuid(), seat.GetProperty("user").GetProperty("id").GetGuid()));
        // D-71: the key is always there; this PC reports no game (it never sent a heartbeat).
        Assert.Equal(JsonValueKind.Null, seat.GetProperty("game").ValueKind);
        var users = overview.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(player.Id, users);
        Assert.DoesNotContain(hidden.Id, users);
        Assert.Contains(overview.GetProperty("tariffs").EnumerateArray(), t => t.GetProperty("id").GetGuid() == Players.Standard);
        Assert.Equal(["Standard", "VIP", "Bootcamp"], overview.GetProperty("zones").EnumerateArray().Select(z => z.GetProperty("name").GetString()));
        Assert.Equal(overview.GetProperty("seats").GetArrayLength(), overview.GetProperty("club").GetProperty("total").GetInt32());
    }
}

/// <summary>
/// Shifts (DESIGN §4.3, S4): one open at a time (<c>409 shiftOpen</c>/<c>noShift</c>), the X/Z report sums only the
/// shift's ledger rows, <c>expectedCash</c> counts only cash top-ups, a shortfall over 500 000 tiyin is flagged.
/// </summary>
public sealed class ShiftTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task One_shift_at_a_time_and_the_Z_report_sums_its_own_ledger_rows()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        var player = await Players.CreateAsync(Server, balance: 0);
        var agent = await TestAgent.CreateAsync(Server);
        Contract.AssertError(await ExpectAsync(Server, 409, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 0 }), "conflict", "noShift");

        var opened = (await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/open", owner, new { openingCash = 100_000 })).GetProperty("shift");
        Assert.Equal(("Владелец", JsonValueKind.Null, JsonValueKind.Null), (opened.GetProperty("staffName").GetString(),
            opened.GetProperty("closedAt").ValueKind, opened.GetProperty("totals").ValueKind));
        Contract.AssertError(await ExpectAsync(Server, 409, HttpMethod.Post, "/shift/open", cashier, new { openingCash = 0 }), "conflict", "shiftOpen");
        Assert.Equal(opened.GetProperty("id").GetGuid(), (await ExpectAsync(Server, 200, HttpMethod.Get, "/me", cashier)).GetProperty("shift").GetProperty("id").GetGuid());

        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 1_000_000, method = "cash" });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 500_000, method = "card" });
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 });
        var x = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier)).GetProperty("x");
        Assert.Equal((1_000_000L, 500_000L, 1_200_000L, 3), (x.GetProperty("topUpCash").GetInt64(), x.GetProperty("topUpOther").GetInt64(),
            x.GetProperty("sessions").GetInt64(), x.GetProperty("count").GetInt32()));

        // Expected in the drawer: 100 000 + 1 000 000 cash; 500 000 counted — 600 000 short, over the 500 000 threshold.
        // A retry under the same key replays the Z report and closes nothing twice.
        var closed = await ReplayedAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 500_000 });
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'shiftClose'"));
        Assert.Equal((1_100_000L, 500_000L, 1_000_000L), (closed.GetProperty("expectedCash").GetInt64(), closed.GetProperty("shift").GetProperty("closingCash").GetInt64(),
            closed.GetProperty("shift").GetProperty("totals").GetProperty("topUpCash").GetInt64()));
        Assert.True(await Players.ScalarAsync<bool>(Server, "SELECT (meta->>'shortfall')::boolean FROM audit_entries WHERE action = 'shiftClose'"));
        Assert.Equal("Кассир Азиз", await Players.ScalarAsync<string>(Server, "SELECT staff_name FROM audit_entries WHERE action = 'shiftClose'"));

        var state = await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", owner);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, opened.GetProperty("id").GetGuid()),
            (state.GetProperty("shift").ValueKind, state.GetProperty("x").ValueKind, state.GetProperty("history")[0].GetProperty("id").GetGuid()));
        Contract.AssertError(await ExpectAsync(Server, 409, HttpMethod.Post, "/shift/close", owner, new { closingCash = 0 }), "conflict", "noShift");

        // Ending needs no shift: its refund, after the Z report, joins none.
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id AND shift_id IS NULL", new { player.Id }));
    }
}

/// <summary>
/// The hall editor (DESIGN §9, S4): approval of a waiting PC through <c>maintenance:false</c>, <c>409 pcBusy</c> and
/// <c>401 revoked</c> after delete, seats added and moved, <c>config_version</c> + <c>refreshConfig</c> on a rename, the last
/// heartbeat's games volume in the hall and health lists (D-73).
/// </summary>
public sealed class PcAdminTests(ApprovalServerFixture server) : IClassFixture<ApprovalServerFixture>
{
    [Fact]
    public async Task Maintenance_false_approves_a_waiting_pc()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var agent = await ApprovedAgentAsync(owner);
        Assert.NotEqual(Guid.Empty, agent.PcId);
    }

    [Fact]
    public async Task A_busy_pc_is_not_deleted_and_a_deleted_one_is_revoked()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var agent = await ApprovedAgentAsync(owner);
        var player = await Players.CreateAsync(server);
        await OpenShiftAsync(server, owner);
        await ExpectAsync(server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 });
        Contract.AssertError(await ExpectAsync(server, 409, HttpMethod.Delete, $"/pcs/{agent.PcId}", owner), "conflict", "pcBusy");

        await ExpectAsync(server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId });
        Assert.True((await ExpectAsync(server, 200, HttpMethod.Delete, $"/pcs/{agent.PcId}", owner)).GetProperty("ok").GetBoolean());
        using var heartbeat = await agent.HeartbeatAsync();
        await Contract.ReadErrorAsync(heartbeat, 401, "unauthorized", "revoked");
        await ExpectAsync(server, 404, HttpMethod.Delete, $"/pcs/{agent.PcId}", owner);
    }

    [Fact]
    public async Task Seats_are_added_moved_and_listed_and_a_rename_reaches_the_agent()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        Contract.AssertError(await ExpectAsync(server, 403, HttpMethod.Post, "/pcs", cashier, new { zone = "VIP", number = 500 }), "forbidden", "ownerOnly");

        var added = (await ReplayedAsync(server, 200, HttpMethod.Post, "/pcs", owner, new { zone = "VIP", number = 500, device = "console", x = 3, y = 4 })).GetProperty("pc");
        Assert.Equal(("CONSOLE-500", "offline"), (added.GetProperty("name").GetString(), added.GetProperty("status").GetString()));
        Assert.Equal(("number", "taken"), Details(await ExpectAsync(server, 400, HttpMethod.Post, "/pcs", owner, new { zone = "VIP", number = 500 })));
        foreach (var method in new[] { HttpMethod.Patch, HttpMethod.Delete })
        {
            Contract.AssertError(await ExpectAsync(server, 403, method, $"/pcs/{added.GetProperty("id").GetGuid()}", cashier, method == HttpMethod.Patch ? new { x = 1 } : null),
                "forbidden", "ownerOnly");
        }

        var hall = await ExpectAsync(server, 200, HttpMethod.Get, "/pcs", cashier);
        var seat = hall.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == added.GetProperty("id").GetGuid());
        Assert.Equal((3, 4, "console", JsonValueKind.Null), (seat.GetProperty("x").GetInt32(), seat.GetProperty("y").GetInt32(), seat.GetProperty("device").GetString(), seat.GetProperty("hardware").ValueKind));

        var agent = await ApprovedAgentAsync(owner);
        var before = (await Players.ReadAsync(await agent.HeartbeatAsync(), 200)).GetProperty("configVersion").GetInt32();
        var renamed = (await ExpectAsync(server, 200, HttpMethod.Patch, $"/pcs/{agent.PcId}", owner, new { name = "PC-NEW", x = 9 })).GetProperty("pc");
        Assert.Equal("PC-NEW", renamed.GetProperty("name").GetString());
        var after = await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        Assert.Equal((before + 1, 1), (after.GetProperty("configVersion").GetInt32(), after.GetProperty("pendingCommands").GetInt32()));
        var command = (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("commands")), 200)).GetProperty("items")[0];
        Assert.Equal(("refreshConfig", true), (command.GetProperty("name").GetString(), command.GetProperty("payload").GetProperty("config").GetBoolean()));
        await ExpectAsync(server, 404, HttpMethod.Patch, $"/pcs/{Guid.NewGuid()}", owner, new { name = "X" });
    }

    [Fact]
    public async Task The_hall_and_health_carry_the_games_volume_of_the_last_heartbeat()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        var agent = await ApprovedAgentAsync(owner);
        async Task<JsonElement[]> VolumesAsync() =>
        [
            (await ExpectAsync(server, 200, HttpMethod.Get, "/pcs", cashier)).GetProperty("items").EnumerateArray()
                .Single(p => p.GetProperty("id").GetGuid() == agent.PcId).GetProperty("gamesVolume"),
            (await ExpectAsync(server, 200, HttpMethod.Get, "/health", cashier)).GetProperty("pcs").EnumerateArray()
                .Single(p => p.GetProperty("id").GetGuid() == agent.PcId).GetProperty("gamesVolume"),
        ];

        // Never reported (no heartbeat yet), then an agent that sends no gamesVolume: null, so the desk shows nothing.
        Assert.All(await VolumesAsync(), v => Assert.Equal(JsonValueKind.Null, v.ValueKind));
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        Assert.All(await VolumesAsync(), v => Assert.Equal(JsonValueKind.Null, v.ValueKind));

        var since = new DateTimeOffset(2026, 10, 10, 7, 40, 0, TimeSpan.Zero);
        await Players.ReadAsync(await agent.HeartbeatAsync(gamesVolume: new { owner = "agent", mounted = true, driveLetter = "G", since }), 200);
        Assert.All(await VolumesAsync(), v => Assert.Equal(
            ("agent", true, "G", since),
            (v.GetProperty("owner").GetString(), v.GetProperty("mounted").GetBoolean(), v.GetProperty("driveLetter").GetString(),
             v.GetProperty("since").GetDateTimeOffset())));

        // ClubDisklessHelper mounts it: the agent cannot tell whether it is mounted, and the field is left out as it sent it.
        await Players.ReadAsync(await agent.HeartbeatAsync(gamesVolume: new { owner = "disklessHelper", mounted = (bool?)null }), 200);
        Assert.All(await VolumesAsync(), v =>
            Assert.Equal(("disklessHelper", false), (v.GetProperty("owner").GetString(), v.TryGetProperty("mounted", out _))));
    }

    /// <summary>A new PC waits for approval (D-7), the owner approves it with <c>maintenance:false</c>, the agent registers again.</summary>
    private async Task<TestAgent> ApprovedAgentAsync(string owner)
    {
        var hwid = TestAgent.RandomHwid();
        var body = TestAgent.RegisterBody(hwid, TestAgent.RandomMac());
        using (var pending = await TestAgent.RegisterAsync(server, body))
        {
            var error = await Contract.ReadErrorAsync(pending, 403, "forbidden", "pendingApproval");
            var pcId = error.GetProperty("error").GetProperty("details").GetProperty("pcId").GetGuid();
            Assert.Equal("offline", (await ExpectAsync(server, 200, HttpMethod.Patch, $"/pcs/{pcId}", owner, new { maintenance = false }))
                .GetProperty("pc").GetProperty("status").GetString());
        }

        using var registered = await TestAgent.RegisterAsync(server, body);
        return new TestAgent(server, await Players.ReadAsync(registered, 200), hwid);
    }

    private static (string?, string?) Details(JsonElement error) =>
        (error.GetProperty("error").GetProperty("details").GetProperty("field").GetString(), error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
}

/// <summary>A Kestrel fixture whose <c>adminCommand</c> waits only 2 s for an ack.</summary>
public sealed class ShortAckServerFixture : KestrelServerFixture
{
    public ShortAckServerFixture() => Settings["Agents:AckWaitSec"] = "2";
}

/// <summary>
/// <c>adminCommand</c> (DESIGN §6.4 step 4): offline → <c>agentOffline</c> and the command stays queued with <c>expiresAt</c>,
/// <c>unlock</c> supersedes the pending <c>lock</c>; online → the agent's ack; silence → <c>timeout</c>. Cashier end/extend
/// queue <c>endSession</c>/<c>extendSession {charge:false}</c>.
/// </summary>
public sealed class AdminCommandTests(ShortAckServerFixture server) : IClassFixture<ShortAckServerFixture>
{
    [Fact]
    public async Task Offline_pc_answers_agentOffline_and_keeps_the_command_queued()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var agent = await TestAgent.CreateAsync(server);
        var ack = (await ExpectAsync(server, 200, HttpMethod.Post, $"/pcs/{agent.PcId}/command", owner, new { kind = "lock", text = "Подойдите к стойке" })).GetProperty("ack");
        Assert.Equal((false, "agentOffline"), (ack.GetProperty("ok").GetBoolean(), ack.GetProperty("error").GetProperty("code").GetString()));
        Assert.Equal("lock:true:true", await Players.ScalarAsync<string>(server,
            "SELECT name || ':' || (expires_at IS NOT NULL) || ':' || (issued_by_staff_id = @owner) FROM agent_commands WHERE pc_id = @PcId",
            new { agent.PcId, owner = DevSeed.Sid("staff:owner") }));

        await ExpectAsync(server, 200, HttpMethod.Post, $"/pcs/{agent.PcId}/command", owner, new { kind = "unlock" });
        var pending = (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("commands")), 200)).GetProperty("items");
        Assert.Equal(["unlock"], pending.EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        foreach (var (body, field, reason) in new[] { ((object)new { kind = "message" }, "text", "required"), (new { kind = "dance" }, "kind", "unknown") })
        {
            var error = await ExpectAsync(server, 400, HttpMethod.Post, $"/pcs/{agent.PcId}/command", owner, body);
            Assert.Equal((field, reason), (error.GetProperty("error").GetProperty("details").GetProperty("field").GetString(), error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString()));
        }

        await ExpectAsync(server, 404, HttpMethod.Post, $"/pcs/{Guid.NewGuid()}/command", owner, new { kind = "lock" });
    }

    [Fact]
    public async Task Online_pc_acks_within_the_wait_and_a_silent_one_times_out()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var agent = await TestAgent.CreateAsync(server);
        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        await Wait.UntilAsync(() => server.Services.GetRequiredService<AgentSocketHub>().IsConnected(agent.PcId));

        var call = ExpectAsync(server, 200, HttpMethod.Post, $"/pcs/{agent.PcId}/command", owner, new { kind = "message", text = "Подойдите к стойке" });
        var frame = await socket.ReceiveAsync();
        Contract.AssertMessage("commandMessage", frame);
        Assert.Equal(("Администратор", true), (frame.GetProperty("payload").GetProperty("from").GetString(), frame.GetProperty("payload").GetProperty("requiresAck").GetBoolean()));
        await socket.SendAsync(new
        {
            type = "ack", id = Guid.NewGuid(), ts = DateTimeOffset.UtcNow, payload = (object?)null,
            ack = new { id = frame.GetProperty("id").GetGuid(), ok = true, result = new { deliveredAt = DateTimeOffset.UtcNow } },
        });
        var ack = (await call).GetProperty("ack");
        Assert.True(ack.GetProperty("ok").GetBoolean());
        Assert.True(ack.GetProperty("result").TryGetProperty("deliveredAt", out _));

        var silent = ExpectAsync(server, 200, HttpMethod.Post, $"/pcs/{agent.PcId}/command", owner, new { kind = "reboot" });
        Assert.Equal("reboot", (await socket.ReceiveAsync()).GetProperty("name").GetString());
        Assert.Equal("timeout", (await silent).GetProperty("ack").GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cashier_extend_and_end_queue_extendSession_and_endSession()
    {
        var owner = await LoginAsync(server, OwnerPin);
        await OpenShiftAsync(server, owner);
        var agent = await TestAgent.CreateAsync(server);
        var player = await Players.CreateAsync(server);
        var id = (await ExpectAsync(server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 }))
            .GetProperty("session").GetProperty("id").GetGuid();
        await ExpectAsync(server, 200, HttpMethod.Post, "/sessions/extend", owner, new { sessionId = id, minutes = 30 });
        await ExpectAsync(server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });

        var items = (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("commands")), 200)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["extendSession", "endSession"], items.Select(c => c.GetProperty("name").GetString()));
        Assert.Equal((id, 30, false), (items[0].GetProperty("payload").GetProperty("sessionId").GetGuid(), items[0].GetProperty("payload").GetProperty("minutes").GetInt32(),
            items[0].GetProperty("payload").GetProperty("charge").GetBoolean()));
        Assert.Equal((id, "admin"), (items[1].GetProperty("payload").GetProperty("sessionId").GetGuid(), items[1].GetProperty("payload").GetProperty("reason").GetString()));
    }
}

/// <summary>
/// Slice S4 with the real agent (DESIGN §10.b): the connected <c>RealtimeClient</c> gets <c>sessionUpdated</c> after the
/// cashier opens and extends, the commands <c>extendSession {charge:false}</c> and <c>endSession</c>, and agrees with
/// <c>GET /sessions/current</c>; a <c>message</c> is acked over WS and then again over REST with <c>ackedAt</c>. No
/// <c>pcStatusChanged</c> is pushed.
/// </summary>
public sealed class AgentHarnessS4Tests(KestrelServerFixture server) : IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task Cashier_actions_reach_the_real_agent()
    {
        var ct = CancellationToken.None;
        await using var agent = await AgentHarness.CreateAsync(server);
        var client = agent.Client;
        var pcId = (await client.RegisterAsync(agent.RegisterRequest(), ct)).PcId;
        var player = await Players.CreateAsync(server, balance: 5_000_000);
        await client.LoginAsync(new Contracts.Users.AuthRequest(Contracts.Users.AuthKind.Password, player.Username, Players.Password, null, null, null, pcId, agent.Hwid), ct);

        var pushes = new List<Contracts.Commands.WsFrame>();
        var handled = new List<ServerCommand>();
        agent.Realtime.PushReceived += (_, frame) =>
        {
            lock (pushes)
            {
                pushes.Add(frame);
            }
        };
        agent.Realtime.CommandHandler = (command, _) =>
        {
            lock (handled)
            {
                handled.Add(command);
            }

            return Task.FromResult(command.Type == ServerCommandType.Message
                ? CommandAck.Success(new MessageDeliveryResult(DateTimeOffset.UtcNow))
                : CommandAck.Success());
        };
        using var stop = new CancellationTokenSource();
        var run = agent.Realtime.RunAsync(null, stop.Token);
        await Wait.UntilAsync(() => server.Services.GetRequiredService<AgentSocketHub>().IsConnected(pcId));

        var owner = await LoginAsync(server, OwnerPin);
        await OpenShiftAsync(server, owner);
        var id = (await ExpectAsync(server, 201, HttpMethod.Post, "/sessions", owner, new { pcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 }))
            .GetProperty("session").GetProperty("id").GetGuid();
        await Wait.UntilAsync(() => Pushed(pushes, id, 3600));

        await ExpectAsync(server, 200, HttpMethod.Post, "/sessions/extend", owner, new { pcId, minutes = 30 });
        await Wait.UntilAsync(() => Pushed(pushes, id, 5400) && Handled(handled, ServerCommandType.ExtendSession));
        var extend = handled.Single(c => c.Type == ServerCommandType.ExtendSession).PayloadAs<ExtendSessionCommand>()!;
        Assert.Equal((id, 30, false), (extend.SessionId, extend.Minutes, extend.Charge));
        Assert.Equal(5400, (await client.GetCurrentSessionAsync(pcId, ct))!.SecondsLeft);

        await ExpectAsync(server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId });
        await Wait.UntilAsync(() => Handled(handled, ServerCommandType.EndSession));
        Assert.Equal(SessionEndReason.Admin, handled.Single(c => c.Type == ServerCommandType.EndSession).PayloadAs<EndSessionCommand>()!.Reason);
        Assert.Null(await client.GetCurrentSessionAsync(pcId, ct));

        var ack = (await ExpectAsync(server, 200, HttpMethod.Post, $"/pcs/{pcId}/command", owner, new { kind = "message", text = "Подойдите к стойке" })).GetProperty("ack");
        Assert.True(ack.GetProperty("ok").GetBoolean());
        var message = handled.Single(c => c.Type == ServerCommandType.Message);
        await client.AckCommandAsync(pcId, message.Id, CommandAck.Success(new MessageDeliveryResult(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)), ct);
        Assert.NotNull((await server.Services.GetRequiredService<Agents.CommandRepository>().WaitForAckAsync(message.Id, TimeSpan.FromSeconds(5), ct))!
            .Result!.Value.GetProperty("ackedAt").GetString());

        lock (pushes)
        {
            Assert.DoesNotContain(pushes, p => p.Name == "pcStatusChanged");
        }

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static bool Pushed(List<Contracts.Commands.WsFrame> pushes, Guid sessionId, int secondsLeft)
    {
        lock (pushes)
        {
            return pushes.Any(p => p.Name == "sessionUpdated" && p.Payload is { } s && s.GetProperty("id").GetGuid() == sessionId && s.GetProperty("secondsLeft").GetInt32() == secondsLeft);
        }
    }

    private static bool Handled(List<ServerCommand> handled, ServerCommandType type)
    {
        lock (handled)
        {
            return handled.Any(c => c.Type == type);
        }
    }
}

/// <summary>Coverage of slice S4 (DESIGN §10.a, §11): each of its 17 operations got a contract-valid success response.</summary>
public sealed class CoverageS4Tests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task S4_operations_all_have_a_contract_valid_success_response()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        await ExpectAsync(Server, 200, HttpMethod.Get, "/me", owner);
        await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", owner);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/open", owner, new { openingCash = 0 });
        await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", owner);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = player.Id, amount = 10_000_000 }, Guid.NewGuid());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/quote", owner, new { tariffId = Players.Standard, pcId = agent.PcId, minutes = 60, userId = player.Id });
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 }, Guid.NewGuid());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/extend", owner, new { pcId = agent.PcId, minutes = 30 }, Guid.NewGuid());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId }, Guid.NewGuid());
        await ExpectAsync(Server, 200, HttpMethod.Post, $"/pcs/{agent.PcId}/command", owner, new { kind = "lock" });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", owner, new { closingCash = 0 }, Guid.NewGuid());
        await ExpectAsync(Server, 200, HttpMethod.Get, "/pcs", owner);
        var seat = (await ExpectAsync(Server, 200, HttpMethod.Post, "/pcs", owner, new { zone = "Standard", number = 900 }, Guid.NewGuid())).GetProperty("pc").GetProperty("id").GetGuid();
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/pcs/{seat}", owner, new { x = 5, maintenance = true });
        await ExpectAsync(Server, 200, HttpMethod.Delete, $"/pcs/{seat}", owner);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/logout", owner, new { });

        var success = new Dictionary<string, int>
        {
            ["adminLogin"] = 200, ["adminLogout"] = 200, ["adminMe"] = 200, ["adminOverview"] = 200, ["adminOpenSession"] = 201, ["adminExtend"] = 200,
            ["adminEnd"] = 200, ["adminTopUp"] = 200, ["adminCommand"] = 200, ["adminShift"] = 200, ["adminOpenShift"] = 200, ["adminCloseShift"] = 200,
            ["adminQuote"] = 200, ["adminPcs"] = 200, ["adminAddPc"] = 200, ["adminUpdatePc"] = 200, ["adminDeletePc"] = 200,
        };
        Assert.Equal(
            StaffEndpoints.Operations.Concat(CounterEndpoints.Operations).Concat(ShiftEndpoints.Operations).Concat(PcAdminEndpoints.Operations).Order(),
            success.Keys.Order());
        Assert.All(success, op => Assert.True(Server.Covered.ContainsKey($"{op.Key} {op.Value}"), $"{op.Key} {op.Value} not covered"));
    }
}

/// <summary><c>PcStatusWorker</c> (DESIGN §6.6, §8): a PC whose derived status turns <c>offline</c> gets one <c>pcOffline</c> telemetry event.</summary>
public sealed class PcStatusWorkerTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task A_pc_that_goes_offline_gets_one_pcOffline_event()
    {
        var agent = await TestAgent.CreateAsync(server);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        var worker = server.Services.GetRequiredService<Agents.PcStatusWorker>();
        await worker.RunOnceAsync();
        server.Clock.Advance(TimeSpan.FromSeconds(91));
        await worker.RunOnceAsync();
        await worker.RunOnceAsync();
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM telemetry_events WHERE pc_id = @PcId AND kind = 'pcOffline'", new { agent.PcId }));
    }
}