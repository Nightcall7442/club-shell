using System.Text.Json;
using System.Text.RegularExpressions;
using ClubShell.Contracts.Commands;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions.Billing;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// Tariffs of the console (slice S5, D-8): <c>adminTariffs</c> lists the club's live tariffs without the agent's zone filter;
/// the owner adds (<c>adminAddTariff</c>, id by the server), replaces (<c>adminSaveTariff</c>, <c>404 tariff</c>) and deletes
/// (<c>adminDeleteTariff</c>, always <c>200 {ok:true}</c>) them. Prices come in as integer tiyin and go out as Money. Delete
/// is soft (<c>deleted_at</c>): open sessions keep their price snapshot and settle against the row, a new purchase no longer
/// finds it. The agent's <c>GET /tariffs</c> ETag hashes the live list, so every change here moves it; each change also
/// queues <c>refreshConfig {tariffs:true}</c> for the club's PCs (§5.9), sent after the commit to those connected.
/// Unknown weekdays in <c>timeWindows</c> are <c>400 enum</c> (§2.4), not dropped as by the mock.
/// </summary>
public static partial class TariffEndpoints
{
    public static readonly string[] Operations = ["adminTariffs", "adminAddTariff", "adminSaveTariff", "adminDeleteTariff"];

    private static readonly string[] Weekdays = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public static void MapTariffEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/tariffs");
        api.MapGet("", ListAsync).WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapPost("", AddAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapPut("/{id}", SaveAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapDelete("/{id}", DeleteAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
    }

    private static async Task<IResult> ListAsync(HttpContext context, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<TariffRow>(
            $"SELECT {TariffRow.Columns} FROM tariffs WHERE club_id = @ClubId AND deleted_at IS NULL ORDER BY created_at, id", new { staff.ClubId });
        return AdminJson.Ok(new AdminTariffList(rows.Select(t => t.ToWire()).ToList()));
    }

    private static async Task<IResult> AddAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, CommandDispatcher commands, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var t = Parse(body);
        var queued = new List<(Guid PcId, ServerCommandEnvelope Command)>();
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var row = await c.QuerySingleAsync<TariffRow>(
                $"""
                INSERT INTO tariffs (id, club_id, name, price_per_hour, min_minutes, max_minutes, zones, time_windows, is_package, package_minutes,
                                     package_price, created_at, updated_at)
                VALUES (@id, @ClubId, @Name, @PricePerHour, @MinMinutes, @MaxMinutes, @Zones, @TimeWindows::jsonb, @IsPackage, @PackageMinutes,
                        @PackagePrice, @now, @now)
                RETURNING {TariffRow.Columns}
                """,
                new
                {
                    id = Guid.CreateVersion7(now), staff.ClubId, t.Name, t.PricePerHour, t.MinMinutes, t.MaxMinutes, t.Zones, t.TimeWindows, t.IsPackage,
                    t.PackageMinutes, t.PackagePrice, now,
                },
                tx);
            await Audit.WriteAsync(c, tx, staff, now, "tariffAdd", detail: t.Name, meta: new { tariffId = row.Id, pricePerHour = t.PricePerHour });
            queued.AddRange(await RefreshAsync(tx, commands, c, staff));
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminTariffResponse(row.ToWire())));
        });
        await SendAsync(commands, queued);
        return result;
    }

    /// <summary>Full replacement of a live tariff; the id stays.</summary>
    private static async Task<IResult> SaveAsync(
        HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, CommandDispatcher commands, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var t = Parse(body);
        var tariffId = Guid.TryParse(id, out var parsed) ? parsed : throw ApiException.NotFound("tariff");
        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued;
        TariffRow row;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            row = await c.QuerySingleOrDefaultAsync<TariffRow>(
                $"""
                UPDATE tariffs SET name = @Name, price_per_hour = @PricePerHour, min_minutes = @MinMinutes, max_minutes = @MaxMinutes, zones = @Zones,
                                   time_windows = @TimeWindows::jsonb, is_package = @IsPackage, package_minutes = @PackageMinutes,
                                   package_price = @PackagePrice, updated_at = @now
                WHERE id = @tariffId AND club_id = @ClubId AND deleted_at IS NULL
                RETURNING {TariffRow.Columns}
                """,
                new
                {
                    tariffId, staff.ClubId, t.Name, t.PricePerHour, t.MinMinutes, t.MaxMinutes, t.Zones, t.TimeWindows, t.IsPackage, t.PackageMinutes,
                    t.PackagePrice, now,
                },
                tx)
                ?? throw ApiException.NotFound("tariff");
            await Audit.WriteAsync(c, tx, staff, now, "tariffSave", detail: t.Name, meta: new { tariffId, pricePerHour = t.PricePerHour });
            queued = await RefreshAsync(tx, commands, c, staff);
            await tx.CommitAsync();
        }

        await SendAsync(commands, queued);
        return AdminJson.Ok(new AdminTariffResponse(row.ToWire()));
    }

    /// <summary>Soft delete; an unknown or already deleted tariff is <c>200</c> too (the contract declares no 404).</summary>
    private static async Task<IResult> DeleteAsync(HttpContext context, string id, NpgsqlDataSource db, CommandDispatcher commands, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (!Guid.TryParse(id, out var tariffId))
        {
            return AdminJson.Ok(AdminJson.OkBody);
        }

        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued = [];
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            var name = await c.QuerySingleOrDefaultAsync<string>(
                "UPDATE tariffs SET deleted_at = @now, updated_at = @now WHERE id = @tariffId AND club_id = @ClubId AND deleted_at IS NULL RETURNING name",
                new { tariffId, staff.ClubId, now }, tx);
            if (name is not null)
            {
                await Audit.WriteAsync(c, tx, staff, now, "tariffDelete", detail: name, meta: new { tariffId });
                queued = await RefreshAsync(tx, commands, c, staff);
            }

            await tx.CommitAsync();
        }

        await SendAsync(commands, queued);
        return AdminJson.Ok(AdminJson.OkBody);
    }

    /// <summary><c>refreshConfig {tariffs:true}</c> queued in <paramref name="tx"/> for every approved live PC of the club (§5.9).</summary>
    private static async Task<IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)>> RefreshAsync(NpgsqlTransaction tx, CommandDispatcher commands, NpgsqlConnection c, StaffContext staff)
    {
        var queued = new List<(Guid PcId, ServerCommandEnvelope Command)>();
        var command = NewCommand.RefreshConfig(new RefreshConfigCommand(Tariffs: true));
        foreach (var pcId in await c.QueryAsync<Guid>(
                     "SELECT id FROM pcs WHERE club_id = @ClubId AND approved AND deleted_at IS NULL ORDER BY id", new { staff.ClubId }, tx))
        {
            queued.Add((pcId, await commands.QueueAsync(tx, staff.ClubId, pcId, command, issuedByStaffId: staff.StaffId)));
        }

        return queued;
    }

    private static async Task SendAsync(CommandDispatcher commands, IEnumerable<(Guid PcId, ServerCommandEnvelope Command)> queued)
    {
        foreach (var (pcId, command) in queued)
        {
            await commands.SendAsync(pcId, command);
        }
    }

    /// <summary>
    /// <c>AdminTariffInput</c>: the contract's bounds (<c>min</c>/<c>max</c>), <c>minMinutes</c> 30 when absent,
    /// <c>maxMinutes</c> below <c>minMinutes</c> is <c>range</c>; package minutes and price are required only for a package
    /// and dropped otherwise.
    /// </summary>
    private static TariffInput Parse(JsonElement body)
    {
        var r = Api.Read<AdminTariffInput>(body, "name", "pricePerHour", "zones", "isPackage");
        var name = AdminInput.Text(r.Name, "name", 64);
        var price = AdminInput.Range(r.PricePerHour, "pricePerHour", 0, 1_000_000_000)!.Value;
        var min = AdminInput.Range(r.MinMinutes ?? 30, "minMinutes", 5, 1440)!.Value;
        var max = AdminInput.Range(r.MaxMinutes, "maxMinutes", 5, 10_080);
        if (max < min)
        {
            throw ApiException.Validation("maxMinutes", "range");
        }

        var zones = r.Zones!;
        if (zones.Count > 20)
        {
            throw ApiException.Validation("zones", "max");
        }

        for (var i = 0; i < zones.Count; i++)
        {
            if (string.IsNullOrEmpty(zones[i]))
            {
                throw ApiException.Validation($"zones[{i}]", "format");
            }
        }

        var package = r.IsPackage!.Value;
        int? packageMinutes = null;
        long? packagePrice = null;
        if (package)
        {
            packageMinutes = AdminInput.Range(r.PackageMinutes ?? throw ApiException.Validation("packageMinutes", "required"), "packageMinutes", 5, 10_080);
            packagePrice = AdminInput.Range(r.PackagePrice ?? throw ApiException.Validation("packagePrice", "required"), "packagePrice", 0, 1_000_000_000);
        }

        return new TariffInput(name, price, min, max, [.. zones], Windows(r.TimeWindows), package, packageMinutes, packagePrice);
    }

    /// <summary><c>TariffTimeWindow[]</c> as stored in <c>tariffs.time_windows</c>; absent or null — always (<c>[]</c>).</summary>
    private static string Windows(JsonElement? value)
    {
        if (value is not { ValueKind: not JsonValueKind.Null } windows)
        {
            return "[]";
        }

        if (windows.ValueKind != JsonValueKind.Array)
        {
            throw ApiException.Validation("timeWindows", "format");
        }

        var list = new List<object>();
        foreach (var (w, i) in windows.EnumerateArray().Select((w, i) => (w, i)))
        {
            var at = $"timeWindows[{i}]";
            if (w.ValueKind != JsonValueKind.Object)
            {
                throw ApiException.Validation(at, "format");
            }

            if (!w.TryGetProperty("days", out var days) || days.ValueKind == JsonValueKind.Null)
            {
                throw ApiException.Validation($"{at}.days", "required");
            }

            if (days.ValueKind != JsonValueKind.Array)
            {
                throw ApiException.Validation($"{at}.days", "format");
            }

            var dayList = new List<string>();
            foreach (var d in days.EnumerateArray())
            {
                var day = d.ValueKind == JsonValueKind.String ? d.GetString()! : throw ApiException.Validation($"{at}.days", "format");
                dayList.Add(Weekdays.Contains(day, StringComparer.Ordinal) ? day : throw ApiException.Validation($"{at}.days", "enum"));
            }

            list.Add(new { days = dayList, from = Time(w, "from", at), to = Time(w, "to", at) });
        }

        return JsonSerializer.Serialize(list);
    }

    /// <summary><c>HH:mm</c> of a valid time of day.</summary>
    private static string Time(JsonElement window, string name, string at)
    {
        if (!window.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw ApiException.Validation($"{at}.{name}", "required");
        }

        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text && HhMm().IsMatch(text) && ClubTime.Minutes(text) is not null
            ? text
            : throw ApiException.Validation($"{at}.{name}", "format");
    }

    [GeneratedRegex("^[0-9]{2}:[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex HhMm();

    private sealed record TariffInput(
        string Name, long PricePerHour, int MinMinutes, int? MaxMinutes, string[] Zones, string TimeWindows, bool IsPackage, int? PackageMinutes,
        long? PackagePrice);
}
