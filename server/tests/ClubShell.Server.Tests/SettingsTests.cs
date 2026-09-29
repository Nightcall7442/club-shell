using System.Text.Json;
using ClubShell.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The club settings document (slice S5, DESIGN §4.2): read by every staff member without secrets, written by the owner;
/// the contract's JSON Schema → <c>400</c> with the failing field; <c>control</c> and the health thresholds clamped, absent
/// values kept; server counters (promo <c>used</c>/<c>usesLeft</c>, rule <c>fired</c>, webhook <c>lastStatus</c>) not
/// overwritten by a save; the owner's API key hashed at rest, shown by <c>adminApiKey</c>, replaced at once by the rotation.
/// </summary>
public sealed class SettingsTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Every_staff_member_reads_the_whole_document_without_secrets_and_with_defaults()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var doc = await ExpectAsync(Server, 200, HttpMethod.Get, "/club", cashier);
        Assert.Equal(
            ["branding", "features", "zones", "pricing", "groups", "bonusTiers", "promoCodes", "happyHours", "loyalty", "limits", "catalog", "banners",
             "rulesText", "stock", "automation", "notifications", "webhooks", "control", "events"],
            doc.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("telegram", doc.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ClubShell", doc.GetProperty("branding").GetProperty("clubName").GetString());
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("branding").GetProperty("logoUrl").ValueKind);
        Assert.Equal(["bigTopupAt"], doc.GetProperty("notifications").EnumerateObject().Select(p => p.Name));
        Assert.Equal(("staff", 50), (doc.GetProperty("groups")[3].GetProperty("id").GetString(), doc.GetProperty("groups")[3].GetProperty("discountPct").GetInt32()));
        Assert.Equal(8, doc.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task A_body_outside_the_schema_is_400_with_the_field()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var version = await Players.ScalarAsync<int>(Server, "SELECT settings_version FROM clubs");
        foreach (var (body, field, reason) in new (object, string, string)[]
        {
            (new { zones = new[] { new { name = "VIP" } } }, "zones[0].color", "required"),
            (new { pricing = new { weekdayPct = new[] { 100 }, holidays = Array.Empty<string>(), holidayPct = 100 } }, "pricing.weekdayPct", "min"),
            (new { branding = new { clubName = 5, accent = "#fff", logoUrl = (string?)null, wallpaperUrl = (string?)null } }, "branding.clubName", "format"),
            (new { limits = new { minorAge = 18, minorCurfew = "7pm" } }, "limits.minorCurfew", "format"),
            (new { groups = new[] { new { id = "g", name = "G", discountPct = 101, color = "#000000" } } }, "groups[0].discountPct", "max"),
            (new { pricing = new { weekdayPct = Enumerable.Repeat(100, 7), holidays = new[] { "31.12.2026" }, holidayPct = 100 } }, "pricing.holidays[0]", "format"),
            (new { webhooks = new[] { new { id = "w1", url = "https://example.com/h", events = new[] { "birthday" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null } } },
                "webhooks[0].events[0]", "enum"),
            (new { automation = new[] { new { name = "R", enabled = true, trigger = new { kind = "sunrise", value = 1 }, action = new { kind = "message", text = "x" } } } },
                "automation[0].trigger.kind", "enum"),
            (new { automation = new[] { new { name = "R", enabled = true, trigger = new { kind = "minutesLeft" }, action = new { kind = "message", text = "x" } } } },
                "automation[0].trigger.value", "required"),
            (new { promoCodes = new[] { Promo("DUP"), Promo("dup") } }, "promoCodes[1].code", "taken"),
            (new { promoCodes = new[] { Promo(new string('X', 33)) } }, "promoCodes[0].code", "max"),
        })
        {
            var error = await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner, body);
            Assert.Equal((field, reason), StaffAdminTests.Details(error));
        }

        Assert.Equal(("body", "schema"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner, "[1]")));
        Assert.Equal(version, await Players.ScalarAsync<int>(Server, "SELECT settings_version FROM clubs"));
    }

    [Fact]
    public async Task A_save_keeps_the_server_counters_of_promo_codes_rules_and_webhooks()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var player = await Players.CreateAsync(Server, balance: 0);
        var rule = new { id = "r-topup", name = "Бонус за пополнение", enabled = true, trigger = new { kind = "topupAtLeast", value = 1_000_000 }, action = new { kind = "notifyOwner", text = "!" } };
        var hook = new { id = "w-1", url = "https://hooks.example.com/club", events = new[] { "bigTopup" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null };
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            promoCodes = new object[] { Promo("SPRING", usesLeft: 5), Promo("WELCOME", usesLeft: 100) },
            automation = new object[] { rule, new { name = "Без id", enabled = false, trigger = new { kind = "sessionStarted" }, action = new { kind = "lockPc" } } },
            webhooks = new[] { hook },
        });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/promo/redeem", owner, new { userId = player.Id, code = "spring" });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = player.Id, amount = 2_000_000, method = "card" });
        await Players.ExecuteAsync(Server, "UPDATE webhooks SET last_status = 204, last_at = now()");

        // The console sends back what it read before, counters included: the stored counters win.
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            promoCodes = new object[] { Promo("spring", usesLeft: 5, value: 700_000) },
            automation = new object[] { new { rule.id, rule.name, rule.enabled, rule.trigger, rule.action, fired = 0, lastFiredAt = (string?)null } },
            webhooks = new[] { hook with { url = "https://hooks.example.com/v2" } },
        });
        var doc = await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner);
        var promo = Assert.Single(doc.GetProperty("promoCodes").EnumerateArray());
        Assert.Equal(("spring", 700_000, 4, 1), (promo.GetProperty("code").GetString(), promo.GetProperty("value").GetInt64(), promo.GetProperty("usesLeft").GetInt32(),
            promo.GetProperty("used").GetInt32()));
        var saved = Assert.Single(doc.GetProperty("automation").EnumerateArray());
        Assert.Equal(("r-topup", 1), (saved.GetProperty("id").GetString(), saved.GetProperty("fired").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, saved.GetProperty("lastFiredAt").ValueKind);
        var webhook = Assert.Single(doc.GetProperty("webhooks").EnumerateArray());
        Assert.Equal(("https://hooks.example.com/v2", 204), (webhook.GetProperty("url").GetString(), webhook.GetProperty("lastStatus").GetInt32()));

        // WELCOME was dropped: soft-deleted, its redemptions stay; the rule without an id got one.
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM promo_codes WHERE upper(code) = 'WELCOME' AND deleted_at IS NOT NULL"));
    }

    [Fact]
    public async Task The_api_key_is_owner_only_hashed_at_rest_and_a_rotation_replaces_it_at_once()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        using (var refused = await Server.Http.SendAsync(Request(HttpMethod.Get, "/club/api-key", cashier)))
        {
            await Contract.ReadErrorAsync(refused, 403, "forbidden", "ownerOnly");
        }

        var first = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club/api-key", owner)).GetProperty("apiKey").GetString()!;
        Assert.Matches("^ck_[0-9a-f]{32}$", first);
        Assert.Equal(first, (await ExpectAsync(Server, 200, HttpMethod.Get, "/club/api-key", owner)).GetProperty("apiKey").GetString());
        var stored = await Players.ScalarAsync<string>(Server, "SELECT encode(api_key_hash, 'hex') || encode(api_key_sealed, 'hex') FROM clubs");
        Assert.DoesNotContain(first[3..], stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(first)), stored, StringComparison.Ordinal);
        await ExpectAsync(Server, 200, HttpMethod.Get, "/club", first);

        using (var rotate = await Server.Http.SendAsync(Request(HttpMethod.Post, "/club/api-key", first, new { })))
        {
            Assert.Equal("no-store", rotate.Headers.CacheControl?.ToString());
            var second = (await Players.ReadAsync(rotate, 200)).GetProperty("apiKey").GetString()!;
            Assert.NotEqual(first, second);
            using (var old = await Server.Http.SendAsync(Request(HttpMethod.Get, "/me", first)))
            {
                await Contract.ReadErrorAsync(old, 401, "unauthorized", "invalid");
            }

            Assert.Equal(second, (await ExpectAsync(Server, 200, HttpMethod.Get, "/club/api-key", second)).GetProperty("apiKey").GetString());
        }

        Assert.Equal("API key", await Players.ScalarAsync<string>(Server, "SELECT staff_name FROM audit_entries WHERE action = 'apiKeyRotate' ORDER BY at DESC LIMIT 1"));
        await ClearApiKeyAsync(Server);
    }

    private static object Promo(string code, int? usesLeft = 10, long value = 500_000) =>
        new { code, kind = "bonus", value, usesLeft, expiresAt = (string?)null, used = 0 };
}

