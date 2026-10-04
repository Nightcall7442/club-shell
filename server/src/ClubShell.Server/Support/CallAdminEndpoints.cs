using System.Globalization;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Users;
using ClubShell.Server.Admin;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Support;

/// <summary>
/// «Позвать администратора» (cash desk part 3, D-62, D-63): the contract's <c>callAdmin</c> (<c>POST /support/call-admin</c>,
/// now implemented; <c>201 {ticketId, createdAt, queuePosition}</c>) and, beyond the contract, the desk's inbox —
/// <c>POST /admin/calls/{id}/ack</c> («Иду») and <c>/resolve</c> («Закрыть»); the open and acknowledged calls ride on the
/// overview (<see cref="AdminCalls.LiveAsync"/>). The only 4xx for valid input is <c>403 pcMismatch</c>: agent 1.0.16 shows any
/// other 4xx to the player and does not fall back to telemetry. The <c>Idempotency-Key</c> is honoured (the agent sends a
/// new one per call); <c>admin_calls UNIQUE(pc_id, at)</c> keeps a call and its telemetry copy as one.
/// </summary>
public static class CallAdminEndpoints
{
    public static readonly string[] Operations = ["callAdmin"];

    /// <summary>«Иду» reaches the PC only within this: an offline PC would show it to the next player (D-63).</summary>
    public static readonly TimeSpan OnTheWayTtl = TimeSpan.FromMinutes(2);

