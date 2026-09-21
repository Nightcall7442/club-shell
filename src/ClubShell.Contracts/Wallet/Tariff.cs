using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;

namespace ClubShell.Contracts.Wallet;

/// <summary>Day of week as used by tariff time windows. Wire values <c>mon</c>..<c>sun</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Weekday>))]
public enum Weekday
{
    /// <summary>Monday.</summary>
    Mon,

    /// <summary>Tuesday.</summary>
    Tue,

    /// <summary>Wednesday.</summary>
    Wed,

    /// <summary>Thursday.</summary>
    Thu,

    /// <summary>Friday.</summary>
    Fri,

    /// <summary>Saturday.</summary>
    Sat,

    /// <summary>Sunday.</summary>
    Sun,
}

/// <summary>Conversions between <see cref="Weekday"/> and <see cref="DayOfWeek"/>.</summary>
public static class WeekdayExtensions
{
    /// <summary>Maps to the BCL <see cref="DayOfWeek"/>.</summary>
    public static DayOfWeek ToDayOfWeek(this Weekday day) => day switch
    {
        Weekday.Mon => DayOfWeek.Monday,
        Weekday.Tue => DayOfWeek.Tuesday,
        Weekday.Wed => DayOfWeek.Wednesday,
        Weekday.Thu => DayOfWeek.Thursday,
        Weekday.Fri => DayOfWeek.Friday,
        Weekday.Sat => DayOfWeek.Saturday,
        Weekday.Sun => DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, null),
    };

    /// <summary>Maps from the BCL <see cref="DayOfWeek"/>.</summary>
    public static Weekday ToWeekday(this DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Weekday.Mon,
        DayOfWeek.Tuesday => Weekday.Tue,
        DayOfWeek.Wednesday => Weekday.Wed,
        DayOfWeek.Thursday => Weekday.Thu,
        DayOfWeek.Friday => Weekday.Fri,
        DayOfWeek.Saturday => Weekday.Sat,
        DayOfWeek.Sunday => Weekday.Sun,
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, null),
    };
}

/// <summary>Weekly time window during which a tariff is valid (club local time). <paramref name="To"/> &lt; <paramref name="From"/> wraps midnight.</summary>
/// <param name="Days">Days the window applies to.</param>
/// <param name="From">Start time, inclusive (<c>HH:mm</c>).</param>
/// <param name="To">End time, exclusive (<c>HH:mm</c>).</param>
public sealed record TariffTimeWindow(
    IReadOnlyList<Weekday> Days,
    TimeOnly From,
    TimeOnly To)
{
    /// <summary><see langword="true"/> when <paramref name="localTime"/> on <paramref name="day"/> falls inside the window (handles midnight wrap).</summary>
    public bool Contains(Weekday day, TimeOnly localTime)
    {
        if (From == To)
        {
            return Days.Contains(day);
        }

        if (From < To)
        {
            return Days.Contains(day) && localTime >= From && localTime < To;
        }

        // Wraps midnight: [From, 24:00) on `day` or [00:00, To) on the following day.
        if (localTime >= From)
        {
            return Days.Contains(day);
        }

        var previous = (Weekday)(((int)day + 6) % 7);
        return localTime < To && Days.Contains(previous);
    }
}

/// <summary>Billing tariff (IPC_PROTOCOL.md §6.11).</summary>
/// <param name="Id">Tariff id.</param>
/// <param name="Name">Display name.</param>
/// <param name="PricePerHour">Hourly price.</param>
/// <param name="MinMinutes">Minimum purchasable minutes.</param>
/// <param name="MaxMinutes">Maximum purchasable minutes; <see langword="null"/> = unlimited.</param>
/// <param name="Zones">Zones the tariff applies to; empty = all.</param>
/// <param name="TimeWindows">Validity windows; empty = always.</param>
/// <param name="IsPackage">Fixed package (<paramref name="PackageMinutes"/> for <paramref name="PackagePrice"/>) instead of hourly billing.</param>
/// <param name="PackageMinutes">Minutes included in the package; required when <paramref name="IsPackage"/>.</param>
/// <param name="PackagePrice">Package price; required when <paramref name="IsPackage"/>.</param>
public sealed record Tariff(
    Guid Id,
    string Name,
    Money PricePerHour,
    int MinMinutes,
    int? MaxMinutes,
    IReadOnlyList<string> Zones,
    IReadOnlyList<TariffTimeWindow> TimeWindows,
    bool IsPackage,
    int? PackageMinutes = null,
    Money? PackagePrice = null)
{
    /// <summary>Price for <paramref name="minutes"/> under this tariff (package price when <see cref="IsPackage"/>; otherwise pro-rata per minute, rounded up to the minor unit).</summary>
    public Money PriceFor(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minutes);
        if (IsPackage && PackagePrice is { } package)
        {
            return package;
        }

        var perMinute = PricePerHour.Amount;
        var total = checked(perMinute * minutes);
        var rounded = total / 60 + (total % 60 == 0 ? 0 : 1);
        return PricePerHour with { Amount = rounded };
    }

    /// <summary><see langword="true"/> when the tariff is valid for <paramref name="zone"/> at the given local day/time.</summary>
    public bool IsValidFor(string zone, Weekday day, TimeOnly localTime)
    {
        if (Zones.Count > 0 && !Zones.Contains(zone, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TimeWindows.Count == 0)
        {
            return true;
        }

        foreach (var window in TimeWindows)
        {
            if (window.Contains(day, localTime))
            {
                return true;
            }
        }

        return false;
    }
}