/// <summary>Thresholds (DESIGN §4.2, contract <c>x-clamp</c>): its own club, since they change what the other tests read.</summary>
public sealed class SettingsThresholdTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private ServerFixture Server => server;

    [Fact]
    public async Task Control_and_health_thresholds_are_rounded_clamped_and_absent_ones_kept()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { control = new { earlyEndMinutes = 500, discountPct = 0.4, shortfallFrom = -5, sameClientTopups = 4.5 } });
        var control = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("control");
        Assert.Equal("{\"earlyEndMinutes\":120,\"earlyEndsPerShift\":3,\"discountPct\":1,\"sameClientTopups\":5,\"shortfallFrom\":0}", control.GetRawText());
        Assert.Equal(("control.earlyEndMinutes", "format"),
            StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner, new { control = new { earlyEndMinutes = "ten" } })));

        // Telegram fields of an older console are accepted and dropped; a missing bigTopupAt keeps the stored one.
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { notifications = new { bigTopupAt = 5_000_000, telegramBotToken = "123:abc", telegramChatId = "42" } });
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { notifications = new { telegramChatId = "43" } });
        var doc = await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner);
        Assert.Equal("{\"bigTopupAt\":5000000}", doc.GetProperty("notifications").GetRawText());
        Assert.DoesNotContain("telegram", await Players.ScalarAsync<string>(Server, "SELECT settings::text FROM clubs"), StringComparison.OrdinalIgnoreCase);

        var saved = (await ExpectAsync(Server, 200, HttpMethod.Patch, "/health/settings", owner, new { cpuHotC = 200, trendC = 2.5, autoMaintenance = true })).GetProperty("settings");
        Assert.Equal("{\"cpuHotC\":110,\"gpuHotC\":85,\"trendC\":3,\"fpsDropPct\":30,\"offlinePerDay\":3,\"autoMaintenance\":true}", saved.GetRawText());
        saved = (await ExpectAsync(Server, 200, HttpMethod.Patch, "/health/settings", owner, new { offlinePerDay = 0 })).GetProperty("settings");
        Assert.Equal((110, 1, true), (saved.GetProperty("cpuHotC").GetInt32(), saved.GetProperty("offlinePerDay").GetInt32(), saved.GetProperty("autoMaintenance").GetBoolean()));
        Assert.Equal(("autoMaintenance", "format"),
            StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/health/settings", owner, new { autoMaintenance = "yes" })));

        var cashier = await LoginAsync(Server, CashierPin);
        foreach (var path in new[] { "/club", "/health/settings" })
        {
            // Owner only, before any field check.
            using var refused = await Server.Http.SendAsync(Request(HttpMethod.Patch, path, cashier, new { cpuHotC = "x", zones = 1 }));
            await Contract.ReadErrorAsync(refused, 403, "forbidden", "ownerOnly");
        }
    }
}

