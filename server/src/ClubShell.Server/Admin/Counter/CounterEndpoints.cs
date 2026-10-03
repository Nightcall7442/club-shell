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
/// The counter (slice S4): hall snapshot, sessions opened/extended/ended by the cashier, top-up with the tier bonus, PC
/// commands and the price preview. Money goes through the kiosk's own <see cref="SessionService"/> (the §5.2 rules and
/// the single <see cref="Pricing"/> function) and <see cref="Ledger"/>; each action and its <see cref="Audit"/> entry
/// commit together, <c>Idempotency-Key</c> (optional) under principal <c>club:&lt;id&gt;</c>. Money is taken only in an open
/// shift: top-up, open and extend answer <c>409 shiftClosed</c> without one; ending a session needs none (it only gives back
/// to the balance). Pushes and commands go out after the commit; <c>pcStatusChanged</c> is never sent (AsyncAPI
/// notImplemented, the console polls, §6.5). Events for the webhooks (<c>sessionOpened</c>, <c>bigTopup</c>,
/// <c>suspicious</c>) commit with the action; the automation of a top-up (<c>topupAtLeast</c>) and of an opened session runs
/// after the commit (S5).
/// </summary>
public static class CounterEndpoints
{
    public static readonly string[] Operations =
        ["adminOverview", "adminOpenSession", "adminExtend", "adminEnd", "adminTopUp", "adminCommand", "adminQuote"];

    /// <summary><c>ledger_entries.method</c> values.</summary>
    private static readonly string[] Methods = ["cash", "card", "payme", "click", "uzum"];

