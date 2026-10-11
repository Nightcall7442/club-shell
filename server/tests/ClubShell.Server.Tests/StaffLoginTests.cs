using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The PIN limits as in production (D-76): club-wide limits on (30 in 24 h), the client address from <c>X-Real-IP</c> as on
/// Railway, the platform administration for a second club, and the server's log kept.
/// </summary>
public sealed class StaffLoginFixture : ServerFixture
{
    public const string PlatformKey = "platform-test-key-0123456789abcdef";

    public StaffLoginFixture()
    {
        Settings["Proxy:ClientIpHeader"] = "X-Real-IP";
        Settings["RateLimit:PinClubAttempts"] = "30";
        Settings["RateLimit:PinTrustedAttempts"] = "30";
        Settings["Platform:AdminKey"] = PlatformKey;
    }

    public LogRecorder Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(Logs));
    }
}

/// <summary>
/// Staff PIN lockout in the database (D-76, DESIGN §3.6): wrong PINs per club and source (an IPv4 address, an IPv6 /64, a
/// trusted browser), the club-wide budgets of new and of trusted browsers, the desks a club trusts staying open while
/// outsiders are locked out, a right PIN clearing nothing, exact counts under parallel requests, the alert once per window,
/// unknown and disabled clubs, malformed device ids, restarts and maintenance. Each test starts from no failures.
/// </summary>
public sealed class StaffLoginTests(StaffLoginFixture server) : IClassFixture<StaffLoginFixture>
{
    private const string WrongPin = "9999";
    private const string ArenaPin = "5150";

