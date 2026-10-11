using System.Text.Json;
using ClubShell.Contracts.Pcs;
using ClubShell.Server.Agents;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>Thresholds of PC diagnostics (<c>AdminHealthSettings</c>), always inside their ranges.</summary>
public sealed record HealthSettings(int CpuHotC, int GpuHotC, int TrendC, int FpsDropPct, int OfflinePerDay, bool AutoMaintenance)
{
    /// <summary>The mock's defaults (<c>health.ts DEFAULT_HEALTH</c>).</summary>
    public static readonly HealthSettings Default = new(90, 85, 10, 30, 3, false);

    /// <summary><c>clubs.health_settings</c>, defaults for what is unset.</summary>
    public static async Task<HealthSettings> LoadAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid clubId)
    {
        var json = await c.ExecuteScalarAsync<string>("SELECT health_settings::text FROM clubs WHERE id = @clubId", new { clubId }, tx);
        using var doc = JsonDocument.Parse(json ?? "{}");
        return Patch(doc.RootElement, Default, clamp: false);
    }

    /// <summary>
    /// <c>AdminHealthSettingsPatch</c> over <paramref name="current"/>: numbers rounded half up and clamped to <c>x-clamp</c>,
    /// a boolean <c>autoMaintenance</c>, absent values kept.
    /// </summary>
    public static HealthSettings Patch(JsonElement patch, HealthSettings current, bool clamp = true)
    {
        long N(string name, long value, long min, long max) =>
            ClubSettingsEndpoints.Clamped(patch, name, value, clamp ? min : long.MinValue, clamp ? max : long.MaxValue);

        return new HealthSettings(
            (int)N("cpuHotC", current.CpuHotC, 50, 110), (int)N("gpuHotC", current.GpuHotC, 50, 110), (int)N("trendC", current.TrendC, 2, 40),
            (int)N("fpsDropPct", current.FpsDropPct, 5, 90), (int)N("offlinePerDay", current.OfflinePerDay, 1, 50),
            patch.TryGetProperty("autoMaintenance", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False ? on.GetBoolean() : current.AutoMaintenance);
    }
}

/// <summary><c>AdminHealthReading</c>: rounded, null when there is no data.</summary>
public sealed record HealthReading(int? Cpu, int? Gpu, int? Fps);

public sealed record HealthHourly(IReadOnlyList<int?> Cpu, IReadOnlyList<int?> Gpu, IReadOnlyList<int?> Fps);

/// <summary><c>AdminHealthIssue</c>; <c>params</c> are the numbers of the console's text.</summary>
public sealed record HealthIssue(string Kind, string Severity, IReadOnlyDictionary<string, double> Params);

/// <summary><c>AdminHealthTicket</c>.</summary>
public sealed record HealthTicket(
    Guid Id, Guid PcId, string PcName, string Kind, string Severity, JsonElement Params, string Status, DateTimeOffset OpenedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt, string Note, bool AutoMaintenance);

/// <summary>The diagnosis of one PC: <c>AdminPcHealth</c> without id/name/zone/status/ticket.</summary>
public sealed record PcDiagnosis(int Score, HealthReading Live, HealthReading Baseline, HealthHourly Hourly, IReadOnlyList<HealthIssue> Issues);