    public static void MapCounterEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin").WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapGet("/overview", OverviewAsync);
        api.MapPost("/sessions", OpenAsync);
        api.MapPost("/sessions/extend", ExtendAsync);
        api.MapPost("/sessions/end", EndAsync);
        api.MapPost("/wallet/topup", TopUpAsync);
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
        var seats = hall.Select(pc => new AdminSeat(
            pc.ToPc(pc.Status(hub.IsConnected(pc.Id), now, TimeSpan.FromSeconds(agents.OfflineAfterSec)), withHwid: false),
            open.TryGetValue(pc.Id, out var s) ? s.ToWire(now) : null,
            s is not null && players.TryGetValue(s.UserId, out var user) ? user : null)).ToList();
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
        // Guests of this club left with a postpaid bill: their accounts are transient, so the client list never shows them.
        var debts = (await c.QueryAsync<(Guid Id, string DisplayName, long Balance, string? Pc, DateTimeOffset? EndedAt)>(
            """
            SELECT u.id, u.display_name, w.main_balance, last.pc, last.ended_at
            FROM users u JOIN wallets w ON w.user_id = u.id
            CROSS JOIN LATERAL (
                SELECT p.name AS pc, s.ended_at FROM sessions s JOIN pcs p ON p.id = s.pc_id
                WHERE s.user_id = u.id AND s.club_id = @ClubId ORDER BY s.started_at DESC, s.id DESC LIMIT 1) last
            WHERE u.network_id = @NetworkId AND u.role = 'guest' AND u.deleted_at IS NULL AND w.main_balance < 0
            ORDER BY last.ended_at DESC NULLS FIRST, u.id
            LIMIT 100
            """,
            new { staff.ClubId, staff.NetworkId }))
            .Select(d => new AdminGuestDebt(d.Id, d.DisplayName, Money.Uzs(-d.Balance), d.Pc, d.EndedAt)).ToList();
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
            await ZonesAsync(c, staff.ClubId), repairs, debts));
    }

    /// <summary>
    /// <c>adminOpenSession</c> (§5.3): prepaid, priced by the club rules; <c>201 {session, charged, balance}</c>. With
    /// <c>payment</c> (the desk's "Посадить · Наличные") the top-up is posted first in the same transaction, so a session the
    /// rules refuse (blacklist, curfew, a busy PC, a changed price) leaves no money booked. The wallet is then locked before
    /// the session row is inserted; only a kiosk sign-in of the same player at that instant can deadlock with it, and
    /// Postgres refuses one of the two.
    /// </summary>
    private static async Task<IResult> OpenAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, PcRepository pcs,
        AutomationService automation, ILoggerFactory logs)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminOpenSessionRequest>(body, "pcId", "userId", "tariffId", "minutes");
        var minutes = Minutes(r.Minutes!.Value);
        var payment = PaymentOf(r.Payment);
        var pc = await LivePcAsync(pcs, staff, r.PcId!.Value);
        var effects = new SessionEffects();
        TopUpPosted? paid = null;
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            await ShiftEndpoints.RequireOpenAsync(c, tx, staff);
            if (payment is { } pay)
            {
                paid = await PostTopUpAsync(c, tx, staff, effects, r.UserId!.Value, pay.Amount, pay.Method, sessions.Clock.GetUtcNow());
            }

            var session = await sessions.CreateAsync(
                c, tx, pc, new SessionCreateRequest(pc.Id, r.UserId!.Value, r.TariffId!.Value, minutes, true), replay: false, effects, staff);
            var (who, tariff, discount, balance) = await c.QuerySingleAsync<(string, string, int, long)>(
                """
                SELECT u.display_name, t.name, s.discount_pct, w.main_balance
                FROM sessions s JOIN users u ON u.id = s.user_id JOIN tariffs t ON t.id = s.tariff_id JOIN wallets w ON w.user_id = s.user_id
                WHERE s.id = @Id
                """,
                new { session.Id }, tx);
            await Audit.WriteAsync(c, tx, staff, sessions.Clock.GetUtcNow(), "sessionOpen", session.UserId, pc.Id, session.Cost.Amount,
                $"{who} · {pc.Name} · {tariff}", new { minutes = session.SecondsLeft / 60, discountPct = discount, sessionId = session.Id });
            return new IdempotentResult(StatusCodes.Status201Created, AdminJson.ToElement(new AdminSessionResult(session, session.Cost, Money.Uzs(balance))));
        });
        await sessions.PublishAsync(effects);
        await AfterTopUpAsync(automation, logs, staff, paid);
        return result;
    }

    /// <summary>
    /// <c>adminExtend</c> (§5.6): the kiosk's extend, plus <c>extendSession {charge:false}</c> to the PC. With <c>payment</c> the
    /// session's player is topped up in the same transaction, after the session row is locked (§4.4).
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
            await ShiftEndpoints.RequireOpenAsync(c, tx, staff);
            var s = await TargetAsync(c, tx, staff, r);
            if (payment is { } pay)
            {
                paid = await PostTopUpAsync(c, tx, staff, effects, s.UserId, pay.Amount, pay.Method, sessions.Clock.GetUtcNow());
            }

            var (session, charged) = await sessions.ExtendAsync(c, tx, s.Id, s.UserId, s.PcId, minutes, r.TariffId, effects, staff);
            var balance = await c.ExecuteScalarAsync<long>("SELECT main_balance FROM wallets WHERE user_id = @UserId", new { s.UserId }, tx);
            await Audit.WriteAsync(c, tx, staff, sessions.Clock.GetUtcNow(), "sessionExtend", s.UserId, s.PcId, charged,
                $"+{minutes}", new { minutes, sessionId = s.Id });
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminSessionResult(session, Money.Uzs(charged), Money.Uzs(balance))));
        });
        await sessions.PublishAsync(effects);
        await AfterTopUpAsync(automation, logs, staff, paid);
        return result;
    }

    /// <summary>
    /// <c>adminEnd</c> (§5.7): settled with reason <c>admin</c> (unused hourly time refunded pro rata to what was paid) and
    /// <c>endSession</c> queued for the PC — without a <c>sessionUpdated</c> push, which the agent would take first and close
    /// the session itself.
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
            var sessionMinutes = (int)(now - s.StartedAt).TotalMinutes;
            await Audit.WriteAsync(c, tx, staff, now, "sessionEnd", s.UserId, s.PcId, refunded, "", new { sessionMinutes, sessionId = s.Id });
            await ControlAlerts.SessionEndedAsync(c, tx, staff, refunded, sessionMinutes, now);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminSessionResult(session, Money.Uzs(charged), Refunded: Money.Uzs(refunded))));
        });
        await sessions.PublishAsync(effects);
        return result;
    }

    /// <summary>
    /// <c>adminTopUp</c>: the top-up and the tier bonus (<c>settings.bonusTiers</c>, highest reached tier, half-up to 100 tiyin)
    /// are one ledger operation; the answer carries the top-up row as <c>transaction</c>.
    /// </summary>
    private static async Task<IResult> TopUpAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, AutomationService automation, ILoggerFactory logs)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminTopUpRequest>(body, "userId", "amount");
        var (amount, method) = Payment(r.Amount, r.Method ?? "cash", "");
        var effects = new SessionEffects();
        TopUpPosted? paid = null;
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            await ShiftEndpoints.RequireOpenAsync(c, tx, staff);
            paid = await PostTopUpAsync(c, tx, staff, effects, r.UserId!.Value, amount, method, sessions.Clock.GetUtcNow());
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminTopUpResponse(Money.Uzs(paid.Balance), paid.Transaction, Money.Uzs(paid.Bonus))));
        });
        await sessions.PublishAsync(effects);
        await AfterTopUpAsync(automation, logs, staff, paid);
        return result;
    }

    /// <summary>A posted counter top-up: its ledger row, the balance after it and the tier bonus.</summary>
    private sealed record TopUpPosted(Guid Id, Guid UserId, long Amount, long Balance, long Bonus, Transaction Transaction);

    /// <summary>
    /// A counter top-up in the caller's transaction: the top-up and the tier bonus as one ledger operation (the top-up row
    /// must join the open shift, <see cref="LedgerLine.ShiftRequired"/>, unless the club API key took it), its journal entry
    /// and the <c>bigTopup</c> event.
    /// </summary>
    private static async Task<TopUpPosted> PostTopUpAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, SessionEffects effects, Guid userId, long amount, string method, DateTimeOffset now)
    {
        var who = await c.QuerySingleOrDefaultAsync<string>(
            "SELECT display_name FROM users WHERE id = @userId AND network_id = @NetworkId AND deleted_at IS NULL", new { userId, staff.NetworkId }, tx)
            ?? throw ApiException.NotFound("user");
        var tiers = await c.ExecuteScalarAsync<string?>("SELECT (settings -> 'bonusTiers')::text FROM clubs WHERE id = @ClubId", new { staff.ClubId }, tx);
        var bonus = Bonus(amount, tiers);
        var id = Guid.CreateVersion7(now);
        var balance = await Ledger.PostAsync(c, tx, userId, allowOverdraft: false, now,
            new LedgerLine("topUp", amount, TopUpDescription(method), staff.ClubId, Method: method, StaffId: staff.StaffId, Id: id,
                ShiftRequired: staff.StaffId is not null),
            new LedgerLine("bonus", bonus, "Бонус за пополнение", staff.ClubId, StaffId: staff.StaffId));
        var row = await c.QuerySingleAsync<(long BalanceAfter, string Description, DateTimeOffset CreatedAt)>(
            "SELECT balance_after, description, created_at FROM ledger_entries WHERE id = @id", new { id }, tx);
        await Audit.WriteAsync(c, tx, staff, now, "topUp", userId, amount: amount, detail: who, meta: new { method, bonus });
        if (amount >= await ClubSettingsEndpoints.BigTopupAtAsync(c, tx, staff.ClubId))
        {
            await Webhooks.EnqueueAsync(c, tx, staff.ClubId, "bigTopup", now, $"{who}: {Webhooks.Sum(amount)}", new { userId, amount });
        }

        effects.Wallets.Add(userId);
        var transaction = new Transaction(id, userId, TransactionType.TopUp, Money.Uzs(amount), Money.Uzs(row.BalanceAfter), row.Description, row.CreatedAt);
        return new TopUpPosted(id, userId, amount, balance, bonus, transaction);
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
    /// (the contract's 404 names only tariff and pc).
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
        var q = Pricing.Compute(tariff, minutes, buyer?.GroupId, buyer?.LifetimeSpent, zone, clock.GetUtcNow(), club.Pricing);
        return AdminJson.Ok(new AdminPriceQuote(Money.Uzs(q.Base), q.DayPct, q.DiscountPct, q.DiscountReason, Money.Uzs(q.Total)));
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
}
