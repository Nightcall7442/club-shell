using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Contracts.Commands;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>Thresholds of the cashier-control rules (<c>AdminControlSettings</c>), always inside their ranges.</summary>
public sealed record ControlSettings(int EarlyEndMinutes, int EarlyEndsPerShift, int DiscountPct, int SameClientTopups, long ShortfallFrom)
{
    /// <summary>The mock's defaults (<c>club.ts DEFAULT_CONTROL</c>).</summary>
    public static readonly ControlSettings Default = new(10, 3, 30, 3, 500_000);
}

/// <summary>
/// The club settings document of the console (slice S5, DESIGN §4.2): <c>adminSettings</c> for every staff member (no
/// secrets in it: no <c>apiKey</c>, no Telegram fields), <c>adminSaveSettings</c> for the owner, and the owner's club API
/// key (<c>adminApiKey</c>, <c>adminRotateApiKey</c>). Plain sections live in <c>clubs.settings</c> as the console sent them
/// (a missing one reads as its default: what the server applies without it); the arrays with server counters live in
/// tables — <c>promoCodes</c>, <c>automation</c>, <c>webhooks</c> — and are synchronized by code / id, keeping the
/// counters of the entries that stay (<c>used</c>/<c>usesLeft</c>, <c>fired</c>/<c>lastFiredAt</c>,
/// <c>lastStatus</c>/<c>lastAt</c>; a new entry starts from what was sent). A body that does not match
/// <c>AdminClubSettingsPatch</c> is <c>400 validation</c>; <c>control</c> numbers are rounded and clamped, absent ones kept.
/// The save holds the <c>clubs</c> row <c>FOR UPDATE</c>, which also serializes it with a promo redeem (§4.4). A changed
/// <c>features</c>/<c>branding</c>/<c>catalog</c>/<c>rulesText</c>/<c>banners</c> bumps <c>config_version</c> (a changed
/// <c>catalog</c> also <c>catalog_version</c>) and sends <c>refreshConfig</c> with explicit flags to the connected PCs (§5.9).
/// </summary>
public static class ClubSettingsEndpoints
{
    public static readonly string[] Operations = ["adminSettings", "adminSaveSettings", "adminApiKey", "adminRotateApiKey"];

    /// <summary>Sections stored as sent, in the order of <c>AdminClubSettings</c>.</summary>
    private static readonly string[] Plain =
        ["branding", "features", "zones", "pricing", "groups", "bonusTiers", "happyHours", "loyalty", "limits", "catalog", "banners", "rulesText", "stock"];

    /// <summary>Sections whose change reaches <c>AgentServerConfig</c> (§5.9).</summary>
    private static readonly string[] ConfigSections = ["features", "branding", "catalog", "rulesText", "banners"];

    /// <summary><c>notifications.bigTopupAt</c> when the owner has not set one (the mock's default, 200 000 sum).</summary>
    public const long DefaultBigTopupAt = 20_000_000;

    public static void MapClubSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/club");
        api.MapGet("", GetAsync).WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapPatch("", SaveAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapGet("/api-key", ApiKeyAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapPost("/api-key", RotateApiKeyAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
    }

    /// <summary><c>settings.control</c> of the club, defaults for what is unset.</summary>
    public static async Task<ControlSettings> ControlAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid clubId) =>
        ControlOf(await c.ExecuteScalarAsync<string?>("SELECT (settings -> 'control')::text FROM clubs WHERE id = @clubId", new { clubId }, tx));

    /// <summary><c>settings.notifications.bigTopupAt</c> of the club (event <c>bigTopup</c>, control flag <c>bigCash</c>).</summary>
    public static async Task<long> BigTopupAtAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid clubId) =>
        await c.ExecuteScalarAsync<long?>("SELECT (settings -> 'notifications' ->> 'bigTopupAt')::bigint FROM clubs WHERE id = @clubId", new { clubId }, tx)
        ?? DefaultBigTopupAt;

