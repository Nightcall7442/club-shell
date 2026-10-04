using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using ClubShell.Server.Sessions.Billing;
using ClubShell.Server.Wallet;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The counter (slice S4, cash desk parts 1 and 2): hall snapshot, sessions opened/extended/ended by the cashier (members
/// prepaid or postpaid, walk-in guests), top-up with the tier bonus and the exact debt payment, a guest's cash payout, PC
/// commands and the price preview. Money goes through the kiosk's own <see cref="SessionService"/> (the §5.2 rules and the
/// single <see cref="Pricing"/> function) and <see cref="Ledger"/>; each action and its <see cref="Audit"/> entry commit
/// together, <c>Idempotency-Key</c> under principal <c>club:&lt;id&gt;</c> (optional, required on the routes beyond the
/// contract). Money is taken only in an open shift: top-up, payout, open and extend answer <c>409 shiftClosed</c> without
/// one; ending a session needs none (it only gives back to the balance). A desk open and a sign-in on the same PC are
/// serialized (<see cref="AdvisoryLocks.PcAsync"/>); a desk open signs out anyone else signed in there, a desk end signs
/// out the session's player (D-27..D-29). Pushes and commands go out after the commit; <c>pcStatusChanged</c> is never sent
/// (AsyncAPI notImplemented, the console polls, §6.5). Events for the webhooks (<c>sessionOpened</c>, <c>bigTopup</c>,
/// <c>suspicious</c>) commit with the action; the automation of a top-up (<c>topupAtLeast</c>) and of an opened session runs
/// after the commit (S5).
/// </summary>
public static class CounterEndpoints
{
    public static readonly string[] Operations =
        ["adminOverview", "adminOpenSession", "adminExtend", "adminEnd", "adminTopUp", "adminCommand", "adminQuote"];

    /// <summary><c>ledger_entries.method</c> values.</summary>
    private static readonly string[] Methods = ["cash", "card", "payme", "click", "uzum"];

    /// <summary>The description of a guest's cash payout row (an <c>adjustment</c> with <c>method = cash</c>, D-37).</summary>
    public const string PayoutDescription = "Выдано наличными на кассе";

    public static void MapCounterEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin").WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapGet("/overview", OverviewAsync);
        api.MapPost("/sessions", OpenAsync);
        api.MapPost("/sessions/guest", GuestOpenAsync);
        api.MapPost("/sessions/extend", ExtendAsync);
        api.MapPost("/sessions/end", EndAsync);
        api.MapPost("/wallet/topup", TopUpAsync);
        api.MapPost("/wallet/payout", PayoutAsync);
        api.MapPost("/pcs/{pcId:guid}/command", CommandAsync);
        api.MapPost("/quote", QuoteAsync);
    }

    /// <summary>
    /// Everything the counter screen draws (polled every 2 s). ponytail: all members of the network in one list, as the mock;
    /// page or search when a club outgrows it.
    /// </summary>
    private static async Task<IResult> OverviewAsync(
        HttpContext context, NpgsqlDataSource db, PcRepository pcs, AgentSocketHub hub, AgentOptions agents, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var now = clock.GetUtcNow();
        var hall = await pcs.ListAsync(staff.ClubId);
        await using var c = await db.OpenConnectionAsync();
        var open = (await c.QueryAsync<SessionRow>($"SELECT {SessionRow.Columns} FROM sessions WHERE club_id = @ClubId AND state <> 'ended'", new { staff.ClubId }))
            .ToDictionary(s => s.PcId);
        var players = (await c.QueryAsync<(Guid Id, string DisplayName, string Role, long Balance)>(
                "SELECT u.id, u.display_name, u.role, w.main_balance FROM users u JOIN wallets w ON w.user_id = u.id WHERE u.id = ANY(@ids)",
                new { ids = open.Values.Select(s => s.UserId).ToArray() }))
            .ToDictionary(u => u.Id, u => new AdminSeatUser(u.Id, u.DisplayName, u.Role, Money.Uzs(u.Balance)));

        // D-49: a desk session nobody has signed in to yet («ждёт входа»; its clock already runs).
        var signedIn = (await c.QueryAsync<Guid>(
            """
            SELECT s.pc_id FROM sessions s JOIN user_tokens t ON t.pc_id = s.pc_id AND t.user_id = s.user_id
            WHERE s.club_id = @ClubId AND s.state <> 'ended' AND t.expires_at > @now
            """,
            new { staff.ClubId, now })).ToHashSet();
        var seats = hall.Select(pc => new AdminSeat(
            pc.ToPc(pc.Status(hub.IsConnected(pc.Id), now, TimeSpan.FromSeconds(agents.OfflineAfterSec)), withHwid: false),
            open.TryGetValue(pc.Id, out var s) ? s.ToWire(now) : null,
            s is not null && players.TryGetValue(s.UserId, out var user) ? user : null,
            s is null ? null : signedIn.Contains(pc.Id))).ToList();
        var tariffs = (await c.QueryAsync<TariffRow>(
            $"SELECT {TariffRow.Columns} FROM tariffs WHERE club_id = @ClubId AND deleted_at IS NULL ORDER BY created_at, id", new { staff.ClubId }))
            .Select(t => t.ToWire()).ToList();
        var members = (await c.QueryAsync<(Guid Id, string DisplayName, string Role, long Balance, string Username)>(
            """
            SELECT u.id, u.display_name, u.role, w.main_balance, u.username
            FROM users u JOIN wallets w ON w.user_id = u.id
            LEFT JOIN client_profiles cp ON cp.user_id = u.id AND cp.club_id = @ClubId
            WHERE u.network_id = @NetworkId AND u.deleted_at IS NULL AND NOT u.transient AND u.role NOT IN ('guest', 'admin')
              AND NOT coalesce(cp.blacklisted, false)
            ORDER BY u.display_name, u.id
            """,
            new { staff.ClubId, staff.NetworkId }))
            .Select(u => new AdminMember(u.Id, u.DisplayName, u.Role, Money.Uzs(u.Balance), u.Username)).ToList();
        // Players of this club left with a postpaid bill («Расчёт с гостями и долги»): guests (their transient accounts are
        // never in the client list) and members (limits.memberDebtLimit), settled by a top-up {settleDebt}.
        var debts = (await c.QueryAsync<(Guid Id, string DisplayName, long Balance, string? Pc, DateTimeOffset? EndedAt, string Role)>(
            """
            SELECT u.id, u.display_name, w.main_balance, last.pc, last.ended_at, u.role
            FROM users u JOIN wallets w ON w.user_id = u.id
            CROSS JOIN LATERAL (
                SELECT p.name AS pc, s.ended_at FROM sessions s JOIN pcs p ON p.id = s.pc_id
                WHERE s.user_id = u.id AND s.club_id = @ClubId ORDER BY s.started_at DESC, s.id DESC LIMIT 1) last
            WHERE u.network_id = @NetworkId AND (u.role = 'guest' OR NOT u.transient) AND u.deleted_at IS NULL AND w.main_balance < 0
            ORDER BY last.ended_at DESC NULLS FIRST, u.id
            LIMIT 100
            """,
            new { staff.ClubId, staff.NetworkId }))
            .Select(d => new AdminGuestDebt(d.Id, d.DisplayName, Money.Uzs(-d.Balance), d.Pc, d.EndedAt, d.Role)).ToList();

        // Walk-in guests of this club with money left after the desk ended their session (D-37), and what may go back in cash.
        var refunds = (await c.QueryAsync<(Guid Id, string DisplayName, long Balance, long Payable, string? Pc, DateTimeOffset? EndedAt)>(
            $"""
            SELECT u.id, u.display_name, w.main_balance, {PayableSql}, last.pc, last.ended_at
            FROM users u JOIN wallets w ON w.user_id = u.id
            CROSS JOIN LATERAL (
                SELECT p.name AS pc, s.ended_at FROM sessions s JOIN pcs p ON p.id = s.pc_id
                WHERE s.user_id = u.id AND s.club_id = @ClubId ORDER BY s.started_at DESC, s.id DESC LIMIT 1) last
            {PayableJoin}
            WHERE u.network_id = @NetworkId AND u.transient AND u.deleted_at IS NULL AND w.main_balance > 0
              AND NOT EXISTS (SELECT 1 FROM sessions o WHERE o.user_id = u.id AND o.state <> 'ended')
            ORDER BY last.ended_at DESC NULLS FIRST, u.id
            LIMIT 100
            """,
            new { staff.ClubId, staff.NetworkId }))
            .Select(r => new AdminGuestRefund(r.Id, r.DisplayName, Money.Uzs(r.Balance), Money.Uzs(r.Payable), r.Pc, r.EndedAt)).ToList();
        // The wrench on the map: the worst open repair ticket of each PC ("Состояние ПК"), so nobody is seated at it.
        var repairs = (await c.QueryAsync<(Guid PcId, string Severity)>(
            """
            SELECT pc_id, CASE WHEN bool_or(severity = 'high') THEN 'high' ELSE 'medium' END
            FROM health_tickets WHERE club_id = @ClubId AND status IN ('open', 'inWork') GROUP BY pc_id
            """,
            new { staff.ClubId }))
            .Select(r => (object)new { pcId = r.PcId, severity = r.Severity }).ToList();
        return AdminJson.Ok(new AdminOverview(
            now, new AdminOccupancy(seats.Count(s => s.Pc.Status == Contracts.Pcs.PcStatus.Free), seats.Count), seats, tariffs, members,
            await ZonesAsync(c, staff.ClubId), repairs, debts, refunds));
    }

    /// <summary>
    /// D-37, the safe default: a guest's cash payout never exceeds the balance, the money a desk end gave back (refund rows
    /// exist only for admin/error ends) or the cash the guest actually paid — each less what was paid out already. A card
    /// or Payme payer gets no cash back, and bonus money (credited to the main balance) can never leave as cash. Joined as
    /// <c>m</c> on <c>u</c>/<c>w</c> (<see cref="PayableJoin"/>).
    /// </summary>
    private const string PayableSql = "greatest(0, least(w.main_balance, m.refunds - m.payouts, m.cash - m.payouts))::bigint AS payable";

    private const string PayableJoin = """
        CROSS JOIN LATERAL (
            SELECT coalesce(sum(amount) FILTER (WHERE type = 'refund'), 0) AS refunds,
                   coalesce(-sum(amount) FILTER (WHERE type = 'adjustment' AND method = 'cash'), 0) AS payouts,
                   coalesce(sum(amount) FILTER (WHERE type = 'topUp' AND method = 'cash'), 0) AS cash
            FROM ledger_entries WHERE user_id = u.id) m
        """;

    /// <summary>What may be paid out to <paramref name="userId"/> in cash now (<see cref="PayableSql"/>), in the caller's transaction.</summary>
    private static Task<long> PayableAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId) =>
        c.ExecuteScalarAsync<long>($"SELECT {PayableSql} FROM users u JOIN wallets w ON w.user_id = u.id {PayableJoin} WHERE u.id = @userId", new { userId }, tx);

    /// <summary>
    /// <c>adminOpenSession</c> (§5.3): priced by the club rules; <c>201 {session, charged, balance, user, payment}</c>.
    /// <c>prepaid</c> (default true; beyond the contract, D-30): false is postpaid from the desk — no minutes bought, the
    /// member's balance plus <c>limits.memberDebtLimit</c> (a guest: <c>limits.guestPostpaid</c>/<c>guestDebtLimit</c>) pays the
    /// frozen price at the end, and no <c>payment</c> may come with it. With <c>payment</c> (the desk's "Посадить · Наличные")
    /// the top-up is posted first in the same transaction, so a session the rules refuse (blacklist, curfew, a busy PC, a
    /// changed price) leaves no money booked. The wallet is then locked before the session row is inserted; only a kiosk
    /// create of the same player at that instant can deadlock with it, and Postgres refuses one of the two.
    /// </summary>
    private static async Task<IResult> OpenAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, PcRepository pcs,
        AutomationService automation, ILoggerFactory logs)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminOpenSessionRequest>(body, "pcId", "userId", "tariffId", "minutes");
        var minutes = Minutes(r.Minutes!.Value);
        var prepaid = r.Prepaid ?? true;
        var payment = PaymentOf(r.Payment);
        if (!prepaid && payment is not null)
        {
            throw ApiException.Validation("payment", "postpaid");
        }

        var pc = await LivePcAsync(pcs, staff, r.PcId!.Value);
        var effects = new SessionEffects();
        TopUpPosted? paid = null;
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            await AdvisoryLocks.PcAsync(c, tx, pc.Id);
            await ShiftEndpoints.RequireOpenAsync(c, tx, staff);
            if (payment is { } pay)
            {
                paid = await PostTopUpAsync(c, tx, staff, effects, r.UserId!.Value, pay.Amount, pay.Method, sessions.Clock.GetUtcNow(), forSession: true);
            }

            var session = await sessions.CreateAsync(
                c, tx, pc, new SessionCreateRequest(pc.Id, r.UserId!.Value, r.TariffId!.Value, minutes, prepaid), replay: false, effects, staff);
            var body201 = await SeatedAsync(c, tx, staff, pc, session, paid, effects, sessions.Clock.GetUtcNow(), guest: false);
            return new IdempotentResult(StatusCodes.Status201Created, body201);
        });
        await sessions.PublishAsync(effects);
        await AfterTopUpAsync(automation, logs, staff, paid);
        return result;
    }

    /// <summary>
    /// <c>POST /admin/sessions/guest</c> (beyond the contract, D-24): a walk-in guest — a transient guest account (named
    /// <c>displayName</c> or «Гость &lt;PC number&gt;») and its seat in one transaction, so a refused seat leaves no account,
    /// money or journal entry. Prepaid needs a <c>payment</c> of exactly the price now (D-48, else <c>409 priceChanged
    /// {total}</c>): no extra money sits on a throwaway account; postpaid follows <c>limits.guestPostpaid</c> and takes no
    /// payment. The guest gets no login code: pressing «Гость» on that PC signs in to this session (D-25). Staff only (the
    /// club API key has no drawer and opens no seats for guests, <c>403 staffOnly</c>); <c>Idempotency-Key</c> required.
    /// </summary>
    private static async Task<IResult> GuestOpenAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, PcRepository pcs,
        AutomationService automation, ILoggerFactory logs)
    {
        var staff = StaffOnly(context);
        var r = Api.Read<AdminGuestSessionRequest>(body, "pcId", "tariffId", "minutes");
        var minutes = Minutes(r.Minutes!.Value);
        var prepaid = r.Prepaid ?? true;
        var name = r.DisplayName?.Trim() is { Length: > 0 } given ? given : null;
        if (name is { Length: > 32 })
        {
            throw ApiException.Validation("displayName", "max");
        }

        var payment = PaymentOf(r.Payment);
        if (prepaid != (payment is not null))
        {
            throw ApiException.Validation("payment", prepaid ? "required" : "postpaid");
        }

        var pc = await LivePcAsync(pcs, staff, r.PcId!.Value);
        var effects = new SessionEffects();
        TopUpPosted? paid = null;
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: true, body, async (c, tx) =>
        {
            var now = sessions.Clock.GetUtcNow();
            await AdvisoryLocks.PcAsync(c, tx, pc.Id);
            await ShiftEndpoints.RequireOpenAsync(c, tx, staff);
            var guest = await Guests.CreateAsync(c, tx, staff.NetworkId, pc.Number, name ?? $"Гость {pc.Number}", "ru", now);
            if (payment is { } pay)
            {
                await ExactPriceAsync(c, tx, pc.ClubId, guest, pc.Zone, r.TariffId!.Value, minutes, pay.Amount, now);
                paid = await PostTopUpAsync(c, tx, staff, effects, guest, pay.Amount, pay.Method, now, forSession: true);
            }

            var session = await sessions.CreateAsync(
                c, tx, pc, new SessionCreateRequest(pc.Id, guest, r.TariffId!.Value, minutes, prepaid), replay: false, effects, staff);
            return new IdempotentResult(StatusCodes.Status201Created, await SeatedAsync(c, tx, staff, pc, session, paid, effects, now, guest: true));
        });
        await sessions.PublishAsync(effects);
        await AfterTopUpAsync(automation, logs, staff, paid);
        return result;
    }

    /// <summary>
    /// The rest of a desk open after the session row, in its transaction: a postpaid open locks the open shift (D-32: no
    /// ledger row does it for it, and its journal entry must not race the close into <c>noShift</c>), anyone else signed in
    /// on the PC is signed out (<c>seatTaken</c>, D-29), the journal entry with the price and the payment merged (the feed
    /// shows one row, D-43), and the answer.
    /// </summary>
    private static async Task<JsonElement> SeatedAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, PcRow pc, Session session, TopUpPosted? paid, SessionEffects effects,
        DateTimeOffset now, bool guest)
    {
        // The club API key opens without a shift (RequireOpenAsync does not gate it): its entry then joins none.
        var shiftId = session.IsPrepaid ? null
            : staff.StaffId is null ? (await ShiftEndpoints.TryLockOpenShiftAsync(c, tx, staff.ClubId, strong: false))?.Id
            : (await ShiftEndpoints.LockOpenShiftAsync(c, tx, staff.ClubId, strong: false)).Id;
        foreach (var other in await c.QueryAsync<Guid>(
            "DELETE FROM user_tokens WHERE pc_id = @PcId AND user_id <> @UserId RETURNING user_id", new { session.PcId, session.UserId }, tx))
        {
            effects.RevokedBefore.Add((other, pc.Id, "seatTaken"));
        }

        var row = await c.QuerySingleAsync<SeatRow>(
            """
            SELECT u.display_name AS who, u.role, t.name AS tariff, s.discount_pct, s.day_pct, s.price_per_hour_snapshot, s.purchased_sec, w.main_balance AS balance
            FROM sessions s JOIN users u ON u.id = s.user_id JOIN tariffs t ON t.id = s.tariff_id JOIN wallets w ON w.user_id = s.user_id
            WHERE s.id = @Id
            """,
            new { session.Id }, tx);
        var quote = await QuoteOfAsync(c, tx, session.Id) ?? new AdminOperationQuote(Pricing.Base(row.PricePerHourSnapshot, 60), row.DayPct, row.DiscountPct);
        await Audit.WriteAsync(c, tx, staff, now, "sessionOpen", session.UserId, pc.Id, session.Cost.Amount, $"{row.Who} · {pc.Name} · {row.Tariff}",
            new
            {
                minutes = row.PurchasedSec / 60, prepaid = session.IsPrepaid, tariff = row.Tariff, discountPct = row.DiscountPct, sessionId = session.Id, quote,
                paidAmount = paid?.Amount, paidMethod = paid?.Method, transactionId = paid?.Id, guest = guest ? true : (bool?)null,
            },
            shiftId);
        return AdminJson.ToElement(new AdminSessionResult(
            session, session.Cost, Money.Uzs(row.Balance), User: new AdminSessionUser(session.UserId, row.Who, row.Role),
            Payment: paid is null ? null : new AdminSessionPaid(paid.Transaction, Money.Uzs(paid.Bonus))));
    }

    /// <summary>The price of the session's latest charge as the feed shows it (<c>meta.quote</c> of the ledger row), else null.</summary>
    private static async Task<AdminOperationQuote?> QuoteOfAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId) =>
        await c.QuerySingleOrDefaultAsync<(long Base, int DayPct, int DiscountPct)?>(
            """
            SELECT (q->>'base')::bigint, (q->>'dayPct')::int, (q->>'discountPct')::int
            FROM (SELECT coalesce(meta->'quote', meta) AS q FROM ledger_entries
                  WHERE session_id = @sessionId AND type = 'charge' AND (coalesce(meta->'quote', meta)->>'base') IS NOT NULL
                  ORDER BY created_at DESC, id DESC LIMIT 1) last
            """,
            new { sessionId }, tx) is { } q ? new AdminOperationQuote(q.Base, q.DayPct, q.DiscountPct) : null;

    /// <summary>
    /// D-48: a guest pays exactly the price at this moment — the single price function for that buyer, tariff (a package's
    /// own minutes) and PC zone — else <c>409 priceChanged {total}</c> and the whole seat is rolled back.
    /// </summary>
    private static async Task ExactPriceAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, Guid userId, string zone, Guid tariffId, int minutes, long amount, DateTimeOffset now)
    {
        var club = await SessionService.ClubAsync(c, tx, clubId);
        var tariff = await SessionService.TariffAsync(c, tx, clubId, tariffId) ?? throw ApiException.NotFound("tariff");
        var buyer = await SessionService.BuyerAsync(c, tx, club, userId) ?? throw ApiException.NotFound("user");
        var total = Pricing.Compute(tariff, tariff.IsPackage ? tariff.PackageMinutes!.Value : minutes, buyer.GroupId, buyer.LifetimeSpent, zone, now, club.Pricing).Total;
        if (amount != total)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: priceChanged", new { reason = "priceChanged", total = Money.Uzs(total) });
        }
    }

    /// <summary>
    /// <c>adminExtend</c> (§5.6): the kiosk's extend, plus <c>extendSession {charge:false}</c> to the PC. With <c>payment</c> the
    /// session's player is topped up in the same transaction, after the session row is locked (§4.4); a transient guest pays
    /// exactly the price (D-48). A package tariff sells its own minutes whatever <c>minutes</c> says; the journal names the
    /// minutes actually bought, the price and the payment.
    /// </summary>
    private static async Task<IResult> ExtendAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, AutomationService automation, ILoggerFactory logs)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminSessionTarget>(body, "minutes");
        var minutes = Minutes(r.Minutes!.Value);
        var payment = PaymentOf(r.Payment);
        var effects = new SessionEffects();
        TopUpPosted? paid = null;
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = sessions.Clock.GetUtcNow();
            await ShiftEndpoints.RequireOpenAsync(c, tx, staff);
            var s = await TargetAsync(c, tx, staff, r);
            if (payment is { } pay)
            {
                if (await c.ExecuteScalarAsync<bool>("SELECT transient FROM users WHERE id = @UserId", new { s.UserId }, tx))
                {
                    var zone = await c.ExecuteScalarAsync<string>("SELECT zone FROM pcs WHERE id = @PcId", new { s.PcId }, tx) ?? "";
                    await ExactPriceAsync(c, tx, s.ClubId, s.UserId, zone, r.TariffId ?? s.TariffId, minutes, pay.Amount, now);
                }

                paid = await PostTopUpAsync(c, tx, staff, effects, s.UserId, pay.Amount, pay.Method, now, forSession: true);
            }

            var (session, charged) = await sessions.ExtendAsync(c, tx, s.Id, s.UserId, s.PcId, minutes, r.TariffId, effects, staff);
            var (balance, tariff, purchased) = await c.QuerySingleAsync<(long, string, int)>(
                "SELECT w.main_balance, t.name, s.purchased_sec FROM sessions s JOIN tariffs t ON t.id = s.tariff_id JOIN wallets w ON w.user_id = s.user_id WHERE s.id = @Id",
                new { s.Id }, tx);
            var bought = (purchased - s.PurchasedSec) / 60;
            await Audit.WriteAsync(c, tx, staff, now, "sessionExtend", s.UserId, s.PcId, charged, $"+{bought}",
                new
                {
                    minutes = bought, tariff, sessionId = s.Id, quote = await QuoteOfAsync(c, tx, s.Id),
                    paidAmount = paid?.Amount, paidMethod = paid?.Method, transactionId = paid?.Id,
                });
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminSessionResult(
                session, Money.Uzs(charged), Money.Uzs(balance), Payment: paid is null ? null : new AdminSessionPaid(paid.Transaction, Money.Uzs(paid.Bonus)))));
        });
        await sessions.PublishAsync(effects);
        await AfterTopUpAsync(automation, logs, staff, paid);
        return result;
    }

    /// <summary>
    /// <c>adminEnd</c> (§5.7): settled with reason <c>admin</c> (unused hourly time refunded pro rata to what was paid) and
    /// <c>endSession</c> queued for the PC — without a <c>sessionUpdated</c> push, which the agent would take first and close
    /// the session itself. The player is signed out of the PC whatever the role (D-28: otherwise the kiosk would start
    /// postpaid on a member's balance again, or let a guest's refund be played away), <c>userRevoked</c> after the command.
    /// The answer adds the balance after the settlement (negative — a debt to take, D-33), the player and, for a transient
    /// guest, what may be paid out in cash now (D-37). Needs no shift: it only gives back to the balance.
    /// </summary>
    private static async Task<IResult> EndAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminSessionTarget>(body);
        var effects = new SessionEffects();
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = sessions.Clock.GetUtcNow();
            var s = await TargetAsync(c, tx, staff, r);
            var (charged, refunded) = await sessions.SettleAsync(c, tx, s, now, SessionEndReason.Admin, effects);
            var session = effects.Sessions[^1];
            effects.Sessions.RemoveAll(pushed => pushed.Id == s.Id);
            effects.Commands.Add((s.ClubId, s.PcId, NewCommand.EndSession(new EndSessionCommand(s.Id, SessionEndReason.Admin))));
            await SessionService.SignOutAsync(c, tx, s.UserId, s.PcId, effects);
            var who = await c.QuerySingleAsync<(string DisplayName, string Role, bool Transient, long Balance)>(
                "SELECT u.display_name, u.role, u.transient, w.main_balance FROM users u JOIN wallets w ON w.user_id = u.id WHERE u.id = @UserId",
                new { s.UserId }, tx);
            var sessionMinutes = (int)(now - s.StartedAt).TotalMinutes;
            await Audit.WriteAsync(c, tx, staff, now, "sessionEnd", s.UserId, s.PcId, refunded, "",
                new { sessionMinutes, sessionId = s.Id, charged, prepaid = s.IsPrepaid, guest = who.Transient ? true : (bool?)null });
            await ControlAlerts.SessionEndedAsync(c, tx, staff, refunded, sessionMinutes, now);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminSessionResult(
                session, Money.Uzs(charged), Money.Uzs(who.Balance), Money.Uzs(refunded), new AdminSessionUser(s.UserId, who.DisplayName, who.Role),
                Payable: who.Transient ? Money.Uzs(await PayableAsync(c, tx, s.UserId)) : null)));
        });
        await sessions.PublishAsync(effects);
        return result;
    }

    /// <summary>
    /// <c>adminTopUp</c>: the top-up and the tier bonus (<c>settings.bonusTiers</c>, highest reached tier, half-up to 100 tiyin)
    /// are one ledger operation; the answer carries the top-up row as <c>transaction</c>. The bonus counts only the new money
    /// above a debt (D-35) and a transient guest gets none (D-36). <c>settleDebt</c> (beyond the contract, D-34) takes a
    /// postpaid debt: exactly <c>−balance</c> in tiyin (<c>409 debtChanged {debt}</c>, none — <c>409 noDebt</c>), no bonus, no
    /// <c>bigTopup</c>, no <c>topupAtLeast</c>; the journal marks it <c>debt</c> (the feed's <c>debtPaid</c>).
    /// </summary>
    private static async Task<IResult> TopUpAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, AutomationService automation, ILoggerFactory logs)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminTopUpRequest>(body, "userId", "amount");
        var (amount, method) = Payment(r.Amount, r.Method ?? "cash", "");
        var settleDebt = r.SettleDebt ?? false;
        var effects = new SessionEffects();
        TopUpPosted? paid = null;
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            await ShiftEndpoints.RequireOpenAsync(c, tx, staff);
            paid = await PostTopUpAsync(c, tx, staff, effects, r.UserId!.Value, amount, method, sessions.Clock.GetUtcNow(), settleDebt: settleDebt);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminTopUpResponse(Money.Uzs(paid.Balance), paid.Transaction, Money.Uzs(paid.Bonus))));
        });
        await sessions.PublishAsync(effects);
        await AfterTopUpAsync(automation, logs, staff, settleDebt ? null : paid);
        return result;
    }

    /// <summary>A posted counter top-up: its ledger row, the balance after it, the tier bonus and how it was paid.</summary>
    private sealed record TopUpPosted(Guid Id, Guid UserId, long Amount, long Balance, long Bonus, Transaction Transaction, string Method);

    /// <summary>
    /// A counter top-up in the caller's transaction: the wallet locked first (§4.4: wallet → shifts), the top-up and the tier
    /// bonus as one ledger operation (the top-up row must join the open shift, <see cref="LedgerLine.ShiftRequired"/>, unless
    /// the club API key took it), its journal entry and the <c>bigTopup</c> event. The bonus is of the new money only, above
    /// the debt the top-up first repays (D-35); a transient guest and a debt payment get none (D-34, D-36).
    /// <paramref name="forSession"/>: paid with an open or extend, whose journal entry shows it (the feed hides this one).
    /// </summary>
    private static async Task<TopUpPosted> PostTopUpAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, SessionEffects effects, Guid userId, long amount, string method, DateTimeOffset now,
        bool forSession = false, bool settleDebt = false)
    {
        var wallet = await c.QuerySingleOrDefaultAsync<(long Balance, string Who, bool Transient)?>(
            """
            SELECT w.main_balance, u.display_name, u.transient FROM wallets w JOIN users u ON u.id = w.user_id
            WHERE u.id = @userId AND u.network_id = @NetworkId AND u.deleted_at IS NULL
            FOR UPDATE OF w
            """,
            new { userId, staff.NetworkId }, tx)
            ?? throw ApiException.NotFound("user");
        var debt = Math.Max(0, -wallet.Balance);
        if (settleDebt && debt == 0)
        {
            throw SessionService.Conflict("noDebt");
        }

        if (settleDebt && amount != debt)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: debtChanged", new { reason = "debtChanged", debt = Money.Uzs(debt) });
        }

        var tiers = await c.ExecuteScalarAsync<string?>("SELECT (settings -> 'bonusTiers')::text FROM clubs WHERE id = @ClubId", new { staff.ClubId }, tx);
        var bonus = wallet.Transient || settleDebt ? 0 : Bonus(Math.Max(0, amount - debt), tiers);
        var id = Guid.CreateVersion7(now);
        var balance = await Ledger.PostAsync(c, tx, userId, allowOverdraft: false, now,
            new LedgerLine("topUp", amount, TopUpDescription(method), staff.ClubId, Method: method, StaffId: staff.StaffId, Id: id,
                ShiftRequired: staff.StaffId is not null),
            new LedgerLine("bonus", bonus, "Бонус за пополнение", staff.ClubId, StaffId: staff.StaffId));
        var row = await c.QuerySingleAsync<(long BalanceAfter, string Description, DateTimeOffset CreatedAt)>(
            "SELECT balance_after, description, created_at FROM ledger_entries WHERE id = @id", new { id }, tx);
        await Audit.WriteAsync(c, tx, staff, now, "topUp", userId, amount: amount, detail: wallet.Who,
            meta: new { method, bonus, transactionId = id, forSession = forSession ? true : (bool?)null, debt = settleDebt ? true : (bool?)null });
        if (!settleDebt && amount >= await ClubSettingsEndpoints.BigTopupAtAsync(c, tx, staff.ClubId))
        {
            await Webhooks.EnqueueAsync(c, tx, staff.ClubId, "bigTopup", now, $"{wallet.Who}: {Webhooks.Sum(amount)}", new { userId, amount });
        }

        effects.Wallets.Add(userId);
        var transaction = new Transaction(id, userId, TransactionType.TopUp, Money.Uzs(amount), Money.Uzs(row.BalanceAfter), row.Description, row.CreatedAt);
        return new TopUpPosted(id, userId, amount, balance, bonus, transaction, method);
    }

    /// <summary>
    /// <c>POST /admin/wallet/payout</c> (beyond the contract, D-37): a walk-in guest's money given back in cash at the desk —
    /// exactly what is payable now (<see cref="PayableSql"/>, else <c>409 payableChanged {payable}</c>), only to a transient
    /// guest (<c>409 notGuest</c>) who is not playing (<c>409 guestPlaying</c>), only in an open shift whose drawer holds it
    /// (<c>409 cashShort {available}</c>). The wallet is locked first, then the shift <c>FOR NO KEY UPDATE</c> (it waits for
    /// every ledger writer and another cash-out, and nothing is locked after it, §4.4). Ledger: an <c>adjustment</c> of
    /// −amount with <c>method = cash</c>, counted as <c>payouts</c> in X/Z. Staff only; <c>Idempotency-Key</c> required.
    /// </summary>
    private static async Task<IResult> PayoutAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions)
    {
        var staff = StaffOnly(context);
        var r = Api.Read<AdminPayoutRequest>(body, "userId", "amount");
        var amount = r.Amount!.Value is < 1 ? throw ApiException.Validation("amount", "min")
            : r.Amount.Value > 100_000_000 ? throw ApiException.Validation("amount", "max") : r.Amount.Value;
        if (r.Method is not (null or "cash"))
        {
            throw ApiException.Validation("method", "enum");
        }

        var effects = new SessionEffects();
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: true, body, async (c, tx) =>
        {
            var now = sessions.Clock.GetUtcNow();
            var userId = r.UserId!.Value;
            var who = await c.QuerySingleOrDefaultAsync<(string DisplayName, bool Transient)?>(
                """
                SELECT u.display_name, u.transient FROM wallets w JOIN users u ON u.id = w.user_id
                WHERE u.id = @userId AND u.network_id = @NetworkId AND u.deleted_at IS NULL
                FOR UPDATE OF w
                """,
                new { userId, staff.NetworkId }, tx)
                ?? throw ApiException.NotFound("user");
            if (!who.Transient)
            {
                throw SessionService.Conflict("notGuest");
            }

            if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM sessions WHERE user_id = @userId AND state <> 'ended')", new { userId }, tx))
            {
                throw SessionService.Conflict("guestPlaying");
            }

            var payable = await PayableAsync(c, tx, userId);
            if (payable == 0 || amount != payable)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: payableChanged",
                    new { reason = "payableChanged", payable = Money.Uzs(payable) });
            }

            var shift = await ShiftEndpoints.LockOpenShiftAsync(c, tx, staff.ClubId, strong: true);
            await ShiftEndpoints.EnsureDrawerAsync(c, tx, shift, amount);
            var id = Guid.CreateVersion7(now);
            var balance = await Ledger.PostAsync(c, tx, userId, allowOverdraft: false, now,
                new LedgerLine("adjustment", -amount, PayoutDescription, staff.ClubId, Method: "cash", StaffId: staff.StaffId, Id: id, ShiftRequired: true));
            var row = await c.QuerySingleAsync<(long BalanceAfter, DateTimeOffset CreatedAt)>(
                "SELECT balance_after, created_at FROM ledger_entries WHERE id = @id", new { id }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "payout", userId, amount: amount, detail: who.DisplayName,
                meta: new { method = "cash", transactionId = id }, shiftId: shift.Id);
            effects.Wallets.Add(userId);
            var transaction = new Transaction(id, userId, TransactionType.Adjustment, Money.Uzs(-amount), Money.Uzs(row.BalanceAfter), PayoutDescription, row.CreatedAt);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminPayoutResponse(
                Money.Uzs(balance), Money.Uzs(await PayableAsync(c, tx, userId)), transaction)));
        });
        await sessions.PublishAsync(effects);
        return result;
    }

    /// <summary>The staff member of a route the club API key may not use (it has no drawer): else <c>403 staffOnly</c>.</summary>
    public static StaffContext StaffOnly(HttpContext context)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        return staff.StaffId is not null ? staff : throw ApiException.Forbidden("staffOnly", "Only a staff member can do this, not the club API key");
    }

    /// <summary>The automation of a committed top-up (<c>topupAtLeast</c>, S5); its failure never fails the top-up. Nothing on a replay.</summary>
    private static async Task AfterTopUpAsync(AutomationService automation, ILoggerFactory logs, StaffContext staff, TopUpPosted? paid)
    {
        if (paid is null)
        {
            return;
        }

        try
        {
            await automation.TopUpAsync(staff.ClubId, paid.UserId, paid.Amount, paid.Id);
        }
        catch (NpgsqlException ex)
        {
            logs.CreateLogger(typeof(CounterEndpoints).FullName!).LogWarning(ex, "Automation after a top-up failed");
        }
    }

    /// <summary>The <c>payment</c> of an open or extend (its method required), else none.</summary>
    private static (long Amount, string Method)? PaymentOf(AdminPayment? payment) =>
        payment is null ? null : Payment(payment.Amount, payment.Method ?? throw ApiException.Validation("payment.method", "required"), "payment.");

    /// <summary>Money the counter takes: 1 tiyin … 1 000 000 сум by one of <see cref="Methods"/>, else <c>400</c> naming the field.</summary>
    private static (long Amount, string Method) Payment(long? amount, string method, string prefix)
    {
        var a = amount ?? throw ApiException.Validation(prefix + "amount", "required");
        if (a is < 1 or > 100_000_000)
        {
            throw ApiException.Validation(prefix + "amount", a < 1 ? "min" : "max");
        }

        return Methods.Contains(method, StringComparer.Ordinal) ? (a, method) : throw ApiException.Validation(prefix + "method", "enum");
    }

    /// <summary>The bonus of <c>AdminBonusTier[]</c> <c>{minAmount, bonusPct}</c>: the highest tier reached, half-up to 100 tiyin.</summary>
    public static long Bonus(long amount, string? tiersJson)
    {
        var tiers = string.IsNullOrEmpty(tiersJson) ? [] : JsonSerializer.Deserialize<BonusTier[]>(tiersJson, JsonSerializerOptions.Web) ?? [];
        var pct = tiers.Where(t => amount >= t.MinAmount).OrderByDescending(t => t.MinAmount).Select(t => t.BonusPct).FirstOrDefault();
        return ((amount * pct) + 5_000) / 10_000 * 100;
    }

    /// <summary>The top-up row's description, which the player also reads in the kiosk's wallet history: words, not the method code.</summary>
    private static string TopUpDescription(string method) => method switch
    {
        "cash" => "Пополнение на кассе наличными",
        "card" => "Пополнение на кассе картой",
        _ => $"Пополнение на кассе через {char.ToUpperInvariant(method[0])}{method[1..]}",
    };

    /// <summary>
    /// <c>adminCommand</c> (§6.4 step 4): queued, then its ack awaited up to <see cref="AgentOptions.AckWaitSec"/>; an offline PC
    /// answers <c>agentOffline</c> at once (the command stays queued), no ack in time <c>timeout</c> — both inside a 200.
    /// <c>unlock</c> supersedes the PC's pending <c>lock</c>.
    /// </summary>
    private static async Task<IResult> CommandAsync(
        HttpContext context, Guid pcId, [FromBody] JsonElement body, PcRepository pcs, NpgsqlDataSource db, CommandDispatcher dispatcher,
        CommandRepository commands, AgentSocketHub hub, AgentOptions agents, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var pc = await LivePcAsync(pcs, staff, pcId);
        var r = Api.Read<AdminPcCommandRequest>(body, "kind");
        if (r.Text is { Length: > 500 })
        {
            throw ApiException.Validation("text", "max");
        }

        var text = string.IsNullOrWhiteSpace(r.Text) ? null : r.Text;
        var command = r.Kind switch
        {
            "message" => NewCommand.Message(new MessageCommand(Guid.NewGuid(), "Администратор", text ?? throw ApiException.Validation("text", "required"), NotificationLevel.Info, RequiresAck: true)),
            "lock" => NewCommand.Lock(new LockCommand("staff", text)),
            "unlock" => NewCommand.Unlock(),
            "reboot" => NewCommand.Reboot(new PowerCommand(5, false, text)),
            "shutdown" => NewCommand.Shutdown(new PowerCommand(5, false, text)),
            _ => throw ApiException.Validation("kind", "unknown"),
        };

        // The command and its audit entry commit together (DESIGN §3.7); it goes to the PC after the commit.
        var online = hub.IsConnected(pc.Id);
        ServerCommandEnvelope queued;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var supersedes = r.Kind != "unlock" ? null : await c.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM agent_commands WHERE pc_id = @Id AND name = 'lock' AND acked_at IS NULL AND superseded_at IS NULL ORDER BY created_at DESC, id DESC LIMIT 1",
                new { pc.Id }, tx);
            queued = await dispatcher.QueueAsync(tx, pc.ClubId, pc.Id, command, supersedes, staff.StaffId);
            await Audit.WriteAsync(c, tx, staff, clock.GetUtcNow(), "pcCommand", pcId: pc.Id, detail: $"{pc.Name} · {r.Kind}", meta: new { kind = r.Kind });
            await tx.CommitAsync();
        }

        await dispatcher.SendAsync(pc.Id, queued);
        var ack = !online
            ? new CommandAck(false, IpcError.Of(ErrorCode.AgentOffline, "The PC is offline; the command stays queued"))
            : await commands.WaitForAckAsync(queued.Id, TimeSpan.FromSeconds(agents.AckWaitSec), context.RequestAborted)
              ?? new CommandAck(false, IpcError.Of(ErrorCode.Timeout, "No acknowledgement from the PC in time"));
        return AdminJson.Ok(new { ack });
    }

    /// <summary>
    /// <c>adminQuote</c>: the single price function now, without charging. An unknown <c>userId</c> is priced as a walk-in
    /// (the contract's 404 names only tariff and pc). Beyond the contract: <c>rule</c> — the tariff's own refusal now
    /// (<see cref="SessionService.TariffRule"/>: zone or time window; the buyer's rules are checked only on open) and
    /// <c>minutes</c> — what the price is for (a package's own minutes).
    /// </summary>
    private static async Task<IResult> QuoteAsync(HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminQuoteRequest>(body, "tariffId", "pcId");
        await using var c = await db.OpenConnectionAsync();
        var club = await SessionService.ClubAsync(c, null, staff.ClubId);
        var tariff = await SessionService.TariffAsync(c, null, club.Id, r.TariffId!.Value) ?? throw ApiException.NotFound("tariff");
        var zone = await c.QuerySingleOrDefaultAsync<string>(
            "SELECT zone FROM pcs WHERE id = @PcId AND club_id = @ClubId AND deleted_at IS NULL", new { r.PcId, staff.ClubId })
            ?? throw ApiException.NotFound("pc");
        var minutes = tariff.IsPackage ? tariff.PackageMinutes!.Value : Minutes(r.Minutes ?? throw ApiException.Validation("minutes", "required"));
        var buyer = r.UserId is { } userId ? await SessionService.BuyerAsync(c, null, club, userId) : null;
        var now = clock.GetUtcNow();
        var q = Pricing.Compute(tariff, minutes, buyer?.GroupId, buyer?.LifetimeSpent, zone, now, club.Pricing);
        return AdminJson.Ok(new AdminPriceQuote(
            Money.Uzs(q.Base), q.DayPct, q.DiscountPct, q.DiscountReason, Money.Uzs(q.Total), SessionService.TariffRule(zone, tariff, club, now), minutes));
    }

    /// <summary><c>settings.zones</c> (<c>AdminZone[]</c>); none configured — no zones.</summary>
    public static async Task<IReadOnlyList<AdminZone>> ZonesAsync(NpgsqlConnection c, Guid clubId)
    {
        var json = await c.ExecuteScalarAsync<string?>("SELECT (settings -> 'zones')::text FROM clubs WHERE id = @clubId", new { clubId });
        return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<AdminZone[]>(json, JsonSerializerOptions.Web) ?? [];
    }

    /// <summary>A live PC of the staff member's club, else <c>404 what=pc</c>.</summary>
    public static async Task<PcRow> LivePcAsync(PcRepository pcs, StaffContext staff, Guid pcId) =>
        await pcs.FindAsync(pcId) is { DeletedAt: null } pc && pc.ClubId == staff.ClubId ? pc : throw ApiException.NotFound("pc");

    /// <summary>The open session to act on, locked: by <c>sessionId</c> when given (it wins, as in the mock), else the PC's.</summary>
    private static async Task<SessionRow> TargetAsync(NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, AdminSessionTarget r)
    {
        var id = r.SessionId ?? (r.PcId is { } pcId
            ? await c.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM sessions WHERE pc_id = @pcId AND state <> 'ended'", new { pcId }, tx)
            : throw ApiException.Validation("pcId", "required"));
        return id is { } sessionId && await SessionService.LockAsync(c, tx, sessionId) is { Ended: false } s && s.ClubId == staff.ClubId
            ? s
            : throw ApiException.NotFound("session");
    }

    private static int Minutes(int minutes) =>
        minutes < 5 ? throw ApiException.Validation("minutes", "min") : minutes > 1440 ? throw ApiException.Validation("minutes", "max") : minutes;

    private sealed class BonusTier
    {
        public long MinAmount { get; init; }
        public int BonusPct { get; init; }
    }

    /// <summary>What the journal entry and the answer of a desk open need from the new session row.</summary>
    private sealed class SeatRow
    {
        public string Who { get; init; } = "";
        public string Role { get; init; } = "";
        public string Tariff { get; init; } = "";
        public int DiscountPct { get; init; }
        public int DayPct { get; init; }
        public long PricePerHourSnapshot { get; init; }
        public int PurchasedSec { get; init; }
        public long Balance { get; init; }
    }
}