/// <summary>
/// PC diagnostics (port of the mock's <c>health.ts diagnose</c>, without its telemetry simulator, DESIGN §8): the samples of
/// <c>pc_metrics</c> from <c>POST /agents/{pcId}/telemetry</c> in UTC hourly buckets over the last 7 days. <c>live</c> —
/// the samples of the last 15 minutes, else the newest bucket (FPS only from live samples); <c>baseline</c> — the buckets
/// older than 24 h; "today" — the last 24 h. Issues: <c>cpuHot</c>/<c>gpuHot</c> — live temperature at the threshold
/// (high) or within 5 °C of it (medium), not for an offline PC; <c>cpuTrend</c>/<c>gpuTrend</c> — today above the baseline
/// by <c>trendC</c>; <c>fpsDrop</c> — today's FPS below the baseline by <c>fpsDropPct</c> %; <c>unstable</c> — at least
/// <c>offlinePerDay</c> <c>pcOffline</c> events in 24 h. Score = 100 − 35 per high − 15 per medium. FPS counts the samples
/// with a running game (<c>fps</c> &gt; 0); the mock's "busy half of the hour" has no counterpart in the agent's samples.
/// </summary>
public static class HealthDiagnosis
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>
    /// The averages of the samples of one group. A sample outside the range a sensor can give (a temperature outside 0–150 °C,
    /// FPS above 1000) is left out: the telemetry takes any double, and two samples of 1e308 overflow <c>avg</c>, which would
    /// fail the report and every <see cref="HealthWorker"/> pass for the whole club, not just for that PC.
    /// </summary>
    private const string Averages =
        """
        avg((data -> 'temps' ->> 'cpu')::float8) FILTER (WHERE (data -> 'temps' ->> 'cpu')::float8 BETWEEN 0 AND 150) AS cpu,
        avg((data -> 'temps' ->> 'gpu')::float8) FILTER (WHERE (data -> 'temps' ->> 'gpu')::float8 BETWEEN 0 AND 150) AS gpu,
        avg((data ->> 'fps')::float8) FILTER (WHERE (data ->> 'fps')::float8 > 0 AND (data ->> 'fps')::float8 <= 1000) AS fps
        """;

    public static async Task<IReadOnlyDictionary<Guid, PcDiagnosis>> DiagnoseAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, IReadOnlyList<(Guid Id, PcStatus Status)> pcs, HealthSettings settings, DateTimeOffset now)
    {
        var ids = pcs.Select(p => p.Id).ToArray();
        var currentHour = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % Hour.Ticks), TimeSpan.Zero);
        var weekStart = currentHour - TimeSpan.FromHours(167);
        var buckets = (await c.QueryAsync<Bucket>(
                $"""
                SELECT pc_id, date_bin('1 hour', at, timestamptz '2000-01-01 00:00:00+00') AS h, {Averages}
                FROM pc_metrics WHERE pc_id = ANY(@ids) AND at >= @weekStart AND at <= @now
                GROUP BY 1, 2
                """,
                new { ids, weekStart, now }, tx))
            .ToLookup(b => b.PcId);
        var live = (await c.QueryAsync<Bucket>(
                $"""
                SELECT pc_id, {Averages}
                FROM pc_metrics WHERE pc_id = ANY(@ids) AND at >= @from AND at <= @now
                GROUP BY 1
                """,
                new { ids, from = now - TimeSpan.FromMinutes(15), now }, tx))
            .ToDictionary(b => b.PcId);
        var drops = (await c.QueryAsync<(Guid PcId, int Count)>(
                "SELECT pc_id, count(*)::int FROM telemetry_events WHERE pc_id = ANY(@ids) AND kind = 'pcOffline' AND at >= @from GROUP BY pc_id",
                new { ids, from = now - TimeSpan.FromHours(24) }, tx))
            .ToDictionary(d => d.PcId, d => d.Count);

        var result = new Dictionary<Guid, PcDiagnosis>();
        foreach (var (id, status) in pcs)
        {
            var list = buckets[id].OrderBy(b => b.H).ToList();
            var dayStart = now - TimeSpan.FromHours(24);
            var recent = list.Where(b => b.H >= dayStart).ToList();
            var week = list.Where(b => b.H < dayStart).ToList();
            var last = list.LastOrDefault();
            live.TryGetValue(id, out var sample);
            var liveReading = new HealthReading(Round(sample?.Cpu ?? last?.Cpu), Round(sample?.Gpu ?? last?.Gpu), Round(sample?.Fps));
            var baseline = new HealthReading(Round(Avg(week.Select(b => b.Cpu))), Round(Avg(week.Select(b => b.Gpu))), Round(Avg(week.Select(b => b.Fps))));
            var day = (Cpu: Avg(recent.Select(b => b.Cpu)), Gpu: Avg(recent.Select(b => b.Gpu)), Fps: Avg(recent.Select(b => b.Fps)));

            var issues = new List<HealthIssue>();
            void Hot(string kind, int? value, int limit)
            {
                if (value is not { } temp || status == PcStatus.Offline)
                {
                    return;
                }

                if (temp >= limit - 5)
                {
                    issues.Add(new HealthIssue(kind, temp >= limit ? "high" : "medium", new Dictionary<string, double> { ["temp"] = temp, ["limit"] = limit }));
                }
            }

            void Trend(string kind, double? today, int? before)
            {
                if (today is { } t && before is { } b && Math.Floor(t - b + 0.5) >= settings.TrendC)
                {
                    issues.Add(new HealthIssue(kind, "medium", new Dictionary<string, double> { ["rise"] = Math.Floor(t - b + 0.5), ["today"] = Math.Floor(t + 0.5), ["before"] = b }));
                }
            }

            Hot("cpuHot", liveReading.Cpu, settings.CpuHotC);
            Hot("gpuHot", liveReading.Gpu, settings.GpuHotC);
            Trend("cpuTrend", day.Cpu, baseline.Cpu);
            Trend("gpuTrend", day.Gpu, baseline.Gpu);
            if (day.Fps is { } fps && baseline.Fps is > 0 and { } before)
            {
                var drop = Math.Floor(((1 - (fps / before)) * 100) + 0.5);
                if (drop >= settings.FpsDropPct)
                {
                    issues.Add(new HealthIssue("fpsDrop", "medium", new Dictionary<string, double> { ["drop"] = drop, ["today"] = Math.Floor(fps + 0.5), ["before"] = before }));
                }
            }

            if (drops.TryGetValue(id, out var count) && count >= settings.OfflinePerDay)
            {
                issues.Add(new HealthIssue("unstable", "medium", new Dictionary<string, double> { ["drops"] = count }));
            }

            var hourly = Enumerable.Range(0, 24).Select(i => list.FirstOrDefault(b => b.H == currentHour - TimeSpan.FromHours(23 - i))).ToList();
            var score = Math.Max(0, 100 - issues.Sum(i => i.Severity == "high" ? 35 : 15));
            result[id] = new PcDiagnosis(
                score, liveReading, baseline,
                new HealthHourly([.. hourly.Select(b => Round(b?.Cpu))], [.. hourly.Select(b => Round(b?.Gpu))], [.. hourly.Select(b => Round(b?.Fps))]),
                issues);
        }

        return result;
    }

    /// <summary>JS <c>Math.round</c>.</summary>
    private static int? Round(double? value) => value is { } v ? (int)Math.Floor(v + 0.5) : null;

    private static double? Avg(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private sealed class Bucket
    {
        public Guid PcId { get; init; }
        public DateTimeOffset H { get; init; }
        public double? Cpu { get; init; }
        public double? Gpu { get; init; }
        public double? Fps { get; init; }
    }
}

