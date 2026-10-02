using System.Globalization;
using System.Text.Json;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Sessions.Billing;

/// <summary>A row of <c>tariffs</c>; <see cref="ToWire"/> is the contract <c>Tariff</c>.</summary>
public sealed class TariffRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public long PricePerHour { get; init; }
    public int MinMinutes { get; init; }
    public int? MaxMinutes { get; init; }
    public string[] Zones { get; init; } = [];
    public string TimeWindows { get; init; } = "[]";
    public bool IsPackage { get; init; }
    public int? PackageMinutes { get; init; }
    public long? PackagePrice { get; init; }

    public const string Columns = "id, name, price_per_hour, min_minutes, max_minutes, zones, time_windows::text AS time_windows, is_package, package_minutes, package_price";

    public Tariff ToWire() => new(
        Id, Name, Money.Uzs(PricePerHour), MinMinutes, MaxMinutes, Zones,
        JsonDefaults.Deserialize<IReadOnlyList<TariffTimeWindow>>(TimeWindows) ?? [], IsPackage, PackageMinutes,
        PackagePrice is { } p ? Money.Uzs(p) : null);
}

/// <summary>A client group of the club (<c>AdminClientGroup</c>): its discount applies to every purchase.</summary>
public sealed class ClientGroup
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int DiscountPct { get; init; }
}

/// <summary><c>AdminHappyHour</c>: days 0 = Sunday, <c>from</c> ≥ <c>to</c> wraps midnight, empty zones = all.</summary>
public sealed class HappyHour
{
    public string Name { get; init; } = "";
    public int[] Days { get; init; } = [];
    public string From { get; init; } = "00:00";
    public string To { get; init; } = "00:00";
    public int DiscountPct { get; init; }
    public string[] Zones { get; init; } = [];
}

/// <summary><c>AdminLoyaltyLevel</c>: reached at <c>minSpent</c> tiyin of lifetime spend (DESIGN §5.13 item 5).</summary>
public sealed class LoyaltyLevel
{
    public int Level { get; init; }
    public string Name { get; init; } = "";
    public long MinSpent { get; init; }
    public int DiscountPct { get; init; }
}

/// <summary>
/// The pricing inputs of a club: its IANA zone and the <c>pricing</c>, <c>groups</c>, <c>happyHours</c>, <c>loyalty</c>
/// and <c>limits</c> keys of <c>clubs.settings</c> (<c>AdminClubSettings</c>, written by <c>PATCH /admin/club</c> in S5).
/// A missing key means no rule: every day 100 %, no discounts, minors under 18 off the PCs from 22:00 to 06:00.
/// </summary>
public sealed class ClubPricing
{
    public string TimeZone { get; init; } = "Asia/Tashkent";
    public int[] WeekdayPct { get; init; } = [100, 100, 100, 100, 100, 100, 100];
    public string[] Holidays { get; init; } = [];
    public int HolidayPct { get; init; } = 100;
    public ClientGroup[] Groups { get; init; } = [];
    public HappyHour[] HappyHours { get; init; } = [];

    /// <summary>Sorted by <c>minSpent</c>.</summary>
    public LoyaltyLevel[] Loyalty { get; init; } = [];

    public int MinorAge { get; init; } = 18;
    public string MinorCurfew { get; init; } = "22:00";

    /// <summary>
    /// <c>limits.guestPostpaid</c> (beyond the contract, owner's choice in the console): a guest may play postpaid and pay at
    /// the counter afterwards. Off by default — a guest who walks away leaves an unpaid bill.
    /// </summary>
    public bool GuestPostpaid { get; init; }

    /// <summary><c>limits.guestDebtLimit</c> (tiyin): how far a guest's postpaid bill may run; null or 0 — no limit.</summary>
    public long? GuestDebtLimit { get; init; }

    /// <summary>
    /// <c>limits.autoExtendMinutes</c> (beyond the contract): when a prepaid session runs out, the tick extends it by this
    /// many minutes from the player's balance, as the cashier's extend does, for as long as the balance pays; 0 — off.
    /// </summary>
    public int AutoExtendMinutes { get; init; }

    public static ClubPricing Parse(string timeZone, string? settingsJson)
    {
        var doc = string.IsNullOrEmpty(settingsJson) ? null : JsonSerializer.Deserialize<SettingsDoc>(settingsJson, JsonSerializerOptions.Web);
        return new ClubPricing
        {
            TimeZone = timeZone,
            WeekdayPct = doc?.Pricing?.WeekdayPct is { Length: 7 } week ? week : [100, 100, 100, 100, 100, 100, 100],
            Holidays = doc?.Pricing?.Holidays ?? [],
            HolidayPct = doc?.Pricing?.HolidayPct ?? 100,
            Groups = doc?.Groups ?? [],
            HappyHours = doc?.HappyHours ?? [],
            Loyalty = (doc?.Loyalty ?? []).OrderBy(l => l.MinSpent).ToArray(),
            MinorAge = doc?.Limits?.MinorAge ?? 18,
            MinorCurfew = doc?.Limits?.MinorCurfew ?? "22:00",
            GuestPostpaid = doc?.Limits?.GuestPostpaid ?? false,
            GuestDebtLimit = doc?.Limits?.GuestDebtLimit is > 0 and var limit ? limit : null,
            AutoExtendMinutes = Math.Clamp(doc?.Limits?.AutoExtendMinutes ?? 0, 0, 720),
        };
    }

