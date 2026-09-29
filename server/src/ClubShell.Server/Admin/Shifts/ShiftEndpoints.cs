using System.Globalization;
using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Sessions;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// Cash shifts (slice S4, DESIGN §4.3): at most one open per club (<c>shifts_open</c>), every ledger row gets the open
/// shift's id (<see cref="Wallet.Ledger"/> takes it <c>FOR SHARE</c>, closing takes it <c>FOR UPDATE</c>, so no row joins the
/// shift after its Z report; a row racing the close gets <c>shift_id NULL</c>, as with no shift open — S5 <c>noShift</c>
/// counts those). X/Z report = sums by <c>shift_id</c> grouped by type and method; <c>expectedCash = openingCash + Σ topUp
/// cash</c>. A shortfall over <c>settings.control.shortfallFrom</c> (default 500 000 tiyin) flags the <c>shiftClose</c>
/// entry. Any staff member may close (not only who opened). Closing raises <c>shiftClosed</c>, and <c>suspicious</c> on a
/// shortfall, for the webhooks (S5), in the close's transaction.
/// </summary>
public static class ShiftEndpoints
{
    public static readonly string[] Operations = ["adminShift", "adminOpenShift", "adminCloseShift"];

    private const long MaxCash = 10_000_000_000;

    public static void MapShiftEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/shift").WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapGet("", async (HttpContext context, NpgsqlDataSource db) =>
        {
            var staff = context.Features.GetRequiredFeature<StaffContext>();
            await using var c = await db.OpenConnectionAsync();
            var open = await OpenShiftAsync(c, staff.ClubId);
            var history = (await c.QueryAsync<ShiftRow>(
                $"SELECT {ShiftRow.Columns} FROM shifts WHERE club_id = @ClubId AND closed_at IS NOT NULL ORDER BY closed_at DESC LIMIT 30", new { staff.ClubId }))
                .Select(s => s.ToWire()).ToList();
            return AdminJson.Ok(new AdminShiftState(open, open is null ? null : await TotalsAsync(c, null, open.Id), history));
        });
        api.MapPost("/open", OpenAsync);
        api.MapPost("/close", CloseAsync);
    }

    /// <summary>Shifts opened at or after <paramref name="since"/>, newest first (<c>adminReports</c>).</summary>
    public static async Task<IReadOnlyList<AdminShift>> OpenedSinceAsync(NpgsqlConnection c, Guid clubId, DateTimeOffset since) =>
        (await c.QueryAsync<ShiftRow>(
            $"SELECT {ShiftRow.Columns} FROM shifts WHERE club_id = @clubId AND opened_at >= @since ORDER BY opened_at DESC, id DESC", new { clubId, since }))
        .Select(s => s.ToWire()).ToList();

    public static async Task<AdminShift?> OpenShiftAsync(NpgsqlConnection c, Guid clubId) =>
        (await c.QuerySingleOrDefaultAsync<ShiftRow>($"SELECT {ShiftRow.Columns} FROM shifts WHERE club_id = @clubId AND closed_at IS NULL", new { clubId }))?.ToWire();

    private static async Task<IResult> OpenAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var cash = Cash(Api.Read<AdminCashRequest>(body, "openingCash").OpeningCash!.Value, "openingCash");
        return await store.ExecuteHttpAsync(context, Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7(now);
            await c.ExecuteAsync("SAVEPOINT open_shift", transaction: tx);
            try
            {
                await c.ExecuteAsync(
                    """
                    INSERT INTO shifts (id, club_id, staff_id, staff_name, opened_at, opening_cash)
                    VALUES (@id, @ClubId, @StaffId, @Name, @now, @cash)
                    """,
                    new { id, staff.ClubId, staff.StaffId, staff.Name, now, cash }, tx);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "shifts_open")
            {
                await c.ExecuteAsync("ROLLBACK TO SAVEPOINT open_shift", transaction: tx);
                throw SessionService.Conflict("shiftOpen");
            }

            await Audit.WriteAsync(c, tx, staff, now, "shiftOpen", amount: cash, detail: staff.Name, meta: new { openingCash = cash }, shiftId: id);
            var shift = await c.QuerySingleAsync<ShiftRow>($"SELECT {ShiftRow.Columns} FROM shifts WHERE id = @id", new { id }, tx);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminShiftResponse(shift.ToWire())));
        });
    }

    private static async Task<IResult> CloseAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var counted = Cash(Api.Read<AdminCashRequest>(body, "closingCash").ClosingCash!.Value, "closingCash");
        return await store.ExecuteHttpAsync(context, Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var shift = await c.QuerySingleOrDefaultAsync<ShiftRow>(
                $"SELECT {ShiftRow.Columns} FROM shifts WHERE club_id = @ClubId AND closed_at IS NULL FOR UPDATE", new { staff.ClubId }, tx)
                ?? throw SessionService.Conflict("noShift");
            var totals = await TotalsAsync(c, tx, shift.Id);
            var expected = shift.OpeningCash + totals.TopUpCash;
            var shortfallFrom = await c.ExecuteScalarAsync<long?>(
                "SELECT (settings -> 'control' ->> 'shortfallFrom')::bigint FROM clubs WHERE id = @ClubId", new { staff.ClubId }, tx) ?? 500_000;
            var shortfall = expected - counted > shortfallFrom;
            await c.ExecuteAsync(
                """
                UPDATE shifts SET closed_at = @now, closing_cash = @counted, expected_cash = @expected, totals = @totals::jsonb
                WHERE id = @Id
                """,
                new { shift.Id, now, counted, expected, totals = JsonSerializer.Serialize(totals, ServerJson.Options) }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "shiftClose", amount: counted, detail: shift.StaffName,
                meta: new { expected, counted, diff = counted - expected, shortfall }, shiftId: shift.Id);
            await Webhooks.EnqueueAsync(c, tx, staff.ClubId, "shiftClosed", now,
                $"{shift.StaffName}: сеансы {Webhooks.Sum(totals.Sessions)}, магазин {Webhooks.Sum(totals.Shop)}, касса {Webhooks.Sum(counted)} (ожидалось {Webhooks.Sum(expected)})",
                new { shiftId = shift.Id });
            await ControlAlerts.ShiftClosedAsync(c, tx, staff, shift.StaffName, counted - expected, now);
            var closed = await c.QuerySingleAsync<ShiftRow>($"SELECT {ShiftRow.Columns} FROM shifts WHERE id = @Id", new { shift.Id }, tx);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminShiftCloseResponse(closed.ToWire(), expected)));
        });
    }

    /// <summary>X/Z report of a shift: its ledger rows by type and method (<c>topUp</c> cash vs other methods).</summary>
    private static async Task<AdminShiftTotals> TotalsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid shiftId) =>
        await c.QuerySingleAsync<AdminShiftTotals>(
            """
            SELECT coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'cash'), 0)::bigint AS "TopUpCash",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method IS DISTINCT FROM 'cash'), 0)::bigint AS "TopUpOther",
                   coalesce(-sum(amount) FILTER (WHERE type = 'charge'), 0)::bigint AS "Sessions",
                   coalesce(-sum(amount) FILTER (WHERE type = 'purchase'), 0)::bigint AS "Shop",
                   coalesce(sum(amount) FILTER (WHERE type = 'refund'), 0)::bigint AS "Refunds",
                   coalesce(sum(amount) FILTER (WHERE type = 'bonus'), 0)::bigint AS "Bonuses",
                   count(*)::int AS "Count"
            FROM ledger_entries WHERE shift_id = @shiftId
            """,
            new { shiftId },
            tx);

    public static string Principal(StaffContext staff) => "club:" + staff.ClubId.ToString("D", CultureInfo.InvariantCulture);

    private static long Cash(long value, string field) =>
        value < 0 ? throw ApiException.Validation(field, "min") : value > MaxCash ? throw ApiException.Validation(field, "max") : value;

    private sealed class ShiftRow
    {
        public const string Columns = "id, staff_id, staff_name, opened_at, closed_at, opening_cash, closing_cash, totals::text AS totals";

        public Guid Id { get; init; }
        public Guid? StaffId { get; init; }
        public string StaffName { get; init; } = "";
        public DateTimeOffset OpenedAt { get; init; }
        public DateTimeOffset? ClosedAt { get; init; }
        public long OpeningCash { get; init; }
        public long? ClosingCash { get; init; }
        public string? Totals { get; init; }

        public AdminShift ToWire() => new(
            Id, StaffId?.ToString() ?? "apiKey", StaffName, OpenedAt, ClosedAt, OpeningCash, ClosingCash,
            Totals is null ? null : JsonSerializer.Deserialize<AdminShiftTotals>(Totals, ServerJson.Options));
    }
}
