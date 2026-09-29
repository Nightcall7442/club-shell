using System.Globalization;
using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace ClubShell.Server.Admin;

public sealed record AuditEntry(
    Guid Id, DateTimeOffset At, string StaffId, string StaffName, Guid? ShiftId, string Action, Guid? UserId, Guid? PcId, long Amount, string Detail,
    IReadOnlyDictionary<string, JsonElement> Meta);

public sealed record ControlFlag(
    string Id, string Kind, string Severity, DateTimeOffset At, string StaffId, string StaffName, Guid? ShiftId, Guid? UserId, Guid? PcId, long Amount,
    IReadOnlyDictionary<string, object> Params);

public sealed record FlagCounts(int High, int Medium, int Low);

public sealed record StaffSummary(
    string StaffId, string StaffName, int Operations, long TopUps, long Refunds, int EarlyEnds, int Discounts, long Shortfall, FlagCounts Flags);

public sealed record ControlReport(
    DateTimeOffset From, DateTimeOffset To, ControlSettings Settings, IReadOnlyList<StaffSummary> Staff, IReadOnlyList<ControlFlag> Flags,
    IReadOnlyList<AuditEntry> Log);

/// <summary>
/// Cashier control (slice S5, port of the mock's <c>control.ts</c>): <c>adminControl</c> reads the staff journal of the
/// last <c>days</c> days (1–90, not a number → 7) and applies the seven rules — <c>shortfall</c> (cash short at close over
/// <c>shortfallFrom</c>, high), <c>earlyEnds</c> (that many early refunds by one cashier in one shift, high), <c>earlyEnd</c>
/// (a session ended with a refund within <c>earlyEndMinutes</c>, low), <c>discount</c> (a client moved into a group with at
/// least <c>discountPct</c>, medium), <c>sameClient</c> (that many top-ups of one client by one cashier in one shift,
/// medium), <c>noShift</c> (money with no shift open, medium), <c>bigCash</c> (a cash top-up of at least
/// <c>bigTopupAt</c>, low). <c>noShift</c> also counts the ledger rows a cashier posted that got no shift (<c>shift_id IS NULL</c>),
/// which is what a row racing the shift close gets (§4.3), when the journal entry of the operation does not already show
/// it. Only the contract's journal actions are read (the owner's configuration entries are left out). <c>staffId</c>
/// narrows the flags and the journal, never the per-cashier totals.
/// </summary>
public static class ControlEndpoints
{
    public static readonly string[] Operations = ["adminControl"];

    /// <summary><c>AdminAuditAction</c>.</summary>
    public static readonly string[] Actions =
    [
        "shiftOpen", "shiftClose", "topUp", "sessionOpen", "sessionExtend", "sessionEnd", "promoRedeem", "clientGroup", "blacklist", "stockReceive",
        "stockEdit", "pcCommand", "clientPassword", "clientCard",
    ];

    private static readonly string[] MoneyActions = ["topUp", "sessionOpen", "sessionExtend", "sessionEnd", "promoRedeem"];