    public static void MapCallAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapApiGroup("/api/v1/support").MapPost("/call-admin", CallAsync).WithMetadata(new AuthRequirement(AuthMode.AgentOptionalUser));
        var admin = app.MapApiGroup("/api/v1/admin/calls").WithMetadata(new AuthRequirement(AuthMode.Staff));
        admin.MapPost("/{id}/ack", AckAsync);
        admin.MapPost("/{id}/resolve", ResolveAsync);
    }

    /// <summary>
    /// The PC's call (<c>CallAdminTicketRequest</c>): the player from the token, else the body's <c>userId</c> only if that
    /// player holds a live token of this PC, else the PC's open-session player. A repeat of <c>(pc, at)</c> answers the call
    /// already stored; <c>queuePosition</c> = 1 + the club's open calls received before it.
    /// </summary>
    private static async Task<IResult> CallAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, TimeProvider clock)
    {
        var agent = context.Features.GetRequiredFeature<AgentContext>();
        var r = Api.Read<CallRequest>(body, "pcId", "category", "at");
        var category = AdminCalls.Categories.Contains(r.Category, StringComparer.Ordinal) ? r.Category! : throw ApiException.Validation("category", "enum");
        var message = r.Message?.Trim() is { Length: > 0 } given ? given : null;
        if (message is { Length: > 500 })
        {
            throw ApiException.Validation("message", "max");
        }

        if (r.PcId != agent.Pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "pcId does not match the agent token");
        }

        var tokenUser = context.Features.Get<UserContext>()?.UserId;
        var principal = "pc:" + agent.Pc.Id.ToString("D", CultureInfo.InvariantCulture);
        return await store.ExecuteHttpAsync(context, principal, keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var call = (await AdminCalls.InsertAsync(c, tx, agent.Pc, new AdminCalls.Candidate(category, message, "direct", r.At!.Value, tokenUser, r.UserId), now))!;
            var position = 1 + await c.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM admin_calls WHERE club_id = @ClubId AND status = 'open' AND (received_at, id) < (@ReceivedAt, @Id)",
                new { call.ClubId, call.ReceivedAt, call.Id }, tx);
            return new IdempotentResult(StatusCodes.Status201Created, JsonDefaults.ToElement(new SysCallAdminResponse(call.Id, call.ReceivedAt, position)));
        });
    }

    /// <summary>
    /// «Иду» (D-63): this call and every older open call of the same PC are acknowledged; with <c>notify</c> (default true)
    /// and only while the PC is connected, «Администратор идёт к вам» goes as a non-blocking <c>message</c> that expires in 2
    /// minutes, in the caller's language (<c>notified</c>). An acknowledged or resolved call answers its state and sends
    /// nothing again. Journal <c>callAck</c>.
    /// </summary>
    private static async Task<IResult> AckAsync(
        HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, CommandDispatcher dispatcher, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var callId = Guid.TryParse(id, out var parsed) ? parsed : throw ApiException.NotFound("call");
        var notify = Api.Read<AdminCallAckRequest>(body).Notify ?? true;
        ServerCommandEnvelope? sent = null;
        AdminCalls.CallRow call;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            call = await AdminCalls.LockAsync(c, tx, staff.ClubId, callId);
            if (call.Status == "open")
            {
                await c.ExecuteAsync(
                    """
                    UPDATE admin_calls SET status = 'acked', acked_at = @now, acked_by_staff_id = @StaffId, acked_by_name = @Name
                    WHERE club_id = @ClubId AND pc_id = @PcId AND status = 'open' AND received_at <= @ReceivedAt
                    """,
                    new { now, staff.StaffId, staff.Name, call.ClubId, call.PcId, call.ReceivedAt }, tx);
                if (notify && hub.IsConnected(call.PcId))
                {
                    var locale = call.UserId is { } userId
                        ? await c.ExecuteScalarAsync<string?>("SELECT locale FROM users WHERE id = @userId", new { userId }, tx)
                        : null;
                    sent = await dispatcher.QueueAsync(tx, call.ClubId, call.PcId, NewCommand.Message(new MessageCommand(
                        Guid.NewGuid(), "Администратор", OnTheWay(locale), NotificationLevel.Info, RequiresAck: false)), issuedByStaffId: staff.StaffId, ttl: OnTheWayTtl);
                }

                await Audit.WriteAsync(c, tx, staff, now, "callAck", call.UserId, call.PcId, detail: call.PcName,
                    meta: new { callId, category = call.Category, notified = sent is not null });
                call = await AdminCalls.LockAsync(c, tx, staff.ClubId, callId);
            }

            await tx.CommitAsync();
        }

        if (sent is not null)
        {
            await dispatcher.SendAsync(call.PcId, sent);
        }

        return AdminJson.Ok(new AdminCallAckResponse(call.ToWire(), sent is not null));
    }

    /// <summary>«Закрыть»: this call and every older open or acknowledged call of the same PC are resolved. Journal <c>callResolve</c>.</summary>
    private static async Task<IResult> ResolveAsync(HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var callId = Guid.TryParse(id, out var parsed) ? parsed : throw ApiException.NotFound("call");
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("body", "schema", "JSON object expected");
        }

        AdminCalls.CallRow call;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            call = await AdminCalls.LockAsync(c, tx, staff.ClubId, callId);
            if (call.Status != "resolved")
            {
                await c.ExecuteAsync(
                    """
                    UPDATE admin_calls SET status = 'resolved', resolved_at = @now, resolved_by_staff_id = @StaffId, resolved_by_name = @Name
                    WHERE club_id = @ClubId AND pc_id = @PcId AND status <> 'resolved' AND received_at <= @ReceivedAt
                    """,
                    new { now, staff.StaffId, staff.Name, call.ClubId, call.PcId, call.ReceivedAt }, tx);
                await Audit.WriteAsync(c, tx, staff, now, "callResolve", call.UserId, call.PcId, detail: call.PcName, meta: new { callId, category = call.Category });
                call = await AdminCalls.LockAsync(c, tx, staff.ClubId, callId);
            }

            await tx.CommitAsync();
        }

        return AdminJson.Ok(new AdminCallResponse(call.ToWire()));
    }

    /// <summary>«Администратор идёт к вам» in the player's language (Russian when unknown).</summary>
    private static string OnTheWay(string? locale) => locale switch
    {
        "uz" => "Administrator yoningizga kelmoqda",
        "en" => "An administrator is on the way",
        _ => "Администратор идёт к вам",
    };

    /// <summary><c>CallAdminTicketRequest</c> with the category as text, so an unknown one is <c>400 category enum</c>.</summary>
    private sealed record CallRequest(Guid? PcId, Guid? UserId, string? Category, string? Message, DateTimeOffset? At);
}

/// <summary>
/// The admin-call inbox (<c>admin_calls</c>, D-62): one row per call, from the route (<c>direct</c>), from telemetry when the
/// route was not reached (<c>telemetry</c>) or a «report a problem» text (<c>report</c>, category <c>problem</c>).
/// <c>UNIQUE(pc_id, at)</c> keeps a call and its telemetry copy as one (the agent sends the same <c>at</c> both ways). A call
/// from a PC whose call was acknowledged in the last 10 minutes, with none open, is stored acknowledged with
/// <c>repeat</c>, so the desk does not ring again; with an open call it joins the same group; a telemetry call older than 30
/// minutes is stored resolved; at most 5 problem reports per PC per hour are kept.
/// </summary>
public static class AdminCalls
{
    /// <summary>The contract's <c>CallAdminCategory</c>.</summary>
    public static readonly string[] Categories = ["help", "technical", "order", "other"];