    /// <summary>The highest level whose <c>minSpent</c> is reached; null when the club has none.</summary>
    public LoyaltyLevel? LevelOf(long lifetimeSpent) => Loyalty.LastOrDefault(l => l.MinSpent <= lifetimeSpent);

    /// <summary>
    /// A minor (<c>birthYear</c> set, younger than <c>minorAge</c> this local year) in the curfew: from
    /// <c>minorCurfew</c> until 06:00 local time (DESIGN §5.2 rule 5; mock <c>club.ts inCurfew</c>).
    /// </summary>
    public bool InCurfew(int? birthYear, DateTimeOffset at)
    {
        var local = ClubTime.Local(at, TimeZone);
        if (birthYear is not { } year || local.Year - year >= MinorAge)
        {
            return false;
        }

        var minutes = (local.Hour * 60) + local.Minute;
        return minutes >= (ClubTime.Minutes(MinorCurfew) ?? 22 * 60) || minutes < 6 * 60;
    }

    private sealed class SettingsDoc
    {
        public PricingDoc? Pricing { get; init; }
        public ClientGroup[]? Groups { get; init; }
        public HappyHour[]? HappyHours { get; init; }
        public LoyaltyLevel[]? Loyalty { get; init; }
        public LimitsDoc? Limits { get; init; }
    }

    private sealed class PricingDoc
    {
        public int[]? WeekdayPct { get; init; }
        public string[]? Holidays { get; init; }
        public int? HolidayPct { get; init; }
    }

    private sealed class LimitsDoc
    {
        public int? MinorAge { get; init; }
        public string? MinorCurfew { get; init; }
        public bool? GuestPostpaid { get; init; }
        public long? GuestDebtLimit { get; init; }
        public int? AutoExtendMinutes { get; init; }
    }
}

/// <summary>The price of a purchase and how it was reached; stored as <c>meta</c> of the charge (<c>package</c>: never refunded, §5.7).</summary>
public sealed record Quote(int Minutes, long Base, int DayPct, int DiscountPct, string? DiscountReason, long Total, bool Package);

/// <summary>
/// The single price function of every channel (DESIGN §5.1, D-8): kiosk create/extend, cashier open/extend, quote,
/// offline replay, postpaid settlement.
/// <code>
/// base   = package ? package_price : ceil(price_per_hour × minutes / 60)
/// dayPct = local date in holidays ? holidayPct : weekdayPct[local day, 0 = Sunday]
/// disc   = max(client group, loyalty level, happy hour of the PC zone now) — never summed
/// total  = (base × dayPct × (100 − disc) + 500 000) / 1 000 000 × 100      half-up to a whole sum
/// </code>
/// </summary>
public static class Pricing
{
    /// <summary>Hourly base: ceil to one tiyin (= <c>Tariff.PriceFor</c> of the contract).</summary>
    public static long Base(long pricePerHour, int minutes) => ((pricePerHour * minutes) + 59) / 60;

    /// <summary>Applies the day percent and the discount, half-up to 100 tiyin.</summary>
    public static long Total(long @base, int dayPct, int discountPct) =>
        Math.Max(0, (((@base * dayPct * (100 - discountPct)) + 500_000) / 1_000_000) * 100);

    /// <summary>A postpaid price frozen at the start: <c>quote_frozen(ceil(used / 60))</c> (§5.4, §5.7).</summary>
    public static long Frozen(long pricePerHour, int usedSec, int dayPct, int discountPct) =>
        Total(Base(pricePerHour, (usedSec + 59) / 60), dayPct, discountPct);

    /// <param name="groupId">The buyer's group in this club; null for a walk-in quote.</param>
    /// <param name="lifetimeSpent">The buyer's lifetime spend (loyalty level); null for a walk-in quote.</param>
    public static Quote Compute(TariffRow tariff, int minutes, string? groupId, long? lifetimeSpent, string zone, DateTimeOffset at, ClubPricing club)
    {
        var local = ClubTime.Local(at, club.TimeZone);
        var dayPct = club.Holidays.Contains(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            ? club.HolidayPct
            : club.WeekdayPct[(int)local.DayOfWeek];

        (int Pct, string Name)? best = null;
        void Offer(int pct, string name)
        {
            if (pct > 0 && (best is null || pct > best.Value.Pct))
            {
                best = (pct, name);
            }
        }

        if (groupId is not null && club.Groups.FirstOrDefault(g => g.Id == groupId) is { } group)
        {
            Offer(group.DiscountPct, group.Name);
        }

        if (lifetimeSpent is { } spent && club.LevelOf(spent) is { } level)
        {
            Offer(level.DiscountPct, level.Name);
        }

        var minute = (local.Hour * 60) + local.Minute;
        foreach (var hh in club.HappyHours)
        {
            var (from, to) = (ClubTime.Minutes(hh.From) ?? 0, ClubTime.Minutes(hh.To) ?? 0);
            if (hh.Days.Contains((int)local.DayOfWeek)
                && (hh.Zones.Length == 0 || hh.Zones.Contains(zone, StringComparer.OrdinalIgnoreCase))
                && (from <= to ? minute >= from && minute < to : minute >= from || minute < to))
            {
                Offer(hh.DiscountPct, hh.Name);
            }
        }

        var @base = tariff.IsPackage ? tariff.PackagePrice ?? 0 : Base(tariff.PricePerHour, minutes);
        var discount = best?.Pct ?? 0;
        return new Quote(minutes, @base, dayPct, discount, best?.Name, Total(@base, dayPct, discount), tariff.IsPackage);
    }
}