    public static void MapControlEndpoints(this IEndpointRouteBuilder app) =>
        app.MapApiGroup("/api/v1/admin/control").MapGet("", ReportAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));

    /// <summary><c>days</c> of the owner's reports: not a number → 7, then into 1–90.</summary>
    public static int Days(string? days) =>
        int.TryParse(days, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, 90)
        : long.TryParse(days, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big) ? (big < 0 ? 1 : 90)
        : 7;

    private static async Task<IResult> ReportAsync(HttpContext context, string? days, string? staffId, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var to = clock.GetUtcNow();
        var from = to - TimeSpan.FromDays(Days(days));
        await using var c = await db.OpenConnectionAsync();
        var settings = await ClubSettingsEndpoints.ControlAsync(c, null, staff.ClubId);
        var bigTopupAt = await ClubSettingsEndpoints.BigTopupAtAsync(c, null, staff.ClubId);
        var entries = (await c.QueryAsync<EntryRow>(
                $"""
                SELECT {EntryRow.Columns} FROM audit_entries
                WHERE club_id = @ClubId AND at >= @from AND at <= @to AND action = ANY(@Actions)
                ORDER BY at DESC, id DESC
                """,
                new { staff.ClubId, from, to, Actions }))
            .Select(e => e.ToWire()).ToList();

        // Operations of the cashier (or the API key) whose ledger rows got no shift.
        var shiftless = (await c.QueryAsync<LedgerOp>(
                """
                SELECT l.op_id, min(l.created_at) AS at, l.staff_id, s.name AS staff_name, min(l.user_id::text)::uuid AS user_id, min(l.pc_id::text)::uuid AS pc_id,
                       (array_agg(l.type ORDER BY l.created_at, l.id))[1] AS type, sum(abs(l.amount))::bigint AS amount,
                       (array_agg(l.description ORDER BY l.created_at, l.id))[1] AS description
                FROM ledger_entries l LEFT JOIN staff s ON s.id = l.staff_id
                WHERE l.club_id = @ClubId AND l.shift_id IS NULL AND l.created_at >= @from AND l.created_at <= @to
                  AND l.type IN ('topUp', 'charge', 'refund', 'bonus')
                  AND l.staff_id IS NOT NULL
                GROUP BY l.op_id, l.staff_id, s.name
                """,
                new { staff.ClubId, from, to }))
            .ToList();

        var flags = Flags(entries, shiftless, settings, bigTopupAt);
        var report = new ControlReport(
            from, to, settings, Summaries(entries, flags, settings),
            staffId is null ? flags : [.. flags.Where(f => f.StaffId == staffId)],
            [.. (staffId is null ? entries : entries.Where(e => e.StaffId == staffId)).Take(300)]);
        return AdminJson.Ok(report);
    }

    private static bool IsEarlyEnd(AuditEntry e, ControlSettings settings) =>
        e.Action == "sessionEnd" && e.Amount > 0 && Number(e, "sessionMinutes") is { } minutes && minutes < settings.EarlyEndMinutes;

    private static double? Number(AuditEntry e, string key) =>
        e.Meta.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Text(AuditEntry e, string key) =>
        e.Meta.TryGetValue(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ValueKind is JsonValueKind.Null ? "" : v.GetRawText() : "";

    /// <summary>Most serious first, then newest (the mock's order).</summary>
    private static List<ControlFlag> Flags(List<AuditEntry> entries, List<LedgerOp> shiftless, ControlSettings settings, long bigTopupAt)
    {
        var flags = new List<ControlFlag>();
        ControlFlag Flag(AuditEntry e, string kind, string severity, Dictionary<string, object> parameters, long? amount = null) =>
            new($"{kind}:{e.Id}", kind, severity, e.At, e.StaffId, e.StaffName, e.ShiftId, e.UserId, e.PcId, amount ?? e.Amount, parameters);

        var earlyByShift = new Dictionary<string, List<AuditEntry>>();
        var topUpsByClient = new Dictionary<string, List<AuditEntry>>();
        var noShiftUsers = new List<(Guid? UserId, DateTimeOffset At)>();
        foreach (var e in entries)
        {
            if (e.Action == "shiftClose" && Number(e, "diff") is { } diff && diff < -settings.ShortfallFrom)
            {
                flags.Add(Flag(e, "shortfall", "high", new() { ["short"] = (long)-diff }, (long)-diff));
            }

            if (MoneyActions.Contains(e.Action) && e.ShiftId is null && (e.Amount > 0 || e.Action != "sessionEnd"))
            {
                flags.Add(Flag(e, "noShift", "medium", new() { ["action"] = e.Action, ["detail"] = e.Detail }));
                noShiftUsers.Add((e.UserId, e.At));
            }

            if (IsEarlyEnd(e, settings))
            {
                flags.Add(Flag(e, "earlyEnd", "low", new() { ["minutes"] = Number(e, "sessionMinutes")!.Value, ["detail"] = e.Detail }));
                var key = $"{e.StaffId}|{e.ShiftId}";
                (earlyByShift.TryGetValue(key, out var list) ? list : earlyByShift[key] = []).Add(e);
            }

            if (e.Action == "clientGroup" && Number(e, "discountPct") is { } pct && pct >= settings.DiscountPct)
            {
                flags.Add(Flag(e, "discount", "medium", new() { ["pct"] = pct, ["group"] = Text(e, "groupName"), ["detail"] = Text(e, "clientName") }));
            }

            if (e.Action == "topUp")
            {
                if (Text(e, "method") == "cash" && e.Amount >= bigTopupAt)
                {
                    flags.Add(Flag(e, "bigCash", "low", new() { ["detail"] = e.Detail }));
                }

                if (e.UserId is { } userId)
                {
                    var key = $"{e.StaffId}|{e.ShiftId}|{userId}";
                    (topUpsByClient.TryGetValue(key, out var list) ? list : topUpsByClient[key] = []).Add(e);
                }
            }
        }

        foreach (var op in shiftless)
        {
            // The same operation already flagged from its journal entry (same client, same moment).
            if (noShiftUsers.Any(n => n.UserId == op.UserId && (n.At - op.At).Duration() <= TimeSpan.FromSeconds(2)))
            {
                continue;
            }

            var action = op.Type switch { "topUp" => "topUp", "charge" => "sessionOpen", "refund" => "sessionEnd", _ => "promoRedeem" };
            flags.Add(new ControlFlag(
                $"noShift:{op.OpId}", "noShift", "medium", op.At, op.StaffId?.ToString() ?? "apiKey", op.StaffName ?? Auth.StaffTokens.ApiKeyName, null,
                op.UserId, op.PcId, op.Amount, new Dictionary<string, object> { ["action"] = action, ["detail"] = op.Description }));
        }

        foreach (var list in earlyByShift.Values.Where(l => l.Count >= settings.EarlyEndsPerShift))
        {
            flags.Add(Flag(list[0], "earlyEnds", "high", new() { ["count"] = list.Count }, list.Sum(e => e.Amount)));
        }

        foreach (var list in topUpsByClient.Values.Where(l => l.Count >= settings.SameClientTopups))
        {
            flags.Add(Flag(list[0], "sameClient", "medium", new() { ["count"] = list.Count, ["detail"] = list[0].Detail }, list.Sum(e => e.Amount)));
        }

        return [.. flags.OrderBy(f => Rank(f.Severity)).ThenByDescending(f => f.At)];
    }

    private static int Rank(string severity) => severity switch { "high" => 0, "medium" => 1, _ => 2 };

    private static List<StaffSummary> Summaries(List<AuditEntry> entries, List<ControlFlag> flags, ControlSettings settings)
    {
        var order = new List<string>();
        var names = new Dictionary<string, string>();
        void Seen(string id, string name)
        {
            if (names.TryAdd(id, name))
            {
                order.Add(id);
            }
        }

        foreach (var e in entries)
        {
            Seen(e.StaffId, e.StaffName);
        }

        foreach (var f in flags)
        {
            Seen(f.StaffId, f.StaffName);
        }

        return [.. order.Select(id =>
            {
                var mine = entries.Where(e => e.StaffId == id).ToList();
                var own = flags.Where(f => f.StaffId == id).ToList();
                return new StaffSummary(
                    id, names[id], mine.Count,
                    mine.Where(e => e.Action == "topUp").Sum(e => e.Amount),
                    mine.Where(e => e.Action == "sessionEnd").Sum(e => e.Amount),
                    mine.Count(e => IsEarlyEnd(e, settings)),
                    mine.Count(e => e.Action == "clientGroup" && Number(e, "discountPct") >= settings.DiscountPct),
                    mine.Where(e => e.Action == "shiftClose").Sum(e => Math.Max(0, -(long)(Number(e, "diff") ?? 0))),
                    new FlagCounts(own.Count(f => f.Severity == "high"), own.Count(f => f.Severity == "medium"), own.Count(f => f.Severity == "low")));
            })
            .OrderByDescending(s => s.Flags.High).ThenByDescending(s => s.Flags.Medium).ThenByDescending(s => s.Operations)];
    }

    private sealed class EntryRow
    {
        public const string Columns = "id, at, staff_id, staff_name, shift_id, action, user_id, pc_id, amount, detail, meta::text AS meta";

        public Guid Id { get; init; }
        public DateTimeOffset At { get; init; }
        public Guid? StaffId { get; init; }
        public string StaffName { get; init; } = "";
        public Guid? ShiftId { get; init; }
        public string Action { get; init; } = "";
        public Guid? UserId { get; init; }
        public Guid? PcId { get; init; }
        public long Amount { get; init; }
        public string Detail { get; init; } = "";
        public string Meta { get; init; } = "{}";

        /// <summary>Only scalar facts go out (<c>AdminAuditEntry.meta</c>: string, number, boolean or null).</summary>
        public AuditEntry ToWire()
        {
            using var doc = JsonDocument.Parse(Meta);
            var meta = doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            return new AuditEntry(Id, At, StaffId?.ToString() ?? "apiKey", StaffName, ShiftId, Action, UserId, PcId, Amount, Detail, meta);
        }
    }

    private sealed class LedgerOp
    {
        public Guid OpId { get; init; }
        public DateTimeOffset At { get; init; }
        public Guid? StaffId { get; init; }
        public string? StaffName { get; init; }
        public Guid? UserId { get; init; }
        public Guid? PcId { get; init; }
        public string Type { get; init; } = "";
        public long Amount { get; init; }
        public string Description { get; init; } = "";
    }
}

