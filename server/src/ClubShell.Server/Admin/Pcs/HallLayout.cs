using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The hall plan of a club (SHELL_CHANGES 94, <c>clubs.hall_plan</c>): the grid's size in cells, its objects — <c>wall</c>,
/// <c>bar</c> (bar or cash desk), <c>entrance</c>, <c>aisle</c>, <c>label</c> — and zone areas (a rectangle in the zone's
/// colour with its name). Cells are the PCs' cells: a PC at (x, y) stands in the cell x of row y.
/// </summary>
public sealed record HallPlan(int Width, int Height, IReadOnlyList<HallObject> Objects, IReadOnlyList<HallArea> Areas);

/// <summary>An object on the plan: <c>w</c> × <c>h</c> cells from (x, y); <c>text</c> is the label's (or any object's caption).</summary>
public sealed record HallObject(string Id, string Kind, int X, int Y, int W, int H, string? Text = null);

/// <summary>A zone's area on the plan: <c>w</c> × <c>h</c> cells from (x, y), drawn in the zone's colour.</summary>
public sealed record HallArea(string Id, string Zone, int X, int Y, int W, int H);

/// <summary>Where a PC stands on the plan: <c>rotation</c> is where its monitor faces (0 up, 90 right, 180 down, 270 left).</summary>
public sealed record HallPlace(int X, int Y, int Rotation);

/// <summary>One PC of <c>PATCH /admin/pcs/layout</c>; a missing <c>rotation</c> keeps the PC's.</summary>
public sealed record HallLayoutItem(Guid? Id, int? X, int? Y, int? Rotation);

public sealed record HallLayoutRequest(IReadOnlyList<HallLayoutItem>? Items);

/// <summary>
/// The hall editor beyond the contract (SHELL_CHANGES 94), owner only:
/// <list type="bullet">
/// <item><c>PATCH /admin/pcs/layout {items:[{id, x, y, rotation?}]}</c> — one save for a move of a group: the whole batch
/// is checked first (every PC the club's, cells in range, no two PCs of the batch in one cell, no cell held by a PC outside
/// the batch — <c>409 cellTaken {pcId, x, y}</c>) and written in one transaction, journalled once as <c>pcLayout</c>;</item>
/// <item><c>PUT /admin/pcs/plan</c> — the club's <see cref="HallPlan"/>; <c>DELETE /admin/pcs/plan</c> clears it.</item>
/// </list>
/// Placements are serialized on the club's row, as registrations are (<see cref="Agents.PcRepository.RegisterAsync"/>
/// puts a new PC in the first free cell, <see cref="FreeCellAsync"/>). PCs that already share a cell (before M0021) are
/// left so until moved: the editor shows them.
/// </summary>
public static class HallLayout
{
    public const int MaxCell = 200;
    public const int MaxObjects = 300;
    public const int MaxText = 40;

    public static readonly string[] ObjectKinds = ["wall", "bar", "entrance", "aisle", "label"];

    private static readonly int[] Rotations = [0, 90, 180, 270];