    private static ControlSettings ControlOf(string? json)
    {
        var d = ControlSettings.Default;
        if (string.IsNullOrEmpty(json))
        {
            return d;
        }

        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        return new ControlSettings(
            (int)Long(e, "earlyEndMinutes", d.EarlyEndMinutes), (int)Long(e, "earlyEndsPerShift", d.EarlyEndsPerShift), (int)Long(e, "discountPct", d.DiscountPct),
            (int)Long(e, "sameClientTopups", d.SameClientTopups), Long(e, "shortfallFrom", d.ShortfallFrom));

        static long Long(JsonElement e, string name, long fallback) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : fallback;
    }

    private static async Task<IResult> GetAsync(HttpContext context, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        await using var c = await db.OpenConnectionAsync();
        var (name, settings) = await c.QuerySingleAsync<(string, string)>("SELECT name, settings::text FROM clubs WHERE id = @ClubId", new { staff.ClubId });
        var stored = JsonNode.Parse(settings)!.AsObject();
        var ordered = new JsonObject();
        foreach (var key in Plain)
        {
            ordered[key] = stored[key]?.DeepClone() ?? Default(key, name);
            if (key == "bonusTiers")
            {
                ordered["promoCodes"] = await PromoCodesAsync(c, staff.ClubId);
            }
        }

        ordered["automation"] = await AutomationAsync(c, staff.ClubId);
        ordered["notifications"] = new JsonObject { ["bigTopupAt"] = stored["notifications"]?["bigTopupAt"]?.DeepClone() ?? DefaultBigTopupAt };
        ordered["webhooks"] = await WebhooksAsync(c, staff.ClubId);
        var control = ControlOf(stored["control"]?.ToJsonString());
        ordered["control"] = JsonSerializer.SerializeToNode(control, AdminJson.Options);
        ordered["events"] = new JsonArray([.. Webhooks.Events.Select(e => (JsonNode)e)]);
        return Results.Text(ordered.ToJsonString(AdminJson.Options), "application/json; charset=utf-8");
    }

    /// <summary>A section the owner never saved: what the server applies without it.</summary>
    private static JsonNode Default(string key, string clubName) => key switch
    {
        "branding" => new JsonObject { ["clubName"] = clubName, ["accent"] = "#9ADFFF", ["logoUrl"] = null, ["wallpaperUrl"] = null },

        // What the agents get (§5.9): the owner's choice is stored, but these are the features v1 serves.
        "features" => new JsonObject
        {
            ["shop"] = false, ["chat"] = false, ["booking"] = false, ["tournaments"] = false, ["profile"] = true, ["topup"] = false, ["apps"] = false,
            ["callAdmin"] = false, ["gpuPanel"] = false,
        },
        "pricing" => new JsonObject { ["weekdayPct"] = new JsonArray(100, 100, 100, 100, 100, 100, 100), ["holidays"] = new JsonArray(), ["holidayPct"] = 100 },
        "limits" => new JsonObject { ["minorAge"] = 18, ["minorCurfew"] = "22:00" },
        "catalog" => new JsonObject { ["order"] = new JsonArray(), ["hidden"] = new JsonArray(), ["featured"] = new JsonArray() },
        "rulesText" => new JsonObject { ["ru"] = "", ["uz"] = "", ["en"] = "" },
        "stock" => new JsonObject { ["lowAt"] = StockEndpoints.DefaultLowAt },
        _ => new JsonArray(),
    };

    private static async Task<JsonArray> PromoCodesAsync(NpgsqlConnection c, Guid clubId) =>
        new([.. (await c.QueryAsync<(string Code, string Kind, long Value, int? UsesLeft, DateTimeOffset? ExpiresAt, int Used)>(
                "SELECT code, kind, value, uses_left, expires_at, used FROM promo_codes WHERE club_id = @clubId AND deleted_at IS NULL ORDER BY created_at, id",
                new { clubId }))
            .Select(p => (JsonNode)new JsonObject
            {
                ["code"] = p.Code, ["kind"] = p.Kind, ["value"] = p.Value, ["usesLeft"] = p.UsesLeft,
                ["expiresAt"] = p.ExpiresAt is { } at ? ServerJson.FormatTime(at) : null, ["used"] = p.Used,
            })]);