/// <summary>
/// The live side of cashier control (mock <c>control.ts alertOn</c>): the <c>suspicious</c> event for the patterns that
/// should not wait for the owner to open the page — a cash shortfall at close, and the early refund that makes a cashier's
/// shift reach <c>earlyEndsPerShift</c> (exactly then, so one bad shift sends one event). Written in the action's own
/// transaction, after its journal entry.
/// </summary>
public static class ControlAlerts
{
    public static async Task ShiftClosedAsync(NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, string cashier, long diff, DateTimeOffset now)
    {
        var settings = await ClubSettingsEndpoints.ControlAsync(c, tx, staff.ClubId);
        if (diff < -settings.ShortfallFrom)
        {
            await Webhooks.EnqueueAsync(c, tx, staff.ClubId, "suspicious", now, $"{cashier}: недостача в кассе {Webhooks.Sum(diff)} при закрытии смены", new { staffId = staff.WireId });
        }
    }

    public static async Task SessionEndedAsync(NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, long refunded, int sessionMinutes, DateTimeOffset now)
    {
        var settings = await ClubSettingsEndpoints.ControlAsync(c, tx, staff.ClubId);
        if (refunded <= 0 || sessionMinutes >= settings.EarlyEndMinutes)
        {
            return;
        }

        var count = await c.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM audit_entries
            WHERE club_id = @ClubId AND action = 'sessionEnd' AND amount > 0 AND staff_id IS NOT DISTINCT FROM @StaffId
              AND shift_id IS NOT DISTINCT FROM (SELECT id FROM shifts WHERE club_id = @ClubId AND closed_at IS NULL)
              AND (meta ->> 'sessionMinutes')::int < @limit
              AND (shift_id IS NOT NULL OR at > @now - interval '1 day')
            """,
            new { staff.ClubId, staff.StaffId, limit = settings.EarlyEndMinutes, now }, tx);
        if (count == settings.EarlyEndsPerShift)
        {
            await Webhooks.EnqueueAsync(c, tx, staff.ClubId, "suspicious", now,
                $"{staff.Name}: {count} сеанса закрыты с возвратом в первые {settings.EarlyEndMinutes} мин", new { staffId = staff.WireId });
        }
    }
}