    public static void MapHallLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/pcs").WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapPatch("/layout", LayoutAsync);
        api.MapPut("/plan", SavePlanAsync);
        api.MapDelete("/plan", (HttpContext context, NpgsqlDataSource db, TimeProvider clock) => StorePlanAsync(context, null, db, clock));
    }

    /// <summary>The club's plan, null when none was drawn (or the stored one no longer reads).</summary>
    public static async Task<HallPlan?> PlanAsync(NpgsqlConnection c, Guid clubId, NpgsqlTransaction? tx = null)
    {
        var json = await c.ExecuteScalarAsync<string?>("SELECT hall_plan::text FROM clubs WHERE id = @clubId", new { clubId }, tx);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HallPlan>(json, ServerJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Locks the club's placements (its row) for the transaction.</summary>
    public static Task LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId) =>
        c.ExecuteAsync("SELECT 1 FROM clubs WHERE id = @clubId FOR UPDATE", new { clubId }, tx);

    /// <summary>A live PC of the club, other than <paramref name="except"/>, standing at (x, y); null — the cell is free.</summary>
    public static Task<Guid?> HolderAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, int x, int y, Guid? except = null) =>
        c.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM pcs WHERE club_id = @clubId AND deleted_at IS NULL AND x = @x AND y = @y AND id <> @except ORDER BY number LIMIT 1",
            new { clubId, x, y, except = except ?? Guid.Empty }, tx);

    /// <summary><c>409 cellTaken {pcId, x, y}</c> when another live PC of the club stands at (x, y).</summary>
    public static async Task EnsureFreeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, int x, int y, Guid? except = null)
    {
        if (await HolderAsync(c, tx, clubId, x, y, except) is { } holder)
        {
            throw CellTaken(holder, x, y);
        }
    }

    /// <summary>
    /// The first free cell for a new PC: row by row within the plan's width (the club's plan, else the widest row in use,
    /// at least 10 cells), right of the hall when every row is full.
    /// </summary>
    public static async Task<(int X, int Y)> FreeCellAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId)
    {
        var taken = (await c.QueryAsync<(int X, int Y)>("SELECT x, y FROM pcs WHERE club_id = @clubId AND deleted_at IS NULL", new { clubId }, tx)).ToHashSet();
        var plan = await PlanAsync(c, clubId, tx);
        var width = Math.Clamp(plan?.Width ?? Math.Max(10, taken.Count == 0 ? 0 : taken.Max(t => t.X) + 1), 1, MaxCell + 1);
        for (var y = 0; y <= MaxCell; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!taken.Contains((x, y)))
                {
                    return (x, y);
                }
            }
        }

        return (Math.Min(width, MaxCell), 0);
    }

    public static ApiException CellTaken(Guid pcId, int x, int y) =>
        new(StatusCodes.Status409Conflict, Contracts.Errors.ErrorCode.Conflict, $"Cell ({x}, {y}) is taken", new { reason = "cellTaken", pcId, x, y });

    public static void CheckRotation(int? rotation, string field = "rotation")
    {
        if (rotation is { } r && !Rotations.Contains(r))
        {
            throw ApiException.Validation(field, "enum");
        }
    }

    private static void CheckCell(int? x, int? y, string prefix)
    {
        if (x is < 0 or > MaxCell || y is < 0 or > MaxCell)
        {
            throw ApiException.Validation(prefix + (x is < 0 or > MaxCell ? "x" : "y"), "range");
        }
    }

    private static async Task<IResult> LayoutAsync(HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var request = Api.Read<HallLayoutRequest>(body, "items");
        var items = request.Items!;
        if (items.Count is 0 or > 1000)
        {
            throw ApiException.Validation("items", items.Count == 0 ? "min" : "max");
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Id is null || item.X is null || item.Y is null)
            {
                throw ApiException.Validation($"items[{i}].{(item.Id is null ? "id" : item.X is null ? "x" : "y")}", "required");
            }

            CheckCell(item.X, item.Y, $"items[{i}].");
            CheckRotation(item.Rotation, $"items[{i}].rotation");
        }

        if (items.Select(i => i.Id).Distinct().Count() != items.Count)
        {
            throw ApiException.Validation("items", "duplicate", "A PC is listed twice");
        }

        // Two PCs of the batch in one cell: the second is refused, naming the first.
        var cells = new Dictionary<(int, int), Guid>();
        foreach (var item in items)
        {
            if (!cells.TryAdd((item.X!.Value, item.Y!.Value), item.Id!.Value))
            {
                throw CellTaken(cells[(item.X.Value, item.Y.Value)], item.X.Value, item.Y.Value);
            }
        }

        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await LockAsync(c, tx, staff.ClubId);
        var ids = items.Select(i => i.Id!.Value).ToArray();
        var known = (await c.QueryAsync<Guid>(
            "SELECT id FROM pcs WHERE club_id = @ClubId AND deleted_at IS NULL AND id = ANY(@ids)", new { staff.ClubId, ids }, tx)).ToHashSet();
        if (known.Count != ids.Length)
        {
            throw ApiException.NotFound("pc");
        }

        // Cells held by PCs outside the batch (the batch's own PCs leave theirs).
        var others = new Dictionary<(int, int), Guid>();
        foreach (var o in await c.QueryAsync<(Guid Id, int X, int Y)>(
            "SELECT id, x, y FROM pcs WHERE club_id = @ClubId AND deleted_at IS NULL AND NOT (id = ANY(@ids)) ORDER BY number",
            new { staff.ClubId, ids }, tx))
        {
            others.TryAdd((o.X, o.Y), o.Id);
        }

        foreach (var item in items)
        {
            if (others.TryGetValue((item.X!.Value, item.Y!.Value), out var holder))
            {
                throw CellTaken(holder, item.X.Value, item.Y.Value);
            }
        }

        var now = clock.GetUtcNow();
        foreach (var item in items)
        {
            await c.ExecuteAsync(
                "UPDATE pcs SET x = @X, y = @Y, rotation = coalesce(@Rotation, rotation), updated_at = @now WHERE id = @Id",
                new { item.Id, item.X, item.Y, item.Rotation, now }, tx);
        }

        await Audit.WriteAsync(c, tx, staff, now, "pcLayout", detail: $"Расстановка: {items.Count} ПК", meta: new { count = items.Count });
        await tx.CommitAsync();
        return AdminJson.Ok(new { ok = true, count = items.Count });
    }

    private static async Task<IResult> SavePlanAsync(HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var plan = Api.Read<HallPlan>(body, "width", "height");
        plan = plan with { Objects = plan.Objects ?? [], Areas = plan.Areas ?? [] };
        Check(plan);
        return await StorePlanAsync(context, plan, db, clock);
    }

    private static async Task<IResult> StorePlanAsync(HttpContext context, HallPlan? plan, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var now = clock.GetUtcNow();
        await c.ExecuteAsync(
            "UPDATE clubs SET hall_plan = @json::jsonb, updated_at = @now WHERE id = @ClubId",
            new { json = plan is null ? null : JsonSerializer.Serialize(plan, ServerJson.Options), now, staff.ClubId }, tx);
        await Audit.WriteAsync(c, tx, staff, now, "settingsSave", detail: "План зала", meta: new { hallPlan = plan is not null, objects = plan?.Objects.Count ?? 0, areas = plan?.Areas.Count ?? 0 });
        await tx.CommitAsync();
        return AdminJson.Ok(new { ok = true, plan });
    }

    private static void Check(HallPlan plan)
    {
        if (plan.Width is < 1 or > MaxCell + 1 || plan.Height is < 1 or > MaxCell + 1)
        {
            throw ApiException.Validation(plan.Width is < 1 or > MaxCell + 1 ? "width" : "height", "range");
        }

        var objects = plan.Objects;
        var areas = plan.Areas;
        if (objects.Count + areas.Count > MaxObjects)
        {
            throw ApiException.Validation("objects", "max");
        }

        void Box(string field, string? id, int x, int y, int w, int h)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64)
            {
                throw ApiException.Validation(field + ".id", "required");
            }

            if (x < 0 || y < 0 || w < 1 || h < 1 || x + w > MaxCell + 1 || y + h > MaxCell + 1)
            {
                throw ApiException.Validation(field, "range");
            }
        }

        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            Box($"objects[{i}]", o.Id, o.X, o.Y, o.W, o.H);
            if (!ObjectKinds.Contains(o.Kind, StringComparer.Ordinal))
            {
                throw ApiException.Validation($"objects[{i}].kind", "enum");
            }

            if (o.Text is { Length: > MaxText })
            {
                throw ApiException.Validation($"objects[{i}].text", "max");
            }
        }

        for (var i = 0; i < areas.Count; i++)
        {
            var a = areas[i];
            Box($"areas[{i}]", a.Id, a.X, a.Y, a.W, a.H);
            if (string.IsNullOrEmpty(a.Zone) || a.Zone.Length > 32)
            {
                throw ApiException.Validation($"areas[{i}].zone", "required");
            }
        }
    }
}