/// <summary>
/// Version bumps of a settings save (DESIGN §5.9): a changed <c>branding</c> bumps <c>config_version</c> and sends the
/// connected PC <c>refreshConfig {config:true}</c>, a changed <c>catalog</c> also <c>catalog_version</c> and <c>games:true</c>;
/// an unchanged section or one outside the agent config bumps nothing and sends nothing. The agent's <c>shell.club</c>
/// follows branding, live banners and rules.
/// </summary>
public sealed class SettingsRefreshTests(KestrelServerFixture server) : IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task Config_and_catalog_bumps_send_refreshConfig_with_explicit_flags_to_connected_pcs()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var agent = await TestAgent.CreateAsync(server);
        var offline = await TestAgent.CreateAsync(server);
        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        await Wait.UntilAsync(() => server.Services.GetRequiredService<AgentSocketHub>().IsConnected(agent.PcId));
        var versions = async () => await Players.ScalarAsync<string>(server, "SELECT config_version || '/' || catalog_version FROM clubs");
        var before = await versions();

        var branding = new { clubName = "Arena", accent = "#FF0000", logoUrl = "https://cdn.example.com/logo.png", wallpaperUrl = (string?)null };
        await ExpectAsync(server, 200, HttpMethod.Patch, "/club", owner, new { branding });
        var frame = await socket.ReceiveAsync();
        Contract.AssertMessage("commandRefreshConfig", frame);
        Assert.Equal("{\"config\":true}", frame.GetProperty("payload").GetRawText());

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd");
        var game = Guid.NewGuid();
        await ExpectAsync(server, 200, HttpMethod.Patch, "/club", owner, new
        {
            branding, // unchanged
            catalog = new { order = new[] { game }, hidden = Array.Empty<Guid>(), featured = Array.Empty<Guid>() },
            banners = new[]
            {
                new { id = "b1", title = "Турнир", imageUrl = "https://cdn.example.com/b1.png", from = (string?)today, to = (string?)null, enabled = true },
                new { id = "b2", title = "Скрыт", imageUrl = "https://cdn.example.com/b2.png", from = (string?)null, to = (string?)null, enabled = false },
                new { id = "b3", title = "Прошёл", imageUrl = "https://cdn.example.com/b3.png", from = (string?)null, to = (string?)"2020-01-01", enabled = true },
            },
            rulesText = new { ru = "Правила", uz = "Qoidalar", en = "Rules" },
        });
        frame = await socket.ReceiveAsync();
        Assert.Equal("{\"config\":true,\"games\":true}", frame.GetProperty("payload").GetRawText());
        var (config, catalog) = (int.Parse(before.Split('/')[0]), int.Parse(before.Split('/')[1]));
        Assert.Equal($"{config + 2}/{catalog + 1}", await versions());

        // Outside the agent config: no bump, nothing sent.
        await ExpectAsync(server, 200, HttpMethod.Patch, "/club", owner, new { zones = new[] { new { name = "VIP", color = "#F2B84B" } }, branding });
        Assert.Equal($"{config + 2}/{catalog + 1}", await versions());
        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM agent_commands WHERE pc_id = @PcId", new { offline.PcId }));

        var club = (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("config")), 200)).GetProperty("shell").GetProperty("club");
        Assert.Equal(("Arena", "#FF0000", "https://cdn.example.com/logo.png"),
            (club.GetProperty("name").GetString(), club.GetProperty("accent").GetString(), club.GetProperty("logoUrl").GetString()));
        Assert.Equal(["b1"], club.GetProperty("banners").EnumerateArray().Select(b => b.GetProperty("id").GetString()));
        Assert.Equal("Qoidalar", club.GetProperty("rules").GetProperty("uz").GetString());
        Assert.False(club.TryGetProperty("wallpaperUrl", out _));
    }
}