    private static async Task<JsonArray> AutomationAsync(NpgsqlConnection c, Guid clubId) =>
        new([.. (await c.QueryAsync<(string Id, string Name, bool Enabled, string Trigger, string Action, int Fired, DateTimeOffset? LastFiredAt)>(
                "SELECT id, name, enabled, trigger::text, action::text, fired, last_fired_at FROM automation_rules WHERE club_id = @clubId ORDER BY position",
                new { clubId }))
            .Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["name"] = r.Name, ["enabled"] = r.Enabled, ["trigger"] = JsonNode.Parse(r.Trigger), ["action"] = JsonNode.Parse(r.Action),
                ["fired"] = r.Fired, ["lastFiredAt"] = r.LastFiredAt is { } at ? ServerJson.FormatTime(at) : null,
            })]);

    private static async Task<JsonArray> WebhooksAsync(NpgsqlConnection c, Guid clubId) =>
        new([.. (await c.QueryAsync<(string Id, string Url, string[] Events, bool Enabled, int? LastStatus, DateTimeOffset? LastAt)>(
                "SELECT id, url, events, enabled, last_status, last_at FROM webhooks WHERE club_id = @clubId ORDER BY position", new { clubId }))
            .Select(w => (JsonNode)new JsonObject
            {
                ["id"] = w.Id, ["url"] = w.Url, ["events"] = new JsonArray([.. w.Events.Select(e => (JsonNode)e)]), ["enabled"] = w.Enabled,
                ["lastStatus"] = w.LastStatus, ["lastAt"] = w.LastAt is { } at ? ServerJson.FormatTime(at) : null,
            })]);

    private static async Task<IResult> SaveAsync(
        HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("body", "schema", "JSON object expected");
        }

        ContractSchemas.Validate("AdminClubSettingsPatch", body);
        CheckIds(body);

        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued = [];
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await c.ExecuteAsync("SET LOCAL lock_timeout = '10s'", transaction: tx);
            var now = clock.GetUtcNow();
            var (settingsJson, timeZone) = await c.QuerySingleAsync<(string, string)>(
                "SELECT settings::text, time_zone FROM clubs WHERE id = @ClubId FOR UPDATE", new { staff.ClubId }, tx);
            var settings = JsonNode.Parse(settingsJson)!.AsObject();
            var changed = new List<string>();
            foreach (var key in Plain.Where(k => body.TryGetProperty(k, out _)))
            {
                var next = JsonNode.Parse(body.GetProperty(key).GetRawText());
                if (!JsonNode.DeepEquals(settings[key], next))
                {
                    changed.Add(key);
                }

                settings[key] = next;
            }

            if (body.TryGetProperty("control", out var control))
            {
                settings["control"] = JsonSerializer.SerializeToNode(Clamp(control, ControlOf(settings["control"]?.ToJsonString())), AdminJson.Options);
            }

            if (body.TryGetProperty("notifications", out var notifications))
            {
                // Only bigTopupAt: the Telegram fields an older console sends are accepted and dropped (x-prohibited).
                var bigTopupAt = notifications.TryGetProperty("bigTopupAt", out var n) && n.TryGetInt64(out var value)
                    ? value
                    : settings["notifications"]?["bigTopupAt"]?.GetValue<long>() ?? DefaultBigTopupAt;
                settings["notifications"] = new JsonObject { ["bigTopupAt"] = bigTopupAt };
            }

            if (body.TryGetProperty("promoCodes", out var promoCodes))
            {
                await SyncPromoCodesAsync(c, tx, staff.ClubId, promoCodes, now);
            }

            if (body.TryGetProperty("automation", out var automation))
            {
                await SyncAutomationAsync(c, tx, staff.ClubId, automation);
            }

            if (body.TryGetProperty("webhooks", out var webhooks))
            {
                await SyncWebhooksAsync(c, tx, staff.ClubId, webhooks);
            }

            var config = changed.Intersect(ConfigSections).Any();
            var catalog = changed.Contains("catalog");
            var stored = settings.ToJsonString();
            await c.ExecuteAsync(
                """
                UPDATE clubs SET settings = @stored::jsonb, settings_version = settings_version + 1, config_version = config_version + @config,
                                 catalog_version = catalog_version + @catalog, banners_hash = @banners, updated_at = @now
                WHERE id = @ClubId
                """,
                new
                {
                    staff.ClubId, stored = ServerJson.Jsonb(stored), config = config ? 1 : 0, catalog = catalog ? 1 : 0, now,
                    banners = BannersHash(stored, DateOnly.FromDateTime(ClubTime.Local(now, timeZone))),
                },
                tx);
            var keys = string.Join(",", body.EnumerateObject().Select(p => p.Name).Where(k => k is not ("apiKey" or "events")));
            await Audit.WriteAsync(c, tx, staff, now, "settingsSave", detail: keys, meta: new { keys, configChanged = config, catalogChanged = catalog });
            if (config)
            {
                queued = await ConfigRefresh.QueueAsync(c, tx, commands, hub, staff.ClubId,
                    new RefreshConfigCommand(Config: true, Games: catalog ? true : null), staff.StaffId);
            }

            await tx.CommitAsync();
        }

        await ConfigRefresh.SendAsync(commands, queued);
        return AdminJson.Ok(AdminJson.OkBody);
    }

    /// <summary>The live banner set as the <see cref="ClubTickWorker"/> compares it (OQ-21).</summary>
    public static string BannersHash(string? settingsJson, DateOnly today) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(AgentConfig.LiveBanners(settingsJson, today)))));

    /// <summary><c>AdminControlSettingsPatch</c>: numbers rounded (half up) and clamped to <c>x-clamp</c>, absent ones kept.</summary>
    private static ControlSettings Clamp(JsonElement patch, ControlSettings current) => new(
        (int)Clamped(patch, "earlyEndMinutes", current.EarlyEndMinutes, 1, 120),
        (int)Clamped(patch, "earlyEndsPerShift", current.EarlyEndsPerShift, 1, 50),
        (int)Clamped(patch, "discountPct", current.DiscountPct, 1, 100),
        (int)Clamped(patch, "sameClientTopups", current.SameClientTopups, 2, 50),
        Clamped(patch, "shortfallFrom", current.ShortfallFrom, 0, 1_000_000_000));

    /// <summary>A number of a clamped patch (<c>x-clamp</c>): JS <c>Math.round</c>, then into [min, max]; absent — <paramref name="current"/>.</summary>
    public static long Clamped(JsonElement patch, string name, long current, long min, long max) =>
        patch.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
            ? (long)Math.Clamp(Math.Floor(d + 0.5), min, max)
            : current;

    /// <summary>
    /// Keys the schema cannot see: a promo code is 1–32 characters and unique ignoring case; rule and webhook ids unique
    /// (an empty rule id is assigned by the server) and at most 64 characters — <c>400 max</c> / <c>taken</c> on the entry.
    /// </summary>
    private static void CheckIds(JsonElement body)
    {
        if (body.TryGetProperty("promoCodes", out var promos))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, i) in promos.EnumerateArray().Select((p, i) => (p, i)))
            {
                var code = p.GetProperty("code").GetString()!;
                if (code.Length is 0 or > 32)
                {
                    throw ApiException.Validation($"promoCodes[{i}].code", code.Length == 0 ? "required" : "max");
                }

                if (!seen.Add(code.ToUpperInvariant()))
                {
                    throw ApiException.Validation($"promoCodes[{i}].code", "taken");
                }
            }
        }

        foreach (var key in new[] { "automation", "webhooks" })
        {
            if (!body.TryGetProperty(key, out var list))
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (item, i) in list.EnumerateArray().Select((x, i) => (x, i)))
            {
                var id = item.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                if (id.Length == 0 && key == "automation")
                {
                    continue;
                }

                if (id.Length is 0 or > 64)
                {
                    throw ApiException.Validation($"{key}[{i}].id", id.Length == 0 ? "required" : "max");
                }

                if (!seen.Add(id))
                {
                    throw ApiException.Validation($"{key}[{i}].id", "taken");
                }
            }
        }
    }

    /// <summary>
    /// <c>promoCodes</c> by <c>upper(code)</c>: a kept code gets the new kind, value and expiry but keeps its <c>used</c> and
    /// <c>usesLeft</c> (server counters); a new one starts from the sent ones; a dropped one is soft-deleted (its
    /// redemptions stay).
    /// </summary>
    private static async Task SyncPromoCodesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, JsonElement promoCodes, DateTimeOffset now)
    {
        var live = (await c.QueryAsync<(Guid Id, string Code)>(
                "SELECT id, code FROM promo_codes WHERE club_id = @clubId AND deleted_at IS NULL", new { clubId }, tx))
            .ToDictionary(p => p.Code.ToUpperInvariant(), p => p.Id, StringComparer.Ordinal);
        var kept = new HashSet<Guid>();
        foreach (var (p, i) in promoCodes.EnumerateArray().Select((p, i) => (p, i)))
        {
            var code = p.GetProperty("code").GetString()!;
            var kind = p.GetProperty("kind").GetString()!;
            var value = p.GetProperty("value").GetInt64();
            var expiresAt = p.GetProperty("expiresAt") is { ValueKind: JsonValueKind.String } e
                ? DateTimeOffset.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                : (DateTimeOffset?)null;
            if (live.TryGetValue(code.ToUpperInvariant(), out var id))
            {
                kept.Add(id);
                await c.ExecuteAsync(
                    "UPDATE promo_codes SET code = @code, kind = @kind, value = @value, expires_at = @expiresAt WHERE id = @id AND club_id = @clubId",
                    new { id, clubId, code, kind, value, expiresAt }, tx);
                continue;
            }

            // Ids in the order sent (uuidv7 by millisecond), so the list reads back in that order.
            await c.ExecuteAsync(
                """
                INSERT INTO promo_codes (id, club_id, code, kind, value, uses_left, used, expires_at, created_at)
                VALUES (@id, @clubId, @code, @kind, @value, @usesLeft, @used, @expiresAt, @now)
                """,
                new
                {
                    id = Guid.CreateVersion7(now.AddMilliseconds(i)), clubId, code, kind, value, expiresAt, now,
                    usesLeft = p.GetProperty("usesLeft") is { ValueKind: JsonValueKind.Number } u ? u.GetInt32() : (int?)null,
                    used = p.GetProperty("used").GetInt32(),
                },
                tx);
        }

        var dropped = live.Values.Where(id => !kept.Contains(id)).ToArray();
        await c.ExecuteAsync("UPDATE promo_codes SET deleted_at = @now WHERE club_id = @clubId AND id = ANY(@dropped)", new { clubId, dropped, now }, tx);
    }

    /// <summary>
    /// <c>automation</c> replaces the rules: an empty or absent id gets a UUID; a rule that stays keeps <c>fired</c>/
    /// <c>lastFiredAt</c>, a new one takes the sent ones (0/null when absent, <c>AdminAutomationRuleInput</c>).
    /// </summary>
    private static async Task SyncAutomationAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, JsonElement rules)
    {
        var ids = new List<string>();
        foreach (var (r, i) in rules.EnumerateArray().Select((r, i) => (r, i)))
        {
            var id = r.TryGetProperty("id", out var v) && v.GetString() is { Length: > 0 } given ? given : Guid.NewGuid().ToString();
            ids.Add(id);
            await c.ExecuteAsync(
                """
                INSERT INTO automation_rules (club_id, id, position, name, enabled, trigger, action, fired, last_fired_at)
                VALUES (@clubId, @id, @i, @name, @enabled, @trigger::jsonb, @action::jsonb, @fired, @lastFiredAt)
                ON CONFLICT (club_id, id) DO UPDATE SET position = excluded.position, name = excluded.name, enabled = excluded.enabled,
                                                        trigger = excluded.trigger, action = excluded.action
                """,
                new
                {
                    clubId, id, i, name = r.GetProperty("name").GetString(), enabled = r.GetProperty("enabled").GetBoolean(),
                    trigger = ServerJson.Jsonb(r.GetProperty("trigger").GetRawText()), action = ServerJson.Jsonb(r.GetProperty("action").GetRawText()),
                    fired = r.TryGetProperty("fired", out var f) && f.TryGetInt32(out var n) ? n : 0,
                    lastFiredAt = r.TryGetProperty("lastFiredAt", out var l) && l.ValueKind == JsonValueKind.String
                        ? DateTimeOffset.Parse(l.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                        : (DateTimeOffset?)null,
                },
                tx);
        }

        await c.ExecuteAsync("DELETE FROM automation_rules WHERE club_id = @clubId AND NOT (id = ANY(@ids))", new { clubId, ids = ids.ToArray() }, tx);
    }

    /// <summary><c>webhooks</c> replace the stored ones by id; a kept webhook keeps <c>lastStatus</c>/<c>lastAt</c>.</summary>
    private static async Task SyncWebhooksAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, JsonElement webhooks)
    {
        var ids = new List<string>();
        foreach (var (w, i) in webhooks.EnumerateArray().Select((w, i) => (w, i)))
        {
            var id = w.GetProperty("id").GetString()!;
            ids.Add(id);
            await c.ExecuteAsync(
                """
                INSERT INTO webhooks (club_id, id, position, url, events, enabled, last_status, last_at)
                VALUES (@clubId, @id, @i, @url, @events, @enabled, @lastStatus, @lastAt)
                ON CONFLICT (club_id, id) DO UPDATE SET position = excluded.position, url = excluded.url, events = excluded.events,
                                                        enabled = excluded.enabled
                """,
                new
                {
                    clubId, id, i, url = w.GetProperty("url").GetString(),
                    events = w.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).Distinct().ToArray(),
                    enabled = w.GetProperty("enabled").GetBoolean(),
                    lastStatus = w.GetProperty("lastStatus") is { ValueKind: JsonValueKind.Number } s ? s.GetInt32() : (int?)null,
                    lastAt = w.GetProperty("lastAt") is { ValueKind: JsonValueKind.String } a
                        ? DateTimeOffset.Parse(a.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                        : (DateTimeOffset?)null,
                },
                tx);
        }

        await c.ExecuteAsync("DELETE FROM webhooks WHERE club_id = @clubId AND NOT (id = ANY(@ids))", new { clubId, ids = ids.ToArray() }, tx);
    }

    /// <summary>
    /// <c>adminApiKey</c>: the current key, created on first read (the <c>clubs</c> row is locked, so two first reads agree).
    /// Never cached by the browser (<c>Cache-Control: no-store</c>).
    /// </summary>
    private static async Task<IResult> ApiKeyAsync(HttpContext context, NpgsqlDataSource db, StaffTokens tokens)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        string key;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await c.ExecuteAsync("SELECT 1 FROM clubs WHERE id = @ClubId FOR UPDATE", new { staff.ClubId }, tx);
            key = await tokens.CurrentApiKeyAsync(c, tx, staff.ClubId);
            await tx.CommitAsync();
        }

        context.Response.Headers.CacheControl = "no-store";
        return AdminJson.Ok(new { apiKey = key });
    }

    /// <summary><c>adminRotateApiKey</c>: a new key at once; the old one is refused from this commit on. The key is never journaled.</summary>
    private static async Task<IResult> RotateApiKeyAsync(HttpContext context, NpgsqlDataSource db, StaffTokens tokens, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        string key;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await c.ExecuteAsync("SELECT 1 FROM clubs WHERE id = @ClubId FOR UPDATE", new { staff.ClubId }, tx);
            key = await tokens.RotateApiKeyAsync(c, tx, staff.ClubId);
            await Audit.WriteAsync(c, tx, staff, clock.GetUtcNow(), "apiKeyRotate", detail: staff.Name);
            await tx.CommitAsync();
        }

        context.Response.Headers.CacheControl = "no-store";
        return AdminJson.Ok(new { apiKey = key });
    }
}