    private static readonly TimeSpan PastWindow = TimeSpan.FromSeconds(901);
    private static readonly TimeSpan PastClubWindow = TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1);

    private string? _code;

    [Fact]
    public async Task Sources_count_apart_an_ipv4_by_address_an_ipv6_by_its_64_and_a_restart_forgets_nothing()
    {
        await ResetAsync();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("401", await OutcomeAsync(WrongPin, "203.0.113.1"));
        }

        Assert.Equal("429 source", await OutcomeAsync(OwnerPin, "203.0.113.1"));
        Assert.Equal("200", await OutcomeAsync(OwnerPin, "203.0.113.2"));

        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal("401", await OutcomeAsync(WrongPin, $"2001:db8:1:2::{i}"));
        }

        Assert.Equal("429 source", await OutcomeAsync(OwnerPin, "2001:db8:1:2:ffff:ffff:ffff:fffe"));
        Assert.Equal("200", await OutcomeAsync(OwnerPin, "2001:db8:1:3::1"));

        // The failures live in the database: a new host on it (a deploy restarts the process) still refuses.
        using (var restarted = server.WithWebHostBuilder(_ => { }))
        {
            Assert.Equal("429 source", await OutcomeAsync(OwnerPin, "203.0.113.1", client: restarted));
        }

        // The window is 900 s: still closed after 5 min, open after 15.
        server.Clock.Advance(TimeSpan.FromSeconds(300));
        Assert.Equal("429 source", await OutcomeAsync(OwnerPin, "203.0.113.1"));
        server.Clock.Advance(PastWindow);
        Assert.Equal("200", await OutcomeAsync(OwnerPin, "203.0.113.1"));
    }

    [Fact]
    public async Task New_browsers_share_a_club_budget_and_the_desks_the_club_trusts_stay_open()
    {
        await ResetAsync();
        var logged = server.Logs.Lines.Count;
        var desk = NewDevice();
        Assert.Equal("200", await OutcomeAsync(CashierPin, "198.51.100.10", desk));
        var arenaDesk = NewDevice();
        Assert.Equal("200", await OutcomeAsync(ArenaPin, "198.51.100.11", arenaDesk, await ArenaAsync()));

        // Thirty wrong PINs from seven addresses, none of them past its own five.
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal("401", await OutcomeAsync(WrongPin, $"203.0.113.{20 + (i % 7)}"));
        }

        // Every browser the club does not trust is refused now, the right PIN too: a new address, a made-up device id, the
        // desk of another club.
        var (status, body) = await AttemptAsync(OwnerPin, "192.0.2.50");
        Assert.Equal(429, status);
        Assert.InRange(body.GetProperty("error").GetProperty("details").GetProperty("retryAfterSec").GetInt32(), 86_000, 86_400);
        Assert.Equal("429 club", await OutcomeAsync(OwnerPin, "192.0.2.51", NewDevice()));
        Assert.Equal("429 club", await OutcomeAsync(OwnerPin, "192.0.2.52", arenaDesk));

        // The desk signs in, even from an attacker's address; it is a source of its own.
        Assert.Equal("200", await OutcomeAsync(CashierPin, "203.0.113.20", desk));
        Assert.Equal("401", await OutcomeAsync(WrongPin, "203.0.113.20", desk));

        // One alert: the club's webhook and an error that reaches Sentry, with counts but no PIN and no address.
        Assert.Equal("429 club", await OutcomeAsync(WrongPin, "192.0.2.53"));
        var payloads = (await Players.ScalarAsync<string[]>(server, "SELECT array_agg(payload::text) FROM webhook_outbox WHERE event = 'suspicious'")) ?? [];
        var payload = Assert.Single(payloads);
        Assert.Contains("\"scope\": \"club\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"failures\": 30", payload, StringComparison.Ordinal);
        foreach (var secret in new[] { OwnerPin, CashierPin, WrongPin, "203.0.113." })
        {
            Assert.DoesNotContain(secret, payload, StringComparison.Ordinal);
        }

        var error = Assert.Single(server.Logs.Lines.Skip(logged), l => l.Level == LogLevel.Error && l.Category == typeof(StaffLoginGuard).FullName);
        Assert.True(ErrorReporting.Reported(error.Category, error.Level, null));
        Assert.Contains((await ClubIdAsync()).ToString(), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.", error.Message, StringComparison.Ordinal);

        // A day later new browsers are let in again.
        server.Clock.Advance(PastClubWindow);
        Assert.Equal("200", await OutcomeAsync(OwnerPin, "192.0.2.54"));
    }

    [Fact]
    public async Task Trusted_browsers_share_a_budget_of_their_own_so_new_browsers_do_not_help_a_guessing_cashier()
    {
        await ResetAsync();
        var devices = Enumerable.Range(0, 7).Select(_ => NewDevice()).ToList();
        foreach (var (device, i) in devices.Select((d, i) => (d, i)))
        {
            Assert.Equal("200", await OutcomeAsync(CashierPin, $"198.51.100.{30 + i}", device));
        }

        for (var i = 0; i < 6; i++)
        {
            for (var n = 0; n < 5; n++)
            {
                Assert.Equal("401", await OutcomeAsync(WrongPin, "198.51.100.30", devices[i]));
            }
        }

        // The seventh trusted browser never failed and is refused too.
        Assert.Equal("429 trusted", await OutcomeAsync(OwnerPin, "198.51.100.36", devices[6]));

        // A new browser is not trusted and gets in, but once it is trusted it shares the spent budget.
        var fresh = NewDevice();
        Assert.Equal("200", await OutcomeAsync(CashierPin, "198.51.100.37", fresh));
        Assert.Equal("429 trusted", await OutcomeAsync(WrongPin, "198.51.100.37", fresh));
        Assert.Equal("\"trusted\"", await Players.ScalarAsync<string>(server,
            "SELECT payload -> 'data' -> 'scope' FROM webhook_outbox WHERE event = 'suspicious' AND payload -> 'data' ->> 'reason' = 'pinLimit' ORDER BY id DESC LIMIT 1"));

        server.Clock.Advance(PastClubWindow);
        Assert.Equal("200", await OutcomeAsync(OwnerPin, "198.51.100.36", devices[6]));
    }

    [Fact]
    public async Task A_browser_is_no_longer_trusted_once_its_member_is_disabled_or_gets_a_new_pin()
    {
        await ResetAsync();
        var desk = NewDevice();
        var owner = await TokenAsync(OwnerPin, "198.51.100.70", desk);
        var fired = await AddCashierAsync(owner, "Уволенный кассир", "7301");
        var leaked = await AddCashierAsync(owner, "Кассир с утёкшим PIN", "7302");
        var home = NewDevice();
        var thief = NewDevice();
        Assert.Equal("200", await OutcomeAsync("7301", "203.0.113.90", home));
        Assert.Equal("200", await OutcomeAsync("7302", "203.0.113.91", thief));

        // The owner disables one and gives the other a new PIN: their browsers guess with the outsiders now, not in the budget
        // of the desks, and the desk the owner signed in from is still trusted.
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{fired}", owner, new { active = false });
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{leaked}", owner, new { pin = "73021" });
        Assert.Equal("401", await OutcomeAsync(WrongPin, "203.0.113.90", home));
        Assert.Equal("401", await OutcomeAsync(WrongPin, "203.0.113.91", thief));
        Assert.Equal("401", await OutcomeAsync(WrongPin, "198.51.100.70", desk));
        Assert.Equal("net:203.0.113.90 net:203.0.113.91 trusted", await Players.ScalarAsync<string>(server,
            "SELECT string_agg(CASE WHEN trusted THEN 'trusted' ELSE source END, ' ' ORDER BY trusted, source) FROM staff_login_failures"));

        // The platform's reset of a forgotten owner PIN forgets the owner's browsers too.
        using var client = server.CreateDefaultClient();
        using var reset = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/platform/clubs/{await ClubIdAsync()}/owners/{await StaffIdAsync(owner)}/pin")
        {
            Content = JsonBody(new { pin = OwnerPin }),
        };
        reset.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StaffLoginFixture.PlatformKey);
        using (var response = await client.SendAsync(reset))
        {
            await Players.ReadAsync(response, 200);
        }

        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM staff_devices"));
    }

    [Fact]
    public async Task A_club_limit_kept_full_alerts_once_per_window()
    {
        await ResetAsync();
        var logged = server.Logs.Lines.Count;
        for (var i = 0; i < 29; i++)
        {
            Assert.Equal("401", await OutcomeAsync(WrongPin, $"192.0.2.{150 + i}"));
        }

        server.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("401", await OutcomeAsync(WrongPin, "192.0.2.180")); // the 30th: the alert

        // The first 29 leave the window and the attacker fills it again while the failure that alerted is still in it.
        server.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromSeconds(1));
        for (var i = 0; i < 29; i++)
        {
            Assert.Equal("401", await OutcomeAsync(WrongPin, $"192.0.2.{200 + i}"));
        }

        Assert.Equal("429 club", await OutcomeAsync(OwnerPin, "192.0.2.240"));
        Assert.Equal(1, await AlertsAsync());

        // Once that failure has left the window too, the next trip alerts again.
        server.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("401", await OutcomeAsync(WrongPin, "192.0.2.241"));
        Assert.Equal(2, await AlertsAsync());
        Assert.Equal(2, server.Logs.Lines.Skip(logged).Count(l => l.Level == LogLevel.Error && l.Category == typeof(StaffLoginGuard).FullName));
    }

    [Fact]
    public async Task A_right_pin_does_not_reset_the_count_and_a_refused_attempt_records_nothing()
    {
        await ResetAsync();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("401", await OutcomeAsync(WrongPin, "203.0.113.40"));
        }

        Assert.Equal("200", await OutcomeAsync(OwnerPin, "203.0.113.40"));
        Assert.Equal("401", await OutcomeAsync(WrongPin, "203.0.113.40"));
        Assert.Equal("429 source", await OutcomeAsync(OwnerPin, "203.0.113.40"));
        Assert.Equal("429 source", await OutcomeAsync(WrongPin, "203.0.113.40"));
        Assert.Equal(5, await FailuresAsync());
    }

    [Fact]
    public async Task Parallel_attempts_get_exactly_the_allowed_failures_from_one_source_and_from_many()
    {
        await ResetAsync();
        var one = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => OutcomeAsync(WrongPin, "203.0.113.60")));
        Assert.Equal([("401", 5), ("429 source", 35)], one.GroupBy(o => o).Select(g => (g.Key, g.Count())).OrderBy(g => g.Key));

        await ResetAsync();
        var many = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => OutcomeAsync(WrongPin, $"192.0.2.{100 + i}")));
        Assert.Equal([("401", 30), ("429 club", 10)], many.GroupBy(o => o).Select(g => (g.Key, g.Count())).OrderBy(g => g.Key));
        Assert.Equal(30, await FailuresAsync());
    }

    [Fact]
    public async Task Unknown_codes_and_disabled_clubs_count_and_a_missing_code_counts_nothing()
    {
        await ResetAsync();
        var arena = await ArenaAsync();

        // Two clubs: a PIN without a code is 400 and counts nothing.
        for (var i = 0; i < 6; i++)
        {
            var (status, body) = await AttemptAsync(OwnerPin, "203.0.113.70", code: "");
            Assert.Equal(400, status);
            Contract.AssertError(body, "validation", "required");
        }

        Assert.Equal(0, await FailuresAsync());

        // Unknown codes share one bucket per source; a real club is another one.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("401", await OutcomeAsync(OwnerPin, "203.0.113.70", code: $"NOSUCH{i}"));
        }

        Assert.Equal("429 source", await OutcomeAsync(OwnerPin, "203.0.113.70", code: "ZZZZZZ"));
        Assert.Equal("200", await OutcomeAsync(OwnerPin, "203.0.113.70"));

        // A disabled club is a wrong PIN counted like an unknown code.
        var clubId = await Players.ScalarAsync<Guid>(server, "SELECT id FROM clubs WHERE code = @arena", new { arena });
        await Players.ExecuteAsync(server, "UPDATE clubs SET disabled = true WHERE id = @clubId", new { clubId });
        try
        {
            Assert.Equal("429 source", await OutcomeAsync(ArenaPin, "203.0.113.70", code: arena));
            Assert.Equal("401", await OutcomeAsync(ArenaPin, "203.0.113.71", code: arena));
            Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM staff_login_failures WHERE club_id = @empty AND source = 'net:203.0.113.71'",
                new { empty = Guid.Empty }));
        }
        finally
        {
            await Players.ExecuteAsync(server, "UPDATE clubs SET disabled = false WHERE id = @clubId", new { clubId });
        }

        Assert.Equal("200", await OutcomeAsync(ArenaPin, "203.0.113.72", code: arena));
    }

    [Fact]
    public async Task A_device_id_that_is_not_one_is_ignored_and_a_right_pin_with_a_good_one_trusts_the_browser()
    {
        await ResetAsync();
        foreach (var malformed in new object[] { "short", new string('a', 87), "a b c d e f g h i j k l m n", "ä" + NewDevice(), 12345, new { id = NewDevice() } })
        {
            Assert.Equal("200", await OutcomeAsync(OwnerPin, "203.0.113.80", malformed));
        }

        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM staff_devices"));

        var device = NewDevice();
        Assert.Equal("200", await OutcomeAsync(OwnerPin, "203.0.113.80", device));
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM staff_devices WHERE device_hash = sha256(convert_to(@device, 'UTF8'))", new { device }));
        Assert.Equal("401", await OutcomeAsync(WrongPin, "203.0.113.80", device));
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM staff_login_failures WHERE trusted AND source LIKE 'dev:%'"));
    }

    [Fact]
    public async Task Maintenance_drops_failures_past_every_window_and_browsers_no_longer_trusted()
    {
        await ResetAsync();
        var now = server.Clock.GetUtcNow();
        var club = await ClubIdAsync();
        foreach (var (source, at) in new[] { ("net:old", now - PastClubWindow), ("net:fresh", now.AddHours(-23)) })
        {
            await Players.ExecuteAsync(server, "INSERT INTO staff_login_failures (club_id, source, trusted, at) VALUES (@club, @source, false, @at)", new { club, source, at });
        }

        foreach (var (hash, at) in new[] { (new byte[] { 1 }, now.AddDays(-61)), (new byte[] { 2 }, now.AddDays(-59)) })
        {
            await Players.ExecuteAsync(server,
                "INSERT INTO staff_devices (club_id, device_hash, first_login_at, last_login_at) VALUES (@club, @hash, @at, @at)", new { club, hash, at });
        }

        await server.Services.GetRequiredService<MaintenanceWorker>().RunOnceAsync();
        Assert.Equal("net:fresh", await Players.ScalarAsync<string>(server, "SELECT string_agg(source, ',') FROM staff_login_failures"));
        Assert.Equal(new byte[] { 2 }, await Players.ScalarAsync<byte[]>(server, "SELECT device_hash FROM staff_devices"));
    }

    /// <summary>No failures, trusted browsers or webhook deliveries; the bootstrap club has a webhook for <c>suspicious</c>.</summary>
    private async Task ResetAsync()
    {
        await Players.ExecuteAsync(server,
            """
            DELETE FROM staff_login_failures;
            DELETE FROM staff_devices;
            DELETE FROM webhook_outbox;
            INSERT INTO webhooks (club_id, id, position, url, events, enabled)
            SELECT id, 'pin-alert', 0, 'https://hooks.example/pin', ARRAY['suspicious'], true FROM clubs ORDER BY created_at, id LIMIT 1
            ON CONFLICT DO NOTHING;
            """);
    }

    private async Task<Guid> ClubIdAsync() => await Players.ScalarAsync<Guid>(server, "SELECT id FROM clubs WHERE code = @code", new { code = await CodeAsync() });

    /// <summary>The bootstrap club's code: a test may add a second club, and a PIN alone is then 400.</summary>
    private async Task<string> CodeAsync() =>
        _code ??= await Players.ScalarAsync<string>(server, "SELECT code FROM clubs ORDER BY created_at, id LIMIT 1");

    /// <summary>A second club, «ARENA» (owner PIN <see cref="ArenaPin"/>), made once through the platform administration; its code.</summary>
    private async Task<string> ArenaAsync()
    {
        if (await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM clubs WHERE code = 'ARENA'") == 0)
        {
            // A later created_at keeps the bootstrap club the first one.
            server.Clock.Advance(TimeSpan.FromSeconds(1));
            using var client = server.CreateDefaultClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/platform/clubs")
            {
                Content = JsonBody(new { name = "Arena", code = "ARENA", timeZone = "Asia/Tashkent", ownerName = "Владелец Arena", ownerPin = ArenaPin }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StaffLoginFixture.PlatformKey);
            using var response = await client.SendAsync(request);
            await Players.ReadAsync(response, 201);
        }

        return "ARENA";
    }

    private Task<int> FailuresAsync() => Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM staff_login_failures");

    private Task<int> AlertsAsync() => Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM webhook_outbox WHERE event = 'suspicious'");

    private static string NewDevice() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The token of a sign-in that must succeed.</summary>
    private async Task<string> TokenAsync(string pin, string ip, string device)
    {
        var (status, body) = await AttemptAsync(pin, ip, device);
        Assert.True(status == 200, $"sign-in: {status} {body}");
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>A new cashier of the bootstrap club; their id.</summary>
    private async Task<string> AddCashierAsync(string owner, string name, string pin) =>
        (await ExpectAsync(server, 200, HttpMethod.Post, "/staff", owner, new { name, role = "cashier", pin })).GetProperty("id").GetString()!;

    private async Task<string> StaffIdAsync(string token) =>
        (await ExpectAsync(server, 200, HttpMethod.Get, "/me", token)).GetProperty("staff").GetProperty("id").GetString()!;

    /// <summary><see cref="AttemptAsync"/> in short: <c>200</c>, <c>401</c> (invalidPin) or <c>429 &lt;scope&gt;</c>.</summary>
    private async Task<string> OutcomeAsync(string pin, string ip, object? device = null, string? code = null, WebApplicationFactory<Program>? client = null)
    {
        var (status, body) = await AttemptAsync(pin, ip, device, code, client);
        return status switch
        {
            200 => "200",
            401 when body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString() == "invalidPin" => "401",
            429 => "429 " + body.GetProperty("error").GetProperty("details").GetProperty("scope").GetString(),
            _ => $"{status} {body}",
        };
    }

    /// <summary>
    /// One sign-in from <paramref name="ip"/> (<c>X-Real-IP</c>) with the bootstrap club's code unless another is given ("" —
    /// none), through a client without the contract validator (its 429 is not in the contract yet); a 429 carries
    /// <c>Retry-After</c> equal to <c>retryAfterSec</c>.
    /// </summary>
    private async Task<(int Status, JsonElement Body)> AttemptAsync(
        string pin, string ip, object? device = null, string? code = null, WebApplicationFactory<Program>? client = null)
    {
        using var http = (client ?? server).CreateDefaultClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/login")
        {
            Content = JsonBody(new { pin, clubCode = code ?? await CodeAsync(), deviceId = device }),
        };
        request.Headers.Add("X-Real-IP", ip);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if ((int)response.StatusCode == 429)
        {
            Contract.AssertError(body, "rateLimited");
            Assert.Equal(body.GetProperty("error").GetProperty("details").GetProperty("retryAfterSec").GetInt32().ToString(), response.Headers.RetryAfter!.ToString());
        }

        return ((int)response.StatusCode, body);
    }
}
