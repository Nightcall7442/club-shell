using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Wallet;
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
/// counts those). The counter takes no money without an open shift (<see cref="RequireOpenAsync"/>). X/Z report = sums by
/// <c>shift_id</c> grouped by type and method, top-ups also per method, plus the drawer's movements and the guests' payouts
/// (cash desk part 2); only the server computes <see cref="Expected"/> = opening + staff cash top-ups + cash in − cash out −
/// payouts (cash top-ups of the club API key never were in the drawer). A shortfall over
/// <c>settings.control.shortfallFrom</c> (default 500 000 tiyin) flags the <c>shiftClose</c> entry. Any staff member may
/// close (not only who opened); the shift records who did. Closing raises <c>shiftClosed</c>, and <c>suspicious</c> on a
/// shortfall, for the webhooks (S5), in the close's transaction. Beyond the contract: cash in and out of the drawer
/// (<c>POST /shift/cash</c>, D-40) and the shift's operations feed (<c>GET /shift/operations</c>, D-43).
/// </summary>
public static class ShiftEndpoints
{
    public static readonly string[] Operations = ["adminShift", "adminOpenShift", "adminCloseShift"];

    private const long MaxCash = 10_000_000_000;

    /// <summary>Reason codes of a drawer movement and the journal's words for them (the console prints its own labels).</summary>
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["change"] = "Размен", ["collection"] = "Инкассация", ["expenses"] = "Хозрасходы", ["other"] = "Другое",
    };

    /// <summary>The feed's kinds: journal actions, with <c>debtPaid</c> — a <c>topUp</c> that settled a debt.</summary>
    private static readonly string[] FeedKinds =
        ["topUp", "debtPaid", "sessionOpen", "sessionExtend", "sessionEnd", "payout", "cashIn", "cashOut", "shiftOpen", "shiftClose", "promoRedeem"];

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
            var x = open is null ? null : await TotalsAsync(c, null, open.Id);
            return AdminJson.Ok(new AdminShiftState(open, x, history, open is null ? null : Expected(open.OpeningCash, x!)));
        });
        api.MapPost("/open", OpenAsync);
        api.MapPost("/close", CloseAsync);
        api.MapPost("/cash", CashAsync);
        api.MapGet("/operations", OperationsAsync);
    }

    /// <summary>Shifts opened at or after <paramref name="since"/>, newest first (<c>adminReports</c>).</summary>
    public static async Task<IReadOnlyList<AdminShift>> OpenedSinceAsync(NpgsqlConnection c, Guid clubId, DateTimeOffset since) =>
        (await c.QueryAsync<ShiftRow>(
            $"SELECT {ShiftRow.Columns} FROM shifts WHERE club_id = @clubId AND opened_at >= @since ORDER BY opened_at DESC, id DESC", new { clubId, since }))
        .Select(s => s.ToWire()).ToList();

    public static async Task<AdminShift?> OpenShiftAsync(NpgsqlConnection c, Guid clubId) =>
        (await c.QuerySingleOrDefaultAsync<ShiftRow>($"SELECT {ShiftRow.Columns} FROM shifts WHERE club_id = @clubId AND closed_at IS NULL", new { clubId }))?.ToWire();

    /// <summary>The drawer the server expects (D-41): opening + staff cash top-ups + cash in − cash out − guest payouts.</summary>
    public static long Expected(long openingCash, AdminShiftTotals t) => openingCash + (t.TopUpCash - t.ApiCash) + t.CashIn - t.CashOut - t.Payouts;

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

    /// <summary>
    /// Closes the open shift with the counted drawer: the Z report, <see cref="Expected"/>, who closed it, the
    /// <c>shiftClose</c> journal entry (<c>shortfall</c> flag) and the webhooks. The shift is locked <c>FOR UPDATE</c>, so a
    /// racing ledger row, drawer movement or payout waits and then finds no open shift.
    /// </summary>
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
            var expected = Expected(shift.OpeningCash, totals);
            var shortfallFrom = await c.ExecuteScalarAsync<long?>(
                "SELECT (settings -> 'control' ->> 'shortfallFrom')::bigint FROM clubs WHERE id = @ClubId", new { staff.ClubId }, tx) ?? 500_000;
            var shortfall = expected - counted > shortfallFrom;
            await c.ExecuteAsync(
                """
                UPDATE shifts SET closed_at = @now, closing_cash = @counted, expected_cash = @expected, totals = @totals::jsonb,
                                  closed_by_staff_id = @StaffId, closed_by_name = @Name
                WHERE id = @Id
                """,
                new { shift.Id, now, counted, expected, totals = JsonSerializer.Serialize(totals, ServerJson.Options), staff.StaffId, staff.Name }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "shiftClose", amount: counted, detail: shift.StaffName,
                meta: new { expected, counted, diff = counted - expected, shortfall }, shiftId: shift.Id);
            var moves = totals.CashIn + totals.CashOut + totals.Payouts == 0 ? ""
                : $", внесено {Webhooks.Sum(totals.CashIn)} / изъято {Webhooks.Sum(totals.CashOut)} / выдано гостям {Webhooks.Sum(totals.Payouts)}";
            await Webhooks.EnqueueAsync(c, tx, staff.ClubId, "shiftClosed", now,
                $"{shift.StaffName}: сеансы {Webhooks.Sum(totals.Sessions)}, магазин {Webhooks.Sum(totals.Shop)}{moves}, касса {Webhooks.Sum(counted)} (ожидалось {Webhooks.Sum(expected)})",
                new { shiftId = shift.Id });
            await ControlAlerts.ShiftClosedAsync(c, tx, staff, shift.StaffName, counted - expected, now);
            var closed = await c.QuerySingleAsync<ShiftRow>($"SELECT {ShiftRow.Columns} FROM shifts WHERE id = @Id", new { shift.Id }, tx);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminShiftCloseResponse(closed.ToWire(), expected)));
        });
    }

    /// <summary>
    /// <c>POST /admin/shift/cash</c> (beyond the contract, D-40): cash put into (<c>in</c>) or taken out of (<c>out</c>) the
    /// drawer of the open shift, with a reason code (<c>change | collection | expenses | other</c>, a note of 3–200
    /// characters required for <c>other</c>). Cash-out locks the shift <c>FOR NO KEY UPDATE</c> — it waits for every ledger
    /// writer (<c>FOR SHARE</c>) and for another cash-out, and nothing is locked after it (§4.4) — then may not take more than
    /// the drawer is expected to hold (<c>409 cashShort {available}</c>); cash-in takes it <c>FOR SHARE</c>. With
    /// <c>limits.cashOutOwnerOnly</c> only the owner takes cash out (<c>403 ownerOnly</c>); the club API key never (<c>403
    /// staffOnly</c>). A cash-out of at least <c>notifications.bigTopupAt</c> raises <c>suspicious</c>. The movement, its
    /// journal entry and the event commit together; <c>Idempotency-Key</c> required.
    /// </summary>
    private static async Task<IResult> CashAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, TimeProvider clock)
    {
        var staff = CounterEndpoints.StaffOnly(context);
        var r = Api.Read<AdminCashMoveRequest>(body, "kind", "amount", "reasonCode");
        var kind = r.Kind is "in" or "out" ? r.Kind : throw ApiException.Validation("kind", "enum");
        var amount = r.Amount!.Value < 1 ? throw ApiException.Validation("amount", "min") : r.Amount.Value > MaxCash ? throw ApiException.Validation("amount", "max") : r.Amount.Value;
        var reason = r.ReasonCode is { } code && Reasons.ContainsKey(code) ? code : throw ApiException.Validation("reasonCode", "enum");
        var note = r.Note?.Trim() is { Length: > 0 } given ? given : null;
        if (note is { Length: > 200 })
        {
            throw ApiException.Validation("note", "max");
        }

        if (reason == "other" && note is not { Length: >= 3 })
        {
            throw ApiException.Validation("note", note is null ? "required" : "min");
        }

        return await store.ExecuteHttpAsync(context, Principal(staff), keyRequired: true, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            if (kind == "out" && !staff.IsOwner && (await SessionService.ClubAsync(c, tx, staff.ClubId)).Pricing.CashOutOwnerOnly)
            {
                throw ApiException.Forbidden("ownerOnly", "Only the owner takes cash out of the drawer");
            }

            var shift = await LockOpenShiftAsync(c, tx, staff.ClubId, strong: kind == "out");
            if (kind == "out")
            {
                await EnsureDrawerAsync(c, tx, shift, amount);
            }

            var id = Guid.CreateVersion7(now);
            await c.ExecuteAsync(
                """
                INSERT INTO cash_movements (id, club_id, shift_id, staff_id, staff_name, kind, amount, reason_code, note, created_at)
                VALUES (@id, @ClubId, @shiftId, @StaffId, @Name, @kind, @amount, @reason, @note, @now)
                """,
                new { id, staff.ClubId, shiftId = shift.Id, staff.StaffId, staff.Name, kind, amount, reason, note, now }, tx);
            await Audit.WriteAsync(c, tx, staff, now, kind == "in" ? "cashIn" : "cashOut", amount: amount, detail: note ?? Reasons[reason],
                meta: new { reasonCode = reason, note, movementId = id }, shiftId: shift.Id);
            if (kind == "out")
            {
                await ControlAlerts.CashOutAsync(c, tx, staff, amount, Reasons[reason], now);
            }

            var expected = Expected(shift.OpeningCash, await TotalsAsync(c, tx, shift.Id));
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminCashMoveResponse(
                new AdminCashMovement(id, kind, amount, reason, note, now, staff.Name), expected)));
        });
    }

    /// <summary>
    /// <c>GET /admin/shift/operations</c> (beyond the contract, D-43): the shift's journal as the desk's feed, newest first
    /// (<c>audit_entries_shift</c>, keyset cursor <c>before</c> = base64url <c>at|id</c>, <c>limit</c> 1–200, default 50,
    /// <c>kinds</c> a comma list of <see cref="FeedKinds"/>). A paid open or extend is one row: its top-up
    /// (<c>meta.forSession</c>) is left out, the session row carries the payment; a top-up that settled a debt is
    /// <c>debtPaid</c>. Kiosk self-service is not in it (X/Z has it). <c>shiftId</c> defaults to the open shift; a cashier
    /// reads the open shift and the last closed one (else <c>403 ownerOnly</c>), the owner any. <c>today</c> — the club's
    /// local day so far from the ledger (ReportsEndpoints' calendar), whether a shift is open or not.
    /// </summary>
    private static async Task<IResult> OperationsAsync(
        HttpContext context, string? shiftId, string? before, string? limit, string? kinds, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        Guid? wanted = shiftId is null ? null : Guid.TryParse(shiftId, out var parsedShift) ? parsedShift : throw ApiException.Validation("shiftId", "format");
        var take = limit is null ? 50
            : int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n is >= 1 and <= 200 ? n : throw ApiException.Validation("limit", n < 1 ? "min" : "max")
            : throw ApiException.Validation("limit", "format");
        var cursor = before is null ? ((DateTimeOffset At, Guid Id)?)null : Cursor(before);
        var wantedKinds = kinds is null ? FeedKinds : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wantedKinds.Length == 0 || wantedKinds.Any(k => !FeedKinds.Contains(k, StringComparer.Ordinal)))
        {
            throw ApiException.Validation("kinds", "enum");
        }

        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        var (openId, lastClosedId) = await c.QuerySingleAsync<(Guid?, Guid?)>(
            """
            SELECT (SELECT id FROM shifts WHERE club_id = @ClubId AND closed_at IS NULL),
                   (SELECT id FROM shifts WHERE club_id = @ClubId AND closed_at IS NOT NULL ORDER BY closed_at DESC, id DESC LIMIT 1)
            """,
            new { staff.ClubId });
        var target = wanted ?? openId;
        var shift = target is null ? null : await c.QuerySingleOrDefaultAsync<ShiftRow>(
            $"SELECT {ShiftRow.Columns} FROM shifts WHERE id = @target AND club_id = @ClubId", new { target, staff.ClubId })
            ?? throw ApiException.NotFound("shift");
        if (wanted is not null && !staff.IsOwner && wanted != openId && wanted != lastClosedId)
        {
            throw ApiException.Forbidden("ownerOnly", "A cashier reads the open shift and the last closed one");
        }

        List<AdminOperation> items = [];
        string? next = null;
        if (shift is not null)
        {
            var actions = wantedKinds.Select(k => k == "debtPaid" ? "topUp" : k).Distinct().ToArray();
            var rows = (await c.QueryAsync<OperationRow>(
                $"""
                SELECT a.id, a.at, a.action, a.staff_id, a.staff_name, a.user_id, a.pc_id, a.amount, a.meta::text AS meta,
                       u.display_name AS user_name, u.role AS user_role, p.name AS pc_name
                FROM audit_entries a LEFT JOIN users u ON u.id = a.user_id LEFT JOIN pcs p ON p.id = a.pc_id
                WHERE a.club_id = @ClubId AND a.shift_id = @Id AND a.action = ANY(@actions)
                  AND NOT (a.action = 'topUp' AND coalesce((a.meta ->> 'forSession')::boolean, false))
                  AND (a.action <> 'topUp' OR CASE WHEN coalesce((a.meta ->> 'debt')::boolean, false) THEN @debtPaid ELSE @topUp END)
                  {(cursor is null ? "" : "AND (a.at, a.id) < (@at, @before)")}
                ORDER BY a.at DESC, a.id DESC
                LIMIT @take
                """,
                new
                {
                    staff.ClubId, shift.Id, actions, debtPaid = wantedKinds.Contains("debtPaid"), topUp = wantedKinds.Contains("topUp"),
                    at = cursor?.At, before = cursor?.Id, take = take + 1,
                })).ToList();
            items = [.. rows.Take(take).Select(row => row.ToWire())];
            next = rows.Count > take ? Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{ServerJson.FormatTime(items[^1].At)}|{items[^1].Id}")) : null;
        }

        return AdminJson.Ok(new AdminOperationsPage(
            shift is null ? null : new AdminOperationsShift(shift.Id, shift.StaffName, shift.OpenedAt, shift.ClosedAt, shift.ClosedByName),
            items, next, await TodayAsync(c, staff.ClubId, now)));
    }

    /// <summary>«Сегодня» of the feed: the club's ledger since local midnight — top-ups by method, cash payouts, time (charges − refunds) and the shop.</summary>
    private static async Task<AdminToday> TodayAsync(NpgsqlConnection c, Guid clubId, DateTimeOffset now)
    {
        var timeZone = await c.ExecuteScalarAsync<string>("SELECT time_zone FROM clubs WHERE id = @clubId", new { clubId }) ?? "Asia/Tashkent";
        var today = DateOnly.FromDateTime(ClubTime.Local(now, timeZone));
        var from = ClubTime.Utc(today.ToDateTime(TimeOnly.MinValue), timeZone);
        var t = await c.QuerySingleAsync<TodayRow>(
            """
            SELECT coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'cash'), 0)::bigint AS "Cash",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'card'), 0)::bigint AS "Card",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'payme'), 0)::bigint AS "Payme",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'click'), 0)::bigint AS "Click",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'uzum'), 0)::bigint AS "Uzum",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method IS NULL), 0)::bigint AS "Other",
                   coalesce(-sum(amount) FILTER (WHERE type = 'adjustment' AND method = 'cash'), 0)::bigint AS "Payouts",
                   (coalesce(-sum(amount) FILTER (WHERE type = 'charge'), 0) - coalesce(sum(amount) FILTER (WHERE type = 'refund'), 0))::bigint AS "Sessions",
                   coalesce(-sum(amount) FILTER (WHERE type = 'purchase'), 0)::bigint AS "Shop"
            FROM ledger_entries WHERE club_id = @clubId AND created_at >= @from AND created_at <= @now
            """,
            new { clubId, from, now });
        var byMethod = new AdminTopUpByMethod(t.Cash, t.Card, t.Payme, t.Click, t.Uzum, t.Other);
        return new AdminToday(
            today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), from, byMethod, t.Cash + t.Card + t.Payme + t.Click + t.Uzum + t.Other, t.Payouts,
            t.Sessions, t.Shop);
    }

    /// <summary>A feed cursor: base64url of <c>at|id</c> (the last item of a page), else <c>400 before format</c>.</summary>
    private static (DateTimeOffset At, Guid Id) Cursor(string before)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(before)).Split('|');
            if (parts.Length == 2
                && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                && Guid.TryParse(parts[1], out var id))
            {
                return (at, id);
            }
        }
        catch (FormatException)
        {
        }

        throw ApiException.Validation("before", "format");
    }

    /// <summary>
    /// The counter takes money (a top-up, a session opened or extended) only in an open shift, else <c>409 shiftClosed</c>.
    /// A plain read, first in the action's transaction: <c>shifts</c> is locked last (§4.4), by <see cref="Wallet.Ledger"/>.
    /// A top-up racing the close is refused there too (its line is <c>ShiftRequired</c>); a charge racing it gets
    /// <c>shift_id NULL</c> (§4.3, <c>noShift</c>). The club API key (an integration, no staff member, no drawer) is not gated:
    /// its rows join the open shift when there is one.
    /// </summary>
    public static async Task RequireOpenAsync(NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff)
    {
        if (staff.StaffId is not null
            && !await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM shifts WHERE club_id = @ClubId AND closed_at IS NULL)", new { staff.ClubId }, tx))
        {
            throw SessionService.Conflict("shiftClosed");
        }
    }

    /// <summary>The open shift as locked by an action that needs it: its id and the drawer it opened with.</summary>
    public sealed record OpenShiftLock(Guid Id, long OpeningCash);

    /// <summary>
    /// Locks the open shift, else <c>409 shiftClosed</c>: <c>FOR SHARE</c> (a postpaid open, cash-in — like a ledger row, it
    /// only keeps the close out) or, <paramref name="strong"/>, <c>FOR NO KEY UPDATE</c> (cash-out, payout — it also waits
    /// for every writer and another cash-out, so the drawer it checks is final; journal inserts, which take the key share
    /// of their FK, are not blocked). A transaction that takes the strong lock takes nothing after it (§4.4).
    /// </summary>
    public static async Task<OpenShiftLock> LockOpenShiftAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, bool strong) =>
        await TryLockOpenShiftAsync(c, tx, clubId, strong) ?? throw SessionService.Conflict("shiftClosed");

    /// <summary><see cref="LockOpenShiftAsync"/>, null when no shift is open.</summary>
    public static Task<OpenShiftLock?> TryLockOpenShiftAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, bool strong) =>
        c.QuerySingleOrDefaultAsync<OpenShiftLock?>(
            $"SELECT id AS \"Id\", opening_cash AS \"OpeningCash\" FROM shifts WHERE club_id = @clubId AND closed_at IS NULL {(strong ? "FOR NO KEY UPDATE" : "FOR SHARE")}",
            new { clubId }, tx);

    /// <summary>Cash may leave the drawer only up to what it is expected to hold: else <c>409 cashShort {available}</c>.</summary>
    public static async Task EnsureDrawerAsync(NpgsqlConnection c, NpgsqlTransaction tx, OpenShiftLock shift, long amount)
    {
        var available = Expected(shift.OpeningCash, await TotalsAsync(c, tx, shift.Id));
        if (available < amount)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: cashShort",
                new { reason = "cashShort", available = Money.Uzs(Math.Max(0, available)) });
        }
    }

    /// <summary>
    /// X/Z report of a shift: its ledger rows by type and method (<c>topUp</c> cash vs other methods, and each method on its
    /// own; <c>method</c> is one of the five or NULL, M0002), the cash top-ups of the club API key (<c>apiCash</c>, no staff
    /// member), the guests' cash payouts (<c>adjustment</c> rows with <c>method = cash</c>) and the drawer's movements.
    /// </summary>
    private static async Task<AdminShiftTotals> TotalsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid shiftId) =>
        (await c.QuerySingleAsync<TotalsRow>(
            """
            SELECT coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'cash'), 0)::bigint AS "Cash",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'card'), 0)::bigint AS "Card",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'payme'), 0)::bigint AS "Payme",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'click'), 0)::bigint AS "Click",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'uzum'), 0)::bigint AS "Uzum",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method IS NULL), 0)::bigint AS "Other",
                   coalesce(-sum(amount) FILTER (WHERE type = 'charge'), 0)::bigint AS "Sessions",
                   coalesce(-sum(amount) FILTER (WHERE type = 'purchase'), 0)::bigint AS "Shop",
                   coalesce(sum(amount) FILTER (WHERE type = 'refund'), 0)::bigint AS "Refunds",
                   coalesce(sum(amount) FILTER (WHERE type = 'bonus'), 0)::bigint AS "Bonuses",
                   coalesce(-sum(amount) FILTER (WHERE type = 'adjustment' AND method = 'cash'), 0)::bigint AS "Payouts",
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'cash' AND staff_id IS NULL), 0)::bigint AS "ApiCash",
                   (SELECT coalesce(sum(amount) FILTER (WHERE kind = 'in'), 0) FROM cash_movements WHERE shift_id = @shiftId)::bigint AS "CashIn",
                   (SELECT coalesce(sum(amount) FILTER (WHERE kind = 'out'), 0) FROM cash_movements WHERE shift_id = @shiftId)::bigint AS "CashOut",
                   count(*)::int AS "Count"
            FROM ledger_entries WHERE shift_id = @shiftId
            """,
            new { shiftId },
            tx)).ToWire();

    public static string Principal(StaffContext staff) => "club:" + staff.ClubId.ToString("D", CultureInfo.InvariantCulture);

    private static long Cash(long value, string field) =>
        value < 0 ? throw ApiException.Validation(field, "min") : value > MaxCash ? throw ApiException.Validation(field, "max") : value;

    private sealed class ShiftRow
    {
        public const string Columns =
            "id, staff_id, staff_name, opened_at, closed_at, opening_cash, closing_cash, totals::text AS totals, expected_cash, closed_by_name";

        public Guid Id { get; init; }
        public Guid? StaffId { get; init; }
        public string StaffName { get; init; } = "";
        public DateTimeOffset OpenedAt { get; init; }
        public DateTimeOffset? ClosedAt { get; init; }
        public long OpeningCash { get; init; }
        public long? ClosingCash { get; init; }
        public string? Totals { get; init; }
        public long? ExpectedCash { get; init; }
        public string? ClosedByName { get; init; }

        public AdminShift ToWire() => new(
            Id, StaffId?.ToString() ?? "apiKey", StaffName, OpenedAt, ClosedAt, OpeningCash, ClosingCash,
            Totals is null ? null : Split(JsonSerializer.Deserialize<AdminShiftTotals>(Totals, ServerJson.Options)!), ExpectedCash, ClosedByName);

        /// <summary>
        /// A Z report saved before the split by method: cash as it was, the rest as <c>other</c> (the counter sent no method
        /// then, so it is all cash in practice). One saved before cash desk part 2 reads its drawer fields as 0.
        /// </summary>
        private static AdminShiftTotals Split(AdminShiftTotals z) =>
            z.TopUpByMethod is null ? z with { TopUpByMethod = new(z.TopUpCash, 0, 0, 0, 0, z.TopUpOther) } : z;
    }

    private sealed class TotalsRow
    {
        public long Cash { get; init; }
        public long Card { get; init; }
        public long Payme { get; init; }
        public long Click { get; init; }
        public long Uzum { get; init; }
        public long Other { get; init; }
        public long Sessions { get; init; }
        public long Shop { get; init; }
        public long Refunds { get; init; }
        public long Bonuses { get; init; }
        public long Payouts { get; init; }
        public long ApiCash { get; init; }
        public long CashIn { get; init; }
        public long CashOut { get; init; }
        public int Count { get; init; }

        public AdminShiftTotals ToWire() => new(
            Cash, Card + Payme + Click + Uzum + Other, Sessions, Shop, Refunds, Bonuses, Count, new(Cash, Card, Payme, Click, Uzum, Other),
            CashIn, CashOut, Payouts, ApiCash);
    }

    private sealed class TodayRow
    {
        public long Cash { get; init; }
        public long Card { get; init; }
        public long Payme { get; init; }
        public long Click { get; init; }
        public long Uzum { get; init; }
        public long Other { get; init; }
        public long Payouts { get; init; }
        public long Sessions { get; init; }
        public long Shop { get; init; }
    }

    /// <summary>A journal entry of the feed with its client and PC.</summary>
    private sealed class OperationRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset At { get; init; }
        public string Action { get; init; } = "";
        public Guid? StaffId { get; init; }
        public string StaffName { get; init; } = "";
        public Guid? UserId { get; init; }
        public Guid? PcId { get; init; }
        public long Amount { get; init; }
        public string Meta { get; init; } = "{}";
        public string? UserName { get; init; }
        public string? UserRole { get; init; }
        public string? PcName { get; init; }

        /// <summary>The kind, the payment merged from the meta, and the signed effect on the drawer (staff cash only).</summary>
        public AdminOperation ToWire()
        {
            using var doc = JsonDocument.Parse(Meta);
            var meta = doc.RootElement;
            string? Text(string key) => meta.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? Number(string key) => meta.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
            bool? Flag(string key) => meta.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            Guid? Uuid(string key) => Text(key) is { } s && Guid.TryParse(s, out var g) ? g : null;

            var kind = Action == "topUp" && Flag("debt") == true ? "debtPaid" : Action;
            AdminOperationPaid? paid = kind switch
            {
                "topUp" or "debtPaid" => new AdminOperationPaid(Amount, Text("method") ?? "cash", Uuid("transactionId")),
                "sessionOpen" or "sessionExtend" when Number("paidAmount") is { } paidAmount =>
                    new AdminOperationPaid(paidAmount, Text("paidMethod") ?? "cash", Uuid("transactionId")),
                "payout" => new AdminOperationPaid(Amount, "cash", Uuid("transactionId")),
                _ => null,
            };
            var byStaff = StaffId is not null;
            var drawer = kind switch
            {
                "topUp" or "debtPaid" or "sessionOpen" or "sessionExtend" => byStaff && paid is { Method: "cash" } ? paid.Amount : 0,
                "cashIn" or "shiftOpen" => Amount,
                "cashOut" or "payout" => -Amount,
                _ => 0,
            };
            AdminOperationQuote? quote = meta.TryGetProperty("quote", out var q) && q.ValueKind == JsonValueKind.Object
                && q.TryGetProperty("base", out var b) && b.TryGetInt64(out var @base)
                ? new AdminOperationQuote(@base, q.TryGetProperty("dayPct", out var d) ? d.GetInt32() : 100, q.TryGetProperty("discountPct", out var p) ? p.GetInt32() : 0)
                : null;
            return new AdminOperation(
                Id, At, kind, StaffName,
                UserId is { } userId && UserName is not null ? new AdminOperationClient(userId, UserName, UserRole ?? "member") : null,
                PcId is { } pcId && PcName is not null ? new AdminOperationPc(pcId, PcName) : null,
                Text("tariff"), Number("minutes") is { } minutes ? (int)minutes : null, Flag("prepaid"), Amount,
                kind is "sessionOpen" or "sessionExtend" ? Amount : kind == "sessionEnd" ? Number("charged") : null,
                quote, paid, drawer, Text("reasonCode"), Text("note"), Uuid("sessionId"));
        }
    }
}