/// <summary>Rows of <c>health_tickets</c> as the wire <see cref="HealthTicket"/>.</summary>
public sealed class TicketRow
{
    public const string Columns = "id, pc_id, pc_name, kind, severity, params::text AS params, status, created_at, updated_at, resolved_at, note, put_maintenance";

    public Guid Id { get; init; }
    public Guid PcId { get; init; }
    public string PcName { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Params { get; init; } = "{}";
    public string Status { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string Note { get; init; } = "";
    public bool PutMaintenance { get; init; }

    public HealthTicket ToWire() =>
        new(Id, PcId, PcName, Kind, Severity, JsonElement.Parse(Params), Status, CreatedAt, UpdatedAt, ResolvedAt, Note, PutMaintenance);
}

/// <summary>
/// <c>AdminPcHealth</c>; <c>gamesVolume</c> (beyond the contract, D-73) as in <see cref="AdminHallPc"/>: the last
/// heartbeat's, null when the PC never reported one.
/// </summary>
public sealed record PcHealth(
    Guid Id, string Name, string Zone, PcStatus Status, int Score, HealthReading Live, HealthReading Baseline, HealthHourly Hourly,
    IReadOnlyList<HealthIssue> Issues, HealthTicket? Ticket, JsonElement? GamesVolume);

public sealed record HealthReport(HealthSettings Settings, IReadOnlyList<PcHealth> Pcs, IReadOnlyList<HealthTicket> Tickets);

public sealed record HealthSettingsResponse(HealthSettings Settings);

public sealed record TicketResponse(HealthTicket Ticket);

/// <summary>Body of <c>adminUpdateTicket</c>; <c>note</c> null or absent — unchanged.</summary>
public sealed record TicketUpdateRequest(string? Status, string? Note);
