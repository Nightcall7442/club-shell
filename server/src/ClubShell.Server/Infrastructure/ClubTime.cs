using System.Collections.Concurrent;

namespace ClubShell.Server.Infrastructure;

/// <summary>
/// Club-local calendar time (DESIGN §7.3, D-16): tariff windows, weekday percent, holidays, happy hours and the minors'
/// curfew are evaluated in <c>clubs.time_zone</c> (IANA); the wire is always UTC.
/// </summary>
public static class ClubTime
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Zones = new(StringComparer.Ordinal);

    public static DateTime Local(DateTimeOffset at, string timeZone) =>
        TimeZoneInfo.ConvertTime(at, Zones.GetOrAdd(timeZone, TimeZoneInfo.FindSystemTimeZoneById)).DateTime;

    /// <summary><c>HH:mm</c> as minutes since midnight; null when malformed.</summary>
    public static int? Minutes(string? hhmm) =>
        TimeOnly.TryParseExact(hhmm, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
            ? (t.Hour * 60) + t.Minute
            : null;
}