    /// <summary>The shell's «report a problem» text arrives as a client error with this prefix (SupportScreen.tsx).</summary>
    public const string ReportPrefix = "[user report] ";

    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    private const int ReportsPerHour = 5;

    private const string Columns = """
        id, club_id, pc_id, pc_name, pc_number, user_id, user_name, category, message, source, at, received_at, status, repeat,
        acked_at, acked_by_name
        """;

    /// <summary>A call to store: <paramref name="TokenUser"/> from the request's player token, <paramref name="ClaimedUser"/> as the PC said.</summary>
    public sealed record Candidate(string Category, string? Message, string Source, DateTimeOffset At, Guid? TokenUser, Guid? ClaimedUser);

    /// <summary>
    /// Stores <paramref name="candidate"/> for <paramref name="pc"/> in the caller's transaction and returns the call — or the
    /// one already stored for the same <c>(pc, at)</c>; null when a problem report is over the hourly cap (dropped).
    /// </summary>
    public static async Task<CallRow?> InsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, PcRow pc, Candidate candidate, DateTimeOffset now)
    {
        var at = candidate.At.ToUniversalTime().AddTicks(-(candidate.At.UtcTicks % TimeSpan.TicksPerMillisecond));
        if (await c.QuerySingleOrDefaultAsync<CallRow>($"SELECT {Columns} FROM admin_calls WHERE pc_id = @Id AND at = @at", new { pc.Id, at }, tx) is { } known)
        {
            return known;
        }

        var problem = candidate.Category == "problem";
        if (problem && await c.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM admin_calls WHERE pc_id = @Id AND category = 'problem' AND received_at > @since",
                new { pc.Id, since = now - TimeSpan.FromHours(1) }, tx) >= ReportsPerHour)
        {
            return null;
        }

        // The player: the token's, else the one the PC named if signed in there (a report: whoever is), else the session's.
        var userId = candidate.TokenUser
            ?? await c.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT user_id FROM user_tokens WHERE pc_id = @Id AND expires_at > @now AND (@claimed::uuid IS NULL OR user_id = @claimed) AND (@claimed::uuid IS NOT NULL OR @report)",
                new { pc.Id, now, claimed = candidate.ClaimedUser, report = candidate.Source == "report" }, tx)
            ?? await c.QuerySingleOrDefaultAsync<Guid?>("SELECT user_id FROM sessions WHERE pc_id = @Id AND state <> 'ended'", new { pc.Id }, tx);
        var userName = userId is null ? null : await c.ExecuteScalarAsync<string?>("SELECT display_name FROM users WHERE id = @userId", new { userId }, tx);

        var status = "open";
        var repeat = false;
        (DateTimeOffset At, Guid? StaffId, string? Name)? acked = null;
        if (candidate.Source == "telemetry" && now - at > StaleAfter)
        {
            status = "resolved";
        }
        else if (!problem && !await c.ExecuteScalarAsync<bool>(
                     "SELECT EXISTS (SELECT 1 FROM admin_calls WHERE pc_id = @Id AND status = 'open' AND category <> 'problem')", new { pc.Id }, tx))
        {
            acked = await c.QuerySingleOrDefaultAsync<(DateTimeOffset, Guid?, string?)?>(
                """
                SELECT acked_at, acked_by_staff_id, acked_by_name FROM admin_calls
                WHERE pc_id = @Id AND category <> 'problem' AND acked_at > @since ORDER BY acked_at DESC LIMIT 1
                """,
                new { pc.Id, since = now - RepeatWindow }, tx);
            if (acked is not null)
            {
                (status, repeat) = ("acked", true);
            }
        }

        return await c.QuerySingleOrDefaultAsync<CallRow>(
            $"""
            INSERT INTO admin_calls (id, club_id, pc_id, pc_name, pc_number, user_id, user_name, category, message, source, at, received_at, status, repeat,
                                     acked_at, acked_by_staff_id, acked_by_name, resolved_at, resolved_by_name)
            VALUES (@callId, @ClubId, @Id, @Name, @Number, @userId, @userName, @Category, @Message, @Source, @at, @now, @status, @repeat,
                    @ackedAt, @ackedBy, @ackedByName, @resolvedAt, @resolvedBy)
            ON CONFLICT (pc_id, at) DO NOTHING
            RETURNING {Columns}
            """,
            new
            {
                callId = Guid.CreateVersion7(now), pc.ClubId, pc.Id, pc.Name, pc.Number, userId, userName, candidate.Category, candidate.Message, candidate.Source,
                at, now, status, repeat, ackedAt = acked is null ? (DateTimeOffset?)null : now, ackedBy = acked?.StaffId, ackedByName = acked?.Name,
                resolvedAt = status == "resolved" ? now : (DateTimeOffset?)null, resolvedBy = status == "resolved" ? "auto" : null,
            },
            tx)
            ?? await c.QuerySingleAsync<CallRow>($"SELECT {Columns} FROM admin_calls WHERE pc_id = @Id AND at = @at", new { pc.Id, at }, tx);
    }

    /// <summary>
    /// The admin calls of a telemetry batch (D-62), checked here so that one bad event is skipped and logged instead of failing
    /// the batch: <c>callAdmin</c> — its <c>CallAdminTicketRequest</c> with a known category, a parsable <c>at</c>, this PC's
    /// <c>pcId</c> and a message of at most 500 characters (as the route); <c>shellClientError</c> whose message starts with
    /// «[user report] » — a <c>problem</c> call of the rest of the text (cut to 500), at the event's time.
    /// </summary>
    public static List<Candidate> FromTelemetry(PcRow pc, IEnumerable<TelemetryEvent> events, ILogger logger)
    {
        var calls = new List<Candidate>();
        foreach (var e in events)
        {
            if (e.Kind == "callAdmin")
            {
                var d = e.Data;
                string? Text(string key) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var category = Text("category");
                var message = Text("message")?.Trim() is { Length: > 0 } m ? m : null;
                if (!Guid.TryParse(Text("pcId"), out var pcId) || pcId != pc.Id || !Categories.Contains(category, StringComparer.Ordinal) || message is { Length: > 500 }
                    || !DateTimeOffset.TryParse(Text("at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                {
                    logger.LogWarning("PC {PcId}: a callAdmin telemetry event that is not a valid call was skipped", pc.Id);
                    continue;
                }

                calls.Add(new Candidate(category!, message, "telemetry", at, null, Guid.TryParse(Text("userId"), out var userId) ? userId : null));
            }
            else if (e.Kind == "shellClientError" && e.Data.ValueKind == JsonValueKind.Object && e.Data.TryGetProperty("message", out var text)
                     && text.ValueKind == JsonValueKind.String && text.GetString() is { } report && report.StartsWith(ReportPrefix, StringComparison.Ordinal))
            {
                var body = report[ReportPrefix.Length..].Trim();
                if (body.Length == 0)
                {
                    logger.LogWarning("PC {PcId}: an empty problem report was skipped", pc.Id);
                    continue;
                }

                calls.Add(new Candidate("problem", body.Length > 500 ? body[..500] : body, "report", e.At, null, null));
            }
        }

        return calls;
    }

    /// <summary>The calls of the desk inbox (D-63): open or acknowledged, received in the last 12 h, newest first, at most 50.</summary>
    public static async Task<IReadOnlyList<AdminCall>> LiveAsync(NpgsqlConnection c, Guid clubId, DateTimeOffset now) =>
        (await c.QueryAsync<CallRow>(
            $"""
            SELECT {Columns} FROM admin_calls
            WHERE club_id = @clubId AND status <> 'resolved' AND received_at > @since
            ORDER BY received_at DESC, id DESC LIMIT 50
            """,
            new { clubId, since = now - TimeSpan.FromHours(12) }))
        .Select(r => r.ToWire()).ToList();

    /// <summary>A call of the club, locked; else <c>404 what=call</c>.</summary>
    public static async Task<CallRow> LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, Guid callId) =>
        await c.QuerySingleOrDefaultAsync<CallRow>($"SELECT {Columns} FROM admin_calls WHERE id = @callId AND club_id = @clubId FOR UPDATE", new { callId, clubId }, tx)
        ?? throw ApiException.NotFound("call");

    public sealed class CallRow
    {
        public Guid Id { get; init; }
        public Guid ClubId { get; init; }
        public Guid PcId { get; init; }
        public string PcName { get; init; } = "";
        public int PcNumber { get; init; }
        public Guid? UserId { get; init; }
        public string? UserName { get; init; }
        public string Category { get; init; } = "";
        public string? Message { get; init; }
        public string Source { get; init; } = "";
        public DateTimeOffset At { get; init; }
        public DateTimeOffset ReceivedAt { get; init; }
        public string Status { get; init; } = "";
        public bool Repeat { get; init; }
        public DateTimeOffset? AckedAt { get; init; }
        public string? AckedByName { get; init; }

        public AdminCall ToWire() => new(
            Id, PcId, PcName, PcNumber, UserId is { } userId ? new AdminCallUser(userId, UserName ?? "") : null, Category, Message, Source, ReceivedAt, Status,
            Repeat, AckedByName, AckedAt);
    }
}
