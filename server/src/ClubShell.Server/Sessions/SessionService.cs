using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions.Billing;
using ClubShell.Server.Wallet;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Sessions;

/// <summary>
/// A row of <c>sessions</c> and its billing clock (DESIGN §5.4). Seconds are whole: <c>used(t)</c> floors the running
/// part. The transitions below mutate the row; <see cref="SessionService"/> saves it in the same transaction.
/// </summary>
public class SessionRow
{
    public const string Columns = """
        id, club_id, pc_id, user_id, tariff_id, state, is_prepaid, started_at, ended_at, end_reason, purchased_sec,
        used_before_sec, running_since, paused_at, ends_at, last_transition_at, price_per_hour_snapshot, day_pct,
        discount_pct, charged_total, refunded_total, warnings_sent, client_session_id
        """;

    public Guid Id { get; init; }
    public Guid ClubId { get; init; }
    public Guid PcId { get; init; }
    public Guid UserId { get; init; }
    public Guid TariffId { get; set; }
    public string State { get; set; } = "active";
    public bool IsPrepaid { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public int PurchasedSec { get; set; }
    public int UsedBeforeSec { get; set; }
    public DateTimeOffset? RunningSince { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public DateTimeOffset LastTransitionAt { get; set; }
    public long PricePerHourSnapshot { get; set; }
    public int DayPct { get; init; }
    public int DiscountPct { get; init; }
    public long ChargedTotal { get; set; }
    public long RefundedTotal { get; set; }
    public int[] WarningsSent { get; set; } = [];
    public Guid? ClientSessionId { get; init; }

    public bool Ended => State == "ended";

    /// <summary>The billing clock runs in active, locked and ending: a lock does not stop it (D-12).</summary>
    public bool Running => State is "active" or "locked" or "ending";

    public int Used(DateTimeOffset t) =>
        UsedBeforeSec + (Running && RunningSince is { } since && t > since ? (int)Math.Floor((t - since).TotalSeconds) : 0);

    /// <summary>A running prepaid session ends at <c>running_since + purchased − used_before</c>; otherwise no end.</summary>
    public DateTimeOffset? PrepaidEnd() => IsPrepaid && Running && RunningSince is { } since ? since.AddSeconds(PurchasedSec - UsedBeforeSec) : null;

    /// <summary>
    /// The second a pause falls in counts as used (ceil): a floor would drop up to 999 ms per pause, and pause/resume
    /// under a second would never be billed.
    /// </summary>
    public void Pause(DateTimeOffset t)
    {
        UsedBeforeSec += Running && RunningSince is { } since && t > since ? (int)Math.Ceiling((t - since).TotalSeconds) : 0;
        RunningSince = null;
        PausedAt = t;
        EndsAt = null;
        State = "paused";
        LastTransitionAt = t;
    }

    public void Resume(DateTimeOffset t)
    {
        RunningSince = t;
        PausedAt = null;
        State = "active";
        LastTransitionAt = t;
        EndsAt = PrepaidEnd();
    }

    /// <summary>
    /// Adds bought time at <paramref name="t"/> (§5.6): warnings start over, <c>ending</c> becomes <c>active</c>. Grace
    /// and any overrun past the bought time are free (§5.10), so the clock is first set back to what was bought.
    /// </summary>
    public void Extend(int seconds, Guid tariffId, long charged, DateTimeOffset t)
    {
        if (Used(t) > PurchasedSec)
        {
            UsedBeforeSec = PurchasedSec;
            RunningSince = Running ? t : null;
        }

        PurchasedSec += seconds;
        TariffId = tariffId;
        ChargedTotal += charged;
        WarningsSent = [];
        if (State == "ending")
        {
            State = "active";
        }

        EndsAt = PrepaidEnd();
    }

    /// <summary>Stops the clock at <paramref name="t"/>; prepaid time used is capped by what was bought (grace is free).</summary>
    public int End(DateTimeOffset t, SessionEndReason reason)
    {
        var used = IsPrepaid ? Math.Min(Used(t), PurchasedSec) : Used(t);
        UsedBeforeSec = used;
        RunningSince = null;
        PausedAt = null;
        EndsAt = null;
        State = "ended";
        EndedAt = t;
        EndReason = JsonNamingPolicy.CamelCase.ConvertName(reason.ToString());
        LastTransitionAt = t;
        return used;
    }

    /// <summary>Undoes <see cref="End"/> at <c>ended_at</c> in memory: the clock runs again from there, or stays paused.</summary>
    public void Reopen(bool running)
    {
        var t = EndedAt!.Value;
        State = running ? "active" : "paused";
        RunningSince = running ? t : null;
        PausedAt = running ? null : t;
        EndedAt = null;
        EndReason = null;
        EndsAt = PrepaidEnd();
    }

    /// <summary>
    /// The wire <c>Session</c> at <paramref name="now"/>: <c>secondsLeft</c> −1 for postpaid (0 once ended), <c>cost</c> =
    /// charged − refunded, or for an open postpaid session the frozen price of <c>ceil(used / 60)</c> minutes.
    /// </summary>
    public Session ToWire(DateTimeOffset now)
    {
        var used = Ended ? UsedBeforeSec : Used(now);
        var left = Ended ? 0 : IsPrepaid ? Math.Max(0, PurchasedSec - used) : Session.OpenEnded;
        var cost = IsPrepaid || Ended ? ChargedTotal - RefundedTotal : Pricing.Frozen(PricePerHourSnapshot, used, DayPct, DiscountPct);
        return new Session(
            Id, UserId, PcId, Enum.Parse<SessionState>(State, ignoreCase: true), StartedAt, Ended ? EndedAt : EndsAt, PausedAt, TariffId,
            left, used, Money.Uzs(cost), IsPrepaid, WarningsSent);
    }
}

/// <summary>What a committed change still has to announce: pushes and commands go out after the commit (DESIGN §4.3).</summary>
public sealed class SessionEffects
{
    public List<Session> Sessions { get; } = [];

    public HashSet<Guid> Wallets { get; } = [];

    public List<(Guid ClubId, Guid PcId, NewCommand Command)> Commands { get; } = [];

    /// <summary>Sessions opened (not replayed): the <c>sessionStarted</c>/<c>visitCount</c> automation runs after the commit (§5.3).</summary>
    public List<(Guid ClubId, Guid SessionId, Guid PcId, Guid UserId)> Opened { get; } = [];

    /// <summary>
    /// Players whose token of a PC the change deleted, told <c>userRevoked</c> before the session pushes (<c>seatTaken</c>: the
    /// desk seated someone else there, D-29 — the agent must drop its player before it applies the new session).
    /// </summary>
    public List<(Guid UserId, Guid PcId, string Reason)> RevokedBefore { get; } = [];

    /// <summary>
    /// Players signed out of a PC after the session pushes and commands (<c>sessionEnded</c>: a desk end, a guest's time-up,
    /// D-28 — the agent then ends nothing more and drops the player, so the kiosk does not start anything on their balance;
    /// <c>seatMoved</c>: the desk moved the session to another PC, D-61).
    /// </summary>
    public List<(Guid UserId, Guid PcId, string Reason)> RevokedAfter { get; } = [];

    /// <summary>
    /// Sessions the desk moved off a PC (D-61): the «ended view» (<see cref="SessionService.EndedView"/>, <c>pcId</c> = the old
    /// PC) is pushed to the old PC right after the sessions, so its agent closes the session without charging anything.
    /// </summary>
    public List<Session> Departed { get; } = [];
}

/// <summary>The club facts every rule needs.</summary>
public sealed record ClubInfo(Guid Id, Guid NetworkId, ClubPricing Pricing);

/// <summary>A player as the purchase rules see them in a club (DESIGN §5.2).</summary>
public sealed class Buyer
{
    public Guid Id { get; init; }
    public string Role { get; init; } = "";
    public bool Banned { get; init; }
    public bool Blacklisted { get; init; }
    public string? GroupId { get; init; }
    public int? BirthYear { get; init; }
    public long LifetimeSpent { get; init; }
}

/// <summary>
/// Sessions and their money (DESIGN §5), one implementation for the kiosk, the tick and (S4) the cashier: create with the
/// purchase rules (§5.2, §5.3) and the offline replay (§5.11), pause/resume (§5.5), extend (§5.6), settlement (§5.7), late
/// agent events (§5.12). Every mutation locks the session row first (<c>FOR UPDATE</c>), then the wallet in
/// <see cref="Ledger"/> — the lock order of §4.4. Methods taking a connection run in the caller's transaction (the
/// idempotency store's); the others open their own.
/// </summary>
public sealed class SessionService(
    NpgsqlDataSource db, TimeProvider clock, SessionsOptions options, Pushes pushes, CommandDispatcher commands, Admin.AutomationService automation,
    ILogger<SessionService> logger)
{
    public TimeProvider Clock => clock;

    /// <summary>
    /// How far below zero a postpaid session of <paramref name="role"/> may run (D-10): a guest by the club's
    /// <c>limits.guestDebtLimit</c> (null — no limit; guests are let in at all only with <c>limits.guestPostpaid</c>), anyone
    /// else by the club's <c>limits.memberDebtLimit</c> (D-31), or without one by <see cref="SessionsOptions.PostpaidCreditLimit"/>
    /// (0 by default: a member plays postpaid only while the balance lasts).
    /// </summary>
    public long? PostpaidLimit(string role, ClubPricing club) => role == "guest" ? club.GuestDebtLimit : club.MemberDebtLimit ?? options.PostpaidCreditLimit;

    public static ApiException PolicyDenied(string rule) =>
        new(StatusCodes.Status403Forbidden, ErrorCode.PolicyDenied, $"Denied by club policy: {rule}", new { rule });

    public static ApiException Conflict(string reason) =>
        new(StatusCodes.Status409Conflict, ErrorCode.Conflict, $"Conflict: {reason}", new { reason });

    /// <summary><c>409 sessionNotActive</c> with <c>details.session</c>: on an ended session the agent's end treats it as success.</summary>
    public static ApiException NotActive(Session session) =>
        new(StatusCodes.Status409Conflict, ErrorCode.SessionNotActive, "Session is not in a state that allows this", new { session = JsonDefaults.ToElement(session) });

    public static async Task<(NpgsqlConnection, NpgsqlTransaction)> BeginAsync(NpgsqlDataSource db)
    {
        var c = await db.OpenConnectionAsync();
        var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("SET LOCAL lock_timeout = '10s'", transaction: tx);
        return (c, tx);
    }

    public static async Task<ClubInfo> ClubAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid clubId)
    {
        var (networkId, timeZone, settings) = await c.QuerySingleAsync<(Guid, string, string)>(
            "SELECT network_id, time_zone, settings::text FROM clubs WHERE id = @clubId", new { clubId }, tx);
        return new ClubInfo(clubId, networkId, ClubPricing.Parse(timeZone, settings));
    }

    public static Task<Buyer?> BuyerAsync(NpgsqlConnection c, NpgsqlTransaction? tx, ClubInfo club, Guid userId) =>
        c.QuerySingleOrDefaultAsync<Buyer>(
            """
            SELECT u.id, u.role, u.banned, coalesce(cp.blacklisted, false) AS blacklisted, cp.group_id, cp.birth_year,
                   coalesce(w.lifetime_spent, 0) AS lifetime_spent
            FROM users u
            LEFT JOIN client_profiles cp ON cp.user_id = u.id AND cp.club_id = @clubId
            LEFT JOIN wallets w ON w.user_id = u.id
            WHERE u.id = @userId AND u.network_id = @networkId AND u.deleted_at IS NULL
            """,
            new { userId, clubId = club.Id, networkId = club.NetworkId },
            tx);

    /// <summary>A tariff of the club; a soft-deleted one only when <paramref name="withDeleted"/> (settling a running session).</summary>
    public static Task<TariffRow?> TariffAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid clubId, Guid tariffId, bool withDeleted = false) =>
        c.QuerySingleOrDefaultAsync<TariffRow>(
            $"SELECT {TariffRow.Columns} FROM tariffs WHERE id = @tariffId AND club_id = @clubId AND (@withDeleted OR deleted_at IS NULL)",
            new { tariffId, clubId, withDeleted },
            tx);

