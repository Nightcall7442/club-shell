using System.Globalization;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace ClubShell.Server.Admin;

public sealed record ReportsTotals(long Sessions, long Shop, long TopUps, int SessionsCount);

public sealed record ReportsDay(string Date, long Sessions, long Shop, long TopUps);

public sealed record TopGame(Guid Id, string Title, int Players);

public sealed record TopProduct(string Title, int Qty, long Amount);

public sealed record Reports(
    int Days, ReportsTotals Totals, IReadOnlyList<ReportsDay> ByDay, int[][] Heat, IReadOnlyList<TopGame> TopGames, IReadOnlyList<TopProduct> TopProducts,
    IReadOnlyList<AdminShift> Shifts);

/// <summary>
/// The owner's reports (slice S5, <c>adminReports</c>), all in the club's local calendar (D-16): the period is the last
/// <c>days</c> local days up to today, from local midnight (<c>days</c> not a number → 7, then into 1–90). <c>byDay</c> — one
/// row per local date from the club's ledger: <c>sessions</c> = charges − refunds, <c>shop</c> = purchases (bar sales from the
/// balance net of their voids) plus the bar's method sales net of theirs (D-57), <c>topUps</c>; <c>totals</c> sums them,
/// <c>sessionsCount</c> — sessions started in the period. <c>heat</c> — seat-hours by local weekday (0 = Sunday) and hour of
/// the sessions started in the period (the mock's stepping by whole hours from the start). <c>topGames</c> — distinct
/// players of successful launches in the period (<c>launch_reports</c>, up to 8); <c>topProducts</c> — the bar's products of
/// the period by revenue, voided sales left out (up to 8; kiosk orders are notImplemented); <c>shifts</c> — opened in the
/// period, newest first.
/// </summary>
public static class ReportsEndpoints
{
    public static readonly string[] Operations = ["adminReports"];

    public static void MapReportsEndpoints(this IEndpointRouteBuilder app) =>
        app.MapApiGroup("/api/v1/admin/reports").MapGet("", ReportAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));

    private static async Task<IResult> ReportAsync(HttpContext context, string? days, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var period = ControlEndpoints.Days(days);
        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        var timeZone = await c.ExecuteScalarAsync<string>("SELECT time_zone FROM clubs WHERE id = @ClubId", new { staff.ClubId }) ?? "Asia/Tashkent";
        var today = DateOnly.FromDateTime(ClubTime.Local(now, timeZone));
        var firstDay = today.AddDays(1 - period);
        var start = ClubTime.Utc(firstDay.ToDateTime(TimeOnly.MinValue), timeZone);

        var money = (await c.QueryAsync<(DateTime Day, long Sessions, long Shop, long TopUps)>(
                """
                SELECT (created_at AT TIME ZONE @timeZone)::date AS day,
                       (coalesce(-sum(amount) FILTER (WHERE type = 'charge'), 0) - coalesce(sum(amount) FILTER (WHERE type = 'refund'), 0))::bigint,
                       coalesce(-sum(amount) FILTER (WHERE type = 'purchase'), 0)::bigint,
                       coalesce(sum(amount) FILTER (WHERE type = 'topUp'), 0)::bigint
                FROM ledger_entries WHERE club_id = @ClubId AND created_at >= @start AND created_at <= @now
                GROUP BY 1
                """,
                new { staff.ClubId, timeZone, start, now }))
            .ToDictionary(m => DateOnly.FromDateTime(m.Day));

        // The bar's money taken by a method (outside the ledger, D-52), net of voids, by the local day each row was made.
        var bar = (await c.QueryAsync<(DateTime Day, long Net)>(
                """
                SELECT (created_at AT TIME ZONE @timeZone)::date AS day, sum(CASE WHEN kind = 'sale' THEN total ELSE -total END)::bigint
                FROM shop_sales WHERE club_id = @ClubId AND method <> 'balance' AND created_at >= @start AND created_at <= @now
                GROUP BY 1
                """,
                new { staff.ClubId, timeZone, start, now }))
            .ToDictionary(m => DateOnly.FromDateTime(m.Day), m => m.Net);
        var byDay = Enumerable.Range(0, period).Select(i =>
        {
            var date = firstDay.AddDays(i);
            var m = money.GetValueOrDefault(date);
            return new ReportsDay(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), m.Sessions, m.Shop + bar.GetValueOrDefault(date), m.TopUps);
        }).ToList();

        var sessions = (await c.QueryAsync<(DateTimeOffset StartedAt, DateTimeOffset? EndedAt)>(
            "SELECT started_at, ended_at FROM sessions WHERE club_id = @ClubId AND started_at >= @start AND started_at <= @now", new { staff.ClubId, start, now })).ToList();
        var heat = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
        foreach (var (startedAt, endedAt) in sessions)
        {
            var end = endedAt ?? now;
            for (var t = startedAt; t < end; t += TimeSpan.FromHours(1))
            {
                var local = ClubTime.Local(t, timeZone);
                heat[(int)local.DayOfWeek][local.Hour]++;
            }
        }

        var topGames = (await c.QueryAsync<TopGame>(
            """
            SELECT g.id, g.title, count(DISTINCT r.user_id)::int AS players
            FROM launch_reports r JOIN games g ON g.id = r.game_id
            WHERE r.club_id = @ClubId AND r.phase = 'launch' AND r.user_id IS NOT NULL AND (r.data -> 'result' ->> 'ok')::boolean
              AND r.started_at >= @start AND r.started_at <= @now
            GROUP BY g.id, g.title
            ORDER BY players DESC, g.title, g.id
            LIMIT 8
            """,
            new { staff.ClubId, start, now })).ToList();

        // Bar sales of the period that were not voided, by product (its latest title), up to 8 by revenue (D-57).
        var topProducts = (await c.QueryAsync<TopProduct>(
            """
            SELECT (array_agg(l.title ORDER BY s.created_at DESC))[1] AS title, sum(l.qty)::int AS qty, sum(l.qty * l.price)::bigint AS amount
            FROM shop_sale_lines l JOIN shop_sales s ON s.id = l.sale_id
            WHERE s.club_id = @ClubId AND s.kind = 'sale' AND s.created_at >= @start AND s.created_at <= @now
              AND NOT EXISTS (SELECT 1 FROM shop_sales v WHERE v.void_of = s.id)
            GROUP BY l.product_id
            ORDER BY amount DESC, title
            LIMIT 8
            """,
            new { staff.ClubId, start, now })).ToList();

        var totals = new ReportsTotals(byDay.Sum(d => d.Sessions), byDay.Sum(d => d.Shop), byDay.Sum(d => d.TopUps), sessions.Count);
        return AdminJson.Ok(new Reports(period, totals, byDay, heat, topGames, topProducts, await ShiftEndpoints.OpenedSinceAsync(c, staff.ClubId, start)));
    }
}