    public static Task<SessionRow?> OpenOfPcAsync(NpgsqlConnection c, Guid pcId, NpgsqlTransaction? tx = null) =>
        c.QuerySingleOrDefaultAsync<SessionRow>($"SELECT {SessionRow.Columns} FROM sessions WHERE pc_id = @pcId AND state <> 'ended'", new { pcId }, tx);

    public static Task<SessionRow?> LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId) =>
        c.QuerySingleOrDefaultAsync<SessionRow>($"SELECT {SessionRow.Columns} FROM sessions WHERE id = @sessionId FOR UPDATE", new { sessionId }, tx);

    /// <summary>
    /// <c>POST /sessions</c> in the idempotency transaction (§5.2, §5.3); with <paramref name="replay"/> the offline replay
    /// of §5.11: priced at <c>startedAt</c>, overdraft instead of 402, of the rules only the player's (exists, not banned or
    /// blacklisted), and a session this PC already has under <c>clientSessionId</c> is returned as it is. With
    /// <paramref name="staffId"/> it is the cashier's <c>adminOpenSession</c> (origin <c>cashier</c>): the same rules and price.
    /// </summary>
    public async Task<Session> CreateAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, PcRow pc, SessionCreateRequest request, bool replay, SessionEffects effects, StaffContext? staff = null)
    {
        var now = clock.GetUtcNow();
        if (replay && await c.QuerySingleOrDefaultAsync<SessionRow>(
                $"SELECT {SessionRow.Columns} FROM sessions WHERE pc_id = @pcId AND (id = @id OR client_session_id = @id) LIMIT 1",
                new { pcId = pc.Id, id = request.ClientSessionId }, tx) is { } known)
        {
            return known.ToWire(now);
        }

        // The caller checked the PC before this transaction; adminDeletePc may have soft-deleted it since (it holds the row
        // FOR UPDATE, so this waits for its commit and then sees deleted_at). KEY SHARE keeps a delete from starting until
        // this session is committed, which it then finds and refuses with pcBusy.
        if (await c.ExecuteScalarAsync<int?>(
                "SELECT 1 FROM pcs WHERE id = @Id AND club_id = @ClubId AND deleted_at IS NULL FOR KEY SHARE", new { pc.Id, pc.ClubId }, tx) is null)
        {
            throw ApiException.NotFound("pc");
        }

        // The replay records a game the agent already let happen (§5.11): a tariff deleted and a PC put in maintenance or
        // moved to another zone meanwhile, or a curfew, do not refuse it — only a missing, banned or blacklisted player.
        var club = await ClubAsync(c, tx, pc.ClubId);
        var buyer = await BuyerAsync(c, tx, club, request.UserId) ?? throw ApiException.NotFound("user");
        var tariff = await TariffAsync(c, tx, club.Id, request.TariffId, withDeleted: replay) ?? throw ApiException.NotFound("tariff");

        // A package is bought whole (D-38): the kiosk already forces prepaid for it, the desk is refused; a replay records
        // what the agent let happen.
        if (tariff.IsPackage && !request.Prepaid && !replay)
        {
            throw ApiException.Validation("prepaid", "package");
        }

        var start = replay ? Min(request.StartedAt!.Value, now) : now;
        if (replay && now - start > TimeSpan.FromHours(options.MaxReplayHours))
        {
            throw ApiException.Validation("startedAt", "tooOld");
        }

        if (pc.Maintenance && !replay)
        {
            throw PolicyDenied("pcMaintenance");
        }

        await EnsureNothingOpenAsync(c, tx, pc.Id, buyer.Id);
        CheckRules(pc.Zone, buyer, tariff, club, start, offlineReplay: replay);

        // Postpaid buys no minutes (its price is frozen per hour, §5.3): the kiosk sends none, any sent are ignored.
        int minutes;
        if (tariff.IsPackage)
        {
            minutes = tariff.PackageMinutes!.Value;
        }
        else if (!request.Prepaid)
        {
            minutes = 0;
        }
        else
        {
            minutes = request.Minutes ?? throw ApiException.Validation("minutes", "required");
            CheckMinutes(tariff, minutes);
        }

        if (!request.Prepaid && buyer.Role == "guest" && !club.Pricing.GuestPostpaid)
        {
            throw PolicyDenied("postpaidNotAllowed");
        }

        var quote = Pricing.Compute(tariff, minutes, buyer.GroupId, buyer.LifetimeSpent, pc.Zone, start, club.Pricing);
        if (!request.Prepaid && !replay && PostpaidLimit(buyer.Role, club.Pricing) is { } limit)
        {
            // Rule 10 for postpaid (D-10): the first minute must be affordable, as the tick would stop it at once.
            var first = Pricing.Frozen(tariff.PricePerHour, 1, quote.DayPct, quote.DiscountPct);
            var balance = await c.ExecuteScalarAsync<long>("SELECT main_balance FROM wallets WHERE user_id = @Id", new { buyer.Id }, tx);
            if (first > balance + limit)
            {
                throw InsufficientFunds(first, balance);
            }
        }
        var id = request.ClientSessionId is { } wanted && wanted != Guid.Empty
            && !await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM sessions WHERE id = @wanted)", new { wanted }, tx)
            ? wanted
            : Guid.CreateVersion7(now);
        var row = new SessionRow
        {
            Id = id, ClubId = club.Id, PcId = pc.Id, UserId = buyer.Id, TariffId = tariff.Id, State = "active", IsPrepaid = request.Prepaid,
            StartedAt = start, PurchasedSec = request.Prepaid ? minutes * 60 : 0, RunningSince = start, LastTransitionAt = start,
            PricePerHourSnapshot = tariff.PricePerHour, DayPct = quote.DayPct, DiscountPct = quote.DiscountPct,
            ChargedTotal = request.Prepaid ? quote.Total : 0, ClientSessionId = request.ClientSessionId,
        };
        row.EndsAt = row.PrepaidEnd();

        // The partial unique indexes decide a race (two kiosks, kiosk and cashier, two PCs of one player): the loser
        // waits for the winner's commit on the index and gets unique_violation — then the winner is visible (§4.4).
        await c.ExecuteAsync("SAVEPOINT open_session", transaction: tx);
        try
        {
            await c.ExecuteAsync(
                """
                INSERT INTO sessions (id, club_id, pc_id, user_id, tariff_id, state, is_prepaid, origin, started_at, purchased_sec,
                                      running_since, ends_at, last_transition_at, price_per_hour_snapshot, day_pct, discount_pct,
                                      discount_reason, charged_total, client_session_id, created_by_staff_id, created_at, updated_at)
                VALUES (@Id, @ClubId, @PcId, @UserId, @TariffId, 'active', @IsPrepaid, @origin, @StartedAt, @PurchasedSec,
                        @RunningSince, @EndsAt, @LastTransitionAt, @PricePerHourSnapshot, @DayPct, @DiscountPct,
                        @reason, @ChargedTotal, @ClientSessionId, @staffId, @now, @now)
                """,
                new
                {
                    row.Id, row.ClubId, row.PcId, row.UserId, row.TariffId, row.IsPrepaid, origin = replay ? "offline" : staff is null ? "kiosk" : "cashier",
                    row.StartedAt, row.PurchasedSec, row.RunningSince, row.EndsAt, row.LastTransitionAt, row.PricePerHourSnapshot,
                    row.DayPct, row.DiscountPct, reason = quote.DiscountReason, row.ChargedTotal, row.ClientSessionId, staffId = staff?.StaffId, now,
                },
                tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is "sessions_open_pc" or "sessions_open_user")
        {
            await c.ExecuteAsync("ROLLBACK TO SAVEPOINT open_session", transaction: tx);
            await EnsureNothingOpenAsync(c, tx, pc.Id, buyer.Id);
            throw;
        }

        if (row.ChargedTotal > 0)
        {
            await Ledger.PostAsync(c, tx, buyer.Id, allowOverdraft: replay, now, new LedgerLine(
                "charge", -row.ChargedTotal, $"Сеанс {minutes} мин · {tariff.Name}", club.Id, row.Id, pc.Id, StaffId: staff?.StaffId, Meta: quote));
            effects.Wallets.Add(buyer.Id);
        }

        var session = row.ToWire(now);
        effects.Sessions.Add(session);
        if (!replay)
        {
            // The event commits with the session; the rules run after the commit (§5.3).
            var who = await c.QuerySingleAsync<string>("SELECT display_name FROM users WHERE id = @Id", new { buyer.Id }, tx);
            await Admin.Webhooks.EnqueueAsync(c, tx, club.Id, "sessionOpened", now, $"{pc.Name} · {who}", new { sessionId = row.Id, pcId = pc.Id, userId = buyer.Id });
            effects.Opened.Add((club.Id, row.Id, pc.Id, buyer.Id));
        }

        return session;
    }

    /// <summary><c>POST /sessions/{id}/pause</c> (§5.5): <c>active</c> → <c>paused</c>, the clock stops.</summary>
    public async Task<Session> PauseAsync(Guid sessionId, Guid userId, Guid pcId)
    {
        var (c, tx) = await BeginAsync(db);
        await using (c)
        await using (tx)
        {
            var now = clock.GetUtcNow();
            var s = await OwnedAsync(c, tx, sessionId, userId, pcId);
            if (s.State == "paused")
            {
                throw Conflict("alreadyPaused");
            }

            // locked is informational (D-12) and comes from the agent's queued events: the player may already have unlocked.
            if (s.State is not ("active" or "locked"))
            {
                throw NotActive(s.ToWire(now));
            }

            s.Pause(now);
            return await CommitAsync(c, tx, s, now, new SessionEffects());
        }
    }

    /// <summary>
    /// <c>POST /sessions/{id}/resume</c> (§5.5): <c>paused</c> → <c>active</c>. Prepaid needs time left (else
    /// <c>sessionNotActive</c>); postpaid needs its next second affordable within balance + credit limit (else 402, as the
    /// tick would stop it).
    /// </summary>
    public async Task<Session> ResumeAsync(Guid sessionId, Guid userId, Guid pcId)
    {
        var (c, tx) = await BeginAsync(db);
        await using (c)
        await using (tx)
        {
            var now = clock.GetUtcNow();
            var s = await OwnedAsync(c, tx, sessionId, userId, pcId);
            if (s.State == "active")
            {
                throw Conflict("notPaused");
            }

            if (s.State != "paused" || (s.IsPrepaid && s.PurchasedSec - s.UsedBeforeSec <= 0))
            {
                throw NotActive(s.ToWire(now));
            }

            await EnsureResumableAsync(c, tx, s);
            s.Resume(now);
            return await CommitAsync(c, tx, s, now, new SessionEffects());
        }
    }

    /// <summary>A paused postpaid session resumes only if its next second is affordable within balance + limit (else 402, as the tick would stop it).</summary>
    private async Task EnsureResumableAsync(NpgsqlConnection c, NpgsqlTransaction tx, SessionRow s)
    {
        var role = s.IsPrepaid ? "" : await c.ExecuteScalarAsync<string>("SELECT role FROM users WHERE id = @UserId", new { s.UserId }, tx) ?? "";
        if (!s.IsPrepaid && PostpaidLimit(role, (await ClubAsync(c, tx, s.ClubId)).Pricing) is { } limit)
        {
            var balance = await c.ExecuteScalarAsync<long>("SELECT main_balance FROM wallets WHERE user_id = @UserId", new { s.UserId }, tx);
            var next = Pricing.Frozen(s.PricePerHourSnapshot, s.UsedBeforeSec + 1, s.DayPct, s.DiscountPct);
            if (next > balance + limit)
            {
                throw InsufficientFunds(next, balance);
            }
        }
    }

    /// <summary>
    /// The desk moves a locked open session to <paramref name="target"/> (D-59, D-60), in the caller's transaction, which
    /// holds both PCs' advisory locks: <c>sessions.pc_id</c> is re-pointed and nothing else of the money changes — bought and
    /// used time, the running clock, the charges and the warnings are kept, no ledger row is written. A locked session runs
    /// on as active, a paused one is resumed (a postpaid one only if its next second is affordable, else 402); the tariff
    /// changes only when <paramref name="tariff"/> is given (future extends use it). A <c>staff</c>/<c>moved</c> event records
    /// where it came from (the old PC's late calls and the guest sign-in read it, D-61). A session opened on the target in
    /// the same instant (a kiosk create takes no PC lock) wins: <c>409 pcBusy</c>. Returns the session as it is now.
    /// </summary>
    public async Task<SessionRow> MoveAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, SessionRow s, PcRow target, TariffRow? tariff, DateTimeOffset now, StaffContext staff)
    {
        var from = s.PcId;
        var used = s.Used(now);
        if (s.State == "paused")
        {
            await EnsureResumableAsync(c, tx, s);
            s.Resume(now);
        }
        else if (s.State == "locked")
        {
            s.State = "active";
        }

        s.LastTransitionAt = now;
        if (tariff is not null)
        {
            s.TariffId = tariff.Id;
        }

        await c.ExecuteAsync("SAVEPOINT move_session", transaction: tx);
        try
        {
            await c.ExecuteAsync(
                """
                UPDATE sessions SET pc_id = @pcId, state = @State, running_since = @RunningSince, paused_at = @PausedAt, ends_at = @EndsAt,
                                    last_transition_at = @LastTransitionAt, tariff_id = @TariffId, updated_at = @now
                WHERE id = @Id
                """,
                new { pcId = target.Id, s.State, s.RunningSince, s.PausedAt, s.EndsAt, s.LastTransitionAt, s.TariffId, now, s.Id }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "sessions_open_pc")
        {
            await c.ExecuteAsync("ROLLBACK TO SAVEPOINT move_session", transaction: tx);
            throw PcBusy(target.Id);
        }

        await c.ExecuteAsync(
            """
            INSERT INTO session_events (session_id, club_id, source, type, at, received_at, data, applied)
            VALUES (@Id, @ClubId, 'staff', 'moved', @now, @now, @data::jsonb, true)
            """,
            new { s.Id, s.ClubId, now, data = JsonSerializer.Serialize(new { fromPcId = from, toPcId = target.Id, staffId = staff.WireId, secondsUsed = used }, ServerJson.Options) },
            tx);
        return (await LockAsync(c, tx, s.Id))!;
    }

    /// <summary><c>409 conflict pcBusy {pcId}</c>: the target PC holds an open session.</summary>
    public static ApiException PcBusy(Guid pcId) =>
        new(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: pcBusy", new { reason = "pcBusy", pcId });

    /// <summary>
    /// When the desk moved session <paramref name="sessionId"/> off <paramref name="pcId"/> (its latest such move) and the
    /// seconds used then, or null: the old PC's late <c>/end</c> and <c>/events</c> are answered from it (D-61).
    /// </summary>
    public static Task<(DateTimeOffset At, int SecondsUsed)?> MovedOffAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid sessionId, Guid pcId) =>
        c.QuerySingleOrDefaultAsync<(DateTimeOffset At, int SecondsUsed)?>(
            """
            SELECT at, coalesce((data ->> 'secondsUsed')::int, 0) FROM session_events
            WHERE session_id = @sessionId AND source = 'staff' AND type = 'moved' AND data ->> 'fromPcId' = @pc
            ORDER BY at DESC, id DESC LIMIT 1
            """,
            new { sessionId, pc = pcId.ToString("D", System.Globalization.CultureInfo.InvariantCulture) }, tx);

    /// <summary>
    /// The «ended view» of a moved session for its old PC (D-61): the same id, <c>pcId</c> = the old PC, <c>ended</c> at the
    /// move with nothing charged — the agent closes its local session cleanly (a push, or the 409 of its own <c>/end</c>).
    /// </summary>
    public static Session EndedView(SessionRow s, Guid pcId, DateTimeOffset movedAt, int secondsUsed) => new(
        s.Id, s.UserId, pcId, SessionState.Ended, s.StartedAt, movedAt, null, s.TariffId, 0, secondsUsed, Money.Uzs(0), s.IsPrepaid, s.WarningsSent);

    /// <summary>
    /// Events of the old PC of a moved session (D-61): recorded as the agent's (<c>applied = false</c>, a repeat skipped) and
    /// never applied — an old <c>ended</c> must not close the session on its new PC.
    /// </summary>
    public async Task RecordUnappliedAsync(NpgsqlConnection c, NpgsqlTransaction tx, SessionRow s, IEnumerable<SessionEvent> events)
    {
        var now = clock.GetUtcNow();
        foreach (var e in events)
        {
            await c.ExecuteAsync(
                """
                INSERT INTO session_events (session_id, club_id, source, type, at, received_at, data, applied)
                VALUES (@Id, @ClubId, 'agent', @type, @at, @now, @data::jsonb, false)
                ON CONFLICT (session_id, type, at) WHERE source = 'agent' DO NOTHING
                """,
                new
                {
                    s.Id, s.ClubId, type = JsonNamingPolicy.CamelCase.ConvertName(e.Type.ToString()), at = e.At.AddTicks(-(e.At.UtcTicks % TimeSpan.TicksPerMillisecond)),
                    now, data = e.Data is { } d ? ServerJson.Jsonb(d.GetRawText()) : null,
                },
                tx);
        }
    }

    /// <summary>
    /// <c>POST /sessions/{id}/extend</c> in the idempotency transaction (§5.6): prepaid only, the full rules of §5.2 for the
    /// (possibly new) tariff, priced now, charged at once; returns the session and the charge. The cashier's extend
    /// (<paramref name="staff"/>, S4) also queues <c>extendSession {charge:false}</c>: the agent rereads the session instead
    /// of charging again (§5.6). Both are <c>extend = online</c> in the charge's meta, so a late agent <c>extended</c> event
    /// for the same minutes is not charged twice (§5.12). The tick's auto-extension (<paramref name="notifyAgent"/>, no
    /// staff) queues the same command: the PC did not ask for it.
    /// </summary>
    public async Task<(Session Session, long Charged)> ExtendAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId, Guid userId, Guid pcId, int minutes, Guid? tariffId, SessionEffects effects,
        StaffContext? staff = null, bool notifyAgent = false)
    {
        var now = clock.GetUtcNow();
        var s = await OwnedAsync(c, tx, sessionId, userId, pcId);
        if (!s.IsPrepaid)
        {
            throw Conflict("postpaidSession");
        }

        if (s.Ended)
        {
            throw NotActive(s.ToWire(now));
        }

        var club = await ClubAsync(c, tx, s.ClubId);
        var tariff = await TariffAsync(c, tx, club.Id, tariffId ?? s.TariffId) ?? throw ApiException.NotFound("tariff");
        var buyer = await BuyerAsync(c, tx, club, s.UserId) ?? throw ApiException.NotFound("user");
        var (zone, maintenance) = await c.QuerySingleAsync<(string, bool)>("SELECT zone, maintenance FROM pcs WHERE id = @PcId", new { s.PcId }, tx);
        if (maintenance)
        {
            throw PolicyDenied("pcMaintenance");
        }

        CheckRules(zone, buyer, tariff, club, now, offlineReplay: false);
        if (tariff.IsPackage)
        {
            minutes = tariff.PackageMinutes!.Value;
        }
        else if (tariff.MaxMinutes is { } max && (s.PurchasedSec / 60) + minutes > max)
        {
            throw ApiException.Validation("minutes", "max");
        }

        var quote = Pricing.Compute(tariff, minutes, buyer.GroupId, buyer.LifetimeSpent, zone, now, club.Pricing);
        if (quote.Total > 0)
        {
            await Ledger.PostAsync(c, tx, s.UserId, allowOverdraft: false, now, new LedgerLine(
                "charge", -quote.Total, $"Продление +{minutes} мин · {tariff.Name}", club.Id, s.Id, s.PcId, StaffId: staff?.StaffId,
                Meta: new { quote, extend = "online" }));
            effects.Wallets.Add(s.UserId);
        }

        s.PricePerHourSnapshot = tariff.PricePerHour;
        s.Extend(minutes * 60, tariff.Id, quote.Total, now);
        await SaveAsync(c, tx, s, now);
        var session = s.ToWire(now);
        effects.Sessions.Add(session);
        if (staff is not null || notifyAgent)
        {
            effects.Commands.Add((s.ClubId, s.PcId, NewCommand.ExtendSession(new ExtendSessionCommand(s.Id, minutes, Charge: false))));
        }

        return (session, quote.Total);
    }

    /// <summary>
    /// <c>POST /sessions/{id}/end</c> (§5.7): settled at the server's now. The agent's <c>secondsUsed</c>/<c>endedAt</c>
    /// are kept in <c>agent_reported</c> for audit only. An ended session is <c>409 sessionNotActive</c> + <c>details.session</c>;
    /// so is a session the desk moved off the caller's PC (D-61), with its «ended view»: the old PC ends it cleanly and
    /// nothing is settled.
    /// </summary>
    public async Task<SessionEndResult> EndAsync(Guid sessionId, Guid pcId, SessionEndReport report)
    {
        var effects = new SessionEffects();
        SessionEndResult result;
        var (c, tx) = await BeginAsync(db);
        await using (c)
        await using (tx)
        {
            var now = clock.GetUtcNow();
            var s = await LockAsync(c, tx, sessionId) ?? throw ApiException.NotFound("session");
            if (s.PcId != pcId)
            {
                throw await MovedOffAsync(c, tx, s.Id, pcId) is { } moved
                    ? NotActive(EndedView(s, pcId, moved.At, moved.SecondsUsed))
                    : ApiException.Forbidden("pcMismatch", "The session belongs to another PC");
            }

            if (s.Ended)
            {
                throw NotActive(s.ToWire(now));
            }

            await c.ExecuteAsync(
                "UPDATE sessions SET agent_reported = @reported::jsonb WHERE id = @sessionId",
                new { sessionId, reported = JsonSerializer.Serialize(new { report.SecondsUsed, report.EndedAt }, ServerJson.Options) },
                tx);
            var (charged, refunded) = await SettleAsync(c, tx, s, now, report.Reason, effects);
            await tx.CommitAsync();
            result = new SessionEndResult(effects.Sessions[^1], Money.Uzs(charged), Money.Uzs(refunded));
        }

        await PublishAsync(effects);
        return result;
    }

    /// <summary>
    /// Settlement (§5.7) of a locked open session at <paramref name="end"/>: prepaid time used is capped by what was bought;
    /// a refund only for <c>admin</c>/<c>error</c>, of the hourly purchases, pro rata to what each actually paid and floored
    /// to 100 tiyin; postpaid is charged the frozen price of <c>ceil(used / 60)</c> minutes less what an earlier settlement
    /// charged (a reopened session), overdraft allowed. Saves the row.
    /// </summary>
    public async Task<(long Charged, long Refunded)> SettleAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, SessionRow s, DateTimeOffset end, SessionEndReason reason, SessionEffects effects)
    {
        var now = clock.GetUtcNow();
        var paid = s.ChargedTotal - s.RefundedTotal;
        var purchased = s.PurchasedSec;
        var used = s.End(end, reason);
        var (charged, refunded) = (0L, 0L);
        if (s.IsPrepaid)
        {
            if (reason is SessionEndReason.Admin or SessionEndReason.Error && purchased > used && paid > 0)
            {
                refunded = Math.Min(paid, await RefundableAsync(c, tx, s.Id, purchased - used)) / 100 * 100;
            }
        }
        else
        {
            charged = Math.Max(0, Pricing.Frozen(s.PricePerHourSnapshot, used, s.DayPct, s.DiscountPct) - s.ChargedTotal);
        }

        if (charged > 0 || refunded > 0)
        {
            await Ledger.PostAsync(c, tx, s.UserId, allowOverdraft: true, now, charged > 0
                ? new LedgerLine("charge", -charged, $"Постоплата {(used + 59) / 60} мин", s.ClubId, s.Id, s.PcId)
                : new LedgerLine("refund", refunded, "Возврат за неиспользованное время", s.ClubId, s.Id, s.PcId));
            effects.Wallets.Add(s.UserId);
        }

        s.ChargedTotal += charged;
        s.RefundedTotal += refunded;
        await SaveAsync(c, tx, s, now);
        effects.Sessions.Add(s.ToWire(now));
        return (charged, refunded);
    }

    /// <summary>
    /// Late agent events of one locked session (§5.12), oldest first. A repeat of (type, at) is skipped; an event older
    /// than the last server transition is only recorded (<c>applied = false</c>), except <c>ended</c>, which closes the
    /// session at that transition (the agent has closed it: left open it would come back as a zombie); a newer one is
    /// applied at <c>min(at, now)</c>. Paused/resumed/extended/ended change the clock and money like the online calls;
    /// extended is charged with overdraft (the agent already gave the time). A session the tick closed after
    /// <c>MaxOfflineMinutes</c> of silence is reopened at that end when the agent comes back with its own later end: the
    /// agent was the authority offline (§5.10), so the time up to its end is billed after all.
    /// </summary>
    public async Task ApplyEventsAsync(NpgsqlConnection c, NpgsqlTransaction tx, SessionRow s, IEnumerable<SessionEvent> events, SessionEffects effects)
    {
        var now = clock.GetUtcNow();
        var changed = false;
        var batch = events.OrderBy(e => e.At).ToList();

        // ponytail: only this batch is replayed on the reopened clock; agent events of an earlier batch after the timeout
        // (more than 100 offline events) stay unapplied. Upgrade: replay the recorded ones too.
        if (s is { Ended: true, EndReason: "timeUp" } && batch.Any(e => e.Type == SessionEventType.Ended && e.At > s.EndedAt)
            && await c.QuerySingleOrDefaultAsync<bool?>(
                "SELECT (data->>'running')::boolean FROM session_events WHERE session_id = @Id AND source = 'server' AND type = 'offlineTimeout' AND at = @EndedAt",
                new { s.Id, s.EndedAt }, tx) is { } running)
        {
            s.Reopen(running);
        }

        foreach (var e in batch)
        {
            var type = JsonNamingPolicy.CamelCase.ConvertName(e.Type.ToString());
            var at = e.At.AddTicks(-(e.At.UtcTicks % TimeSpan.TicksPerMillisecond));
            if (await c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM session_events WHERE session_id = @Id AND type = @type AND at = @at AND source = 'agent')",
                    new { s.Id, type, at }, tx))
            {
                continue;
            }

            var t = e.Type == SessionEventType.Ended ? Max(at, s.LastTransitionAt) : at;
            var applied = !s.Ended && t >= s.LastTransitionAt && await ApplyAsync(c, tx, s, e, Min(t, now), effects);
            changed |= applied;
            await c.ExecuteAsync(
                """
                INSERT INTO session_events (session_id, club_id, source, type, at, received_at, data, applied)
                VALUES (@Id, @ClubId, 'agent', @type, @at, @now, @data::jsonb, @applied)
                ON CONFLICT (session_id, type, at) WHERE source = 'agent' DO NOTHING
                """,
                new { s.Id, s.ClubId, type, at, now, data = e.Data is { } d ? ServerJson.Jsonb(d.GetRawText()) : null, applied },
                tx);
        }

        if (changed && !s.Ended)
        {
            await SaveAsync(c, tx, s, now);
            effects.Sessions.Add(s.ToWire(now));
        }
    }

    private async Task<bool> ApplyAsync(NpgsqlConnection c, NpgsqlTransaction tx, SessionRow s, SessionEvent e, DateTimeOffset t, SessionEffects effects)
    {
        switch (e.Type)
        {
            case SessionEventType.Locked when s.State == "active":
                s.State = "locked";
                s.LastTransitionAt = t;
                return true;
            case SessionEventType.Unlocked when s.State == "locked":
                s.State = "active";
                s.LastTransitionAt = t;
                return true;
            case SessionEventType.Warning when e.DataAs<SessionWarningData>() is { } warning && !s.WarningsSent.Contains(warning.MinutesLeft):
                s.WarningsSent = [.. s.WarningsSent, warning.MinutesLeft];
                return true;
            case SessionEventType.Paused when s.Running:
                s.Pause(t);
                return true;
            case SessionEventType.Resumed when s.State == "paused":
                s.Resume(t);
                return true;
            // The online extend's range (1..1440); a package brings its own minutes, as online (§5.13 #3).
            case SessionEventType.Extended when s.IsPrepaid && e.DataAs<SessionExtendedData>() is { Minutes: >= 1 and <= 1440 } extended:
                var club = await ClubAsync(c, tx, s.ClubId);
                var tariff = (await TariffAsync(c, tx, s.ClubId, s.TariffId, withDeleted: true))!;
                var minutes = tariff.IsPackage ? tariff.PackageMinutes!.Value : extended.Minutes;

                // An online extend whose answer the agent lost is applied locally and queued as this event: the charge the
                // server already took for the same minutes around that time is not taken twice.
                // ponytail: matched by time (the agent's clock) within 2 min; upgrade: the agent's Idempotency-Key in the event data.
                if (await c.ExecuteScalarAsync<bool>(
                        """
                        SELECT EXISTS (SELECT 1 FROM ledger_entries WHERE session_id = @Id AND type = 'charge' AND meta->>'extend' = 'online'
                                       AND (meta->'quote'->>'minutes')::int = @minutes AND created_at BETWEEN @from AND @to)
                        """,
                        new { s.Id, minutes, from = t.AddMinutes(-2), to = t.AddMinutes(2) }, tx))
                {
                    return false;
                }

                var buyer = await BuyerAsync(c, tx, club, s.UserId);
                var zone = await c.ExecuteScalarAsync<string>("SELECT zone FROM pcs WHERE id = @PcId", new { s.PcId }, tx) ?? "";
                var quote = Pricing.Compute(tariff, minutes, buyer?.GroupId, buyer?.LifetimeSpent, zone, t, club.Pricing);
                if (quote.Total > 0)
                {
                    await Ledger.PostAsync(c, tx, s.UserId, allowOverdraft: true, clock.GetUtcNow(), new LedgerLine(
                        "charge", -quote.Total, $"Продление +{minutes} мин (офлайн) · {tariff.Name}", s.ClubId, s.Id, s.PcId,
                        Meta: new { quote, extend = "offline", agentCost = extended.Cost }));
                    effects.Wallets.Add(s.UserId);
                }

                s.Extend(minutes * 60, s.TariffId, quote.Total, t);
                return true;
            case SessionEventType.Ended:
                await SettleAsync(c, tx, s, t, e.DataAs<SessionEndedData>()?.Reason is { } reason and not SessionEndReason.Unknown ? reason : SessionEndReason.User, effects);
                return true;
            default:
                // started, charged and anything that does not fit the current state are only recorded.
                return false;
        }
    }

    /// <summary>
    /// Sends what a committed change produced, in this order: <c>userRevoked</c> of a seat taken, the sessions, the ended
    /// views of moved sessions to their old PCs, the wallets, the commands, <c>userRevoked</c> of a session ended or moved. A
    /// failure here must not fail a request whose money is committed.
    /// </summary>
    public async Task PublishAsync(SessionEffects effects)
    {
        try
        {
            foreach (var (userId, pcId, reason) in effects.RevokedBefore)
            {
                await pushes.UserRevokedAsync(userId, reason, [pcId]);
            }

            foreach (var session in effects.Sessions.GroupBy(s => s.Id).Select(g => g.Last()))
            {
                await pushes.SessionAsync(session);
            }

            foreach (var view in effects.Departed)
            {
                await pushes.SessionAsync(view);
            }

            foreach (var userId in effects.Wallets)
            {
                await pushes.WalletAsync(userId);
            }

            foreach (var (clubId, pcId, command) in effects.Commands)
            {
                await commands.EnqueueAsync(clubId, pcId, command);
            }

            foreach (var (userId, pcId, reason) in effects.RevokedAfter)
            {
                await pushes.UserRevokedAsync(userId, reason, [pcId]);
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Pushes after a committed session change failed");
        }

        foreach (var (clubId, sessionId, pcId, userId) in effects.Opened)
        {
            try
            {
                await automation.SessionOpenedAsync(clubId, sessionId, pcId, userId);
            }
            catch (NpgsqlException ex)
            {
                logger.LogWarning(ex, "Automation after session {SessionId} opened failed", sessionId);
            }
        }
    }

    /// <summary>
    /// Signs the player of an ended session out of its PC in the caller's transaction (D-28): the token goes, and
    /// <c>userRevoked {reason: sessionEnded}</c> follows the commands after the commit (the agent drops only that player).
    /// </summary>
    public static async Task SignOutAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, Guid pcId, SessionEffects effects)
    {
        await c.ExecuteAsync("DELETE FROM user_tokens WHERE user_id = @userId AND pc_id = @pcId", new { userId, pcId }, tx);
        effects.RevokedAfter.Add((userId, pcId, "sessionEnded"));
    }

    public static async Task SaveAsync(NpgsqlConnection c, NpgsqlTransaction tx, SessionRow s, DateTimeOffset now) =>
        await c.ExecuteAsync(
            """
            UPDATE sessions SET state = @State, ended_at = @EndedAt, end_reason = @EndReason, purchased_sec = @PurchasedSec,
                                used_before_sec = @UsedBeforeSec, running_since = @RunningSince, paused_at = @PausedAt,
                                ends_at = @EndsAt, last_transition_at = @LastTransitionAt, tariff_id = @TariffId,
                                price_per_hour_snapshot = @PricePerHourSnapshot, charged_total = @ChargedTotal,
                                refunded_total = @RefundedTotal, warnings_sent = @WarningsSent, updated_at = @now
            WHERE id = @Id
            """,
            new
            {
                s.Id, s.State, s.EndedAt, s.EndReason, s.PurchasedSec, s.UsedBeforeSec, s.RunningSince, s.PausedAt, s.EndsAt,
                s.LastTransitionAt, s.TariffId, s.PricePerHourSnapshot, s.ChargedTotal, s.RefundedTotal, s.WarningsSent, now,
            },
            tx);

    private async Task<Session> CommitAsync(NpgsqlConnection c, NpgsqlTransaction tx, SessionRow s, DateTimeOffset now, SessionEffects effects)
    {
        await SaveAsync(c, tx, s, now);
        await tx.CommitAsync();
        var session = s.ToWire(now);
        effects.Sessions.Add(session);
        await PublishAsync(effects);
        return session;
    }

    /// <summary>
    /// The locked session of the player on the agent's PC: <c>404 what=session</c>, another player's is <c>403 notOwner</c>,
    /// one on another PC <c>403 pcMismatch</c> (a player token acts only on its own PC, as <c>/end</c>).
    /// </summary>
    private static async Task<SessionRow> OwnedAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId, Guid userId, Guid pcId)
    {
        var s = await LockAsync(c, tx, sessionId) ?? throw ApiException.NotFound("session");
        if (s.UserId != userId)
        {
            throw ApiException.Forbidden("notOwner", "The session belongs to another user");
        }

        return s.PcId == pcId ? s : throw ApiException.Forbidden("pcMismatch", "The session belongs to another PC");
    }

    private static ApiException InsufficientFunds(long required, long available) =>
        new(StatusCodes.Status402PaymentRequired, ErrorCode.InsufficientFunds, "Balance too low",
            new { required = Money.Uzs(required), available = Money.Uzs(available) });

    /// <summary>
    /// The refund for <paramref name="unusedSec"/> seconds of a prepaid session, unfloored: the unused seconds are the
    /// last ones bought, so the charges are walked newest first, each covering its quote's minutes; a package part is
    /// never refunded, an hourly part pro rata to what that charge took (§5.7).
    /// </summary>
    private static async Task<long> RefundableAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId, int unusedSec)
    {
        var charges = await c.QueryAsync<(long Paid, int Minutes, bool Package)>(
            """
            SELECT -amount, coalesce((coalesce(meta->'quote', meta)->>'minutes')::int, 0), coalesce((coalesce(meta->'quote', meta)->>'package')::boolean, false)
            FROM ledger_entries WHERE session_id = @sessionId AND type = 'charge'
            ORDER BY created_at DESC, id DESC
            """,
            new { sessionId }, tx);
        var refund = 0L;
        foreach (var (paid, minutes, package) in charges)
        {
            var take = Math.Min(unusedSec, minutes * 60);
            refund += package || take <= 0 ? 0 : paid * take / (minutes * 60);
            unusedSec -= take;
        }

        return refund;
    }

    /// <summary>Rule 3 of §5.2: <c>409 sessionAlreadyActive {sessionId, pcId}</c> when the PC or the player has an open session.</summary>
    private static async Task EnsureNothingOpenAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid pcId, Guid userId)
    {
        var open = await c.QueryFirstOrDefaultAsync<SessionRow>(
            $"SELECT {SessionRow.Columns} FROM sessions WHERE state <> 'ended' AND (pc_id = @pcId OR user_id = @userId) LIMIT 1", new { pcId, userId }, tx);
        if (open is not null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.SessionAlreadyActive, "An open session already exists",
                new { sessionId = open.Id, pcId = open.PcId });
        }
    }

    /// <summary>Rules 4–7 of §5.2 for a purchase at <paramref name="at"/> on a PC of <paramref name="zone"/>; the offline replay only rule 4.</summary>
    private static void CheckRules(string zone, Buyer buyer, TariffRow tariff, ClubInfo club, DateTimeOffset at, bool offlineReplay)
    {
        if (buyer.Banned || buyer.Blacklisted)
        {
            throw PolicyDenied("blacklisted");
        }

        if (offlineReplay)
        {
            return;
        }

        if (club.Pricing.InCurfew(buyer.BirthYear, at))
        {
            throw PolicyDenied("minorCurfew");
        }

        if (TariffRule(zone, tariff, club, at) is { } rule)
        {
            throw PolicyDenied(rule);
        }
    }

    /// <summary>
    /// Rules 6–7 of §5.2, the tariff's own: <c>tariffZone</c> when the PC's zone is not one of its zones, <c>tariffTime</c>
    /// outside its windows at <paramref name="at"/> (club-local), else null. <c>adminQuote</c> names it, so the desk shows a
    /// package card outside its window disabled (D-38); the buyer's rules are checked only on open.
    /// </summary>
    public static string? TariffRule(string zone, TariffRow tariff, ClubInfo club, DateTimeOffset at)
    {
        if (tariff.Zones.Length > 0 && !tariff.Zones.Contains(zone, StringComparer.OrdinalIgnoreCase))
        {
            return "tariffZone";
        }

        var local = ClubTime.Local(at, club.Pricing.TimeZone);
        return tariff.ToWire().IsValidFor(zone, local.DayOfWeek.ToWeekday(), TimeOnly.FromDateTime(local)) ? null : "tariffTime";
    }

    /// <summary>Rule 8 of §5.2 for an hourly tariff at creation: <c>minMinutes..maxMinutes</c>.</summary>
    private static void CheckMinutes(TariffRow tariff, int minutes)
    {
        if (minutes < Math.Max(1, tariff.MinMinutes))
        {
            throw ApiException.Validation("minutes", "min");
        }

        if (tariff.MaxMinutes is { } max && minutes > max)
        {
            throw ApiException.Validation("minutes", "max");
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
