using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// A missing key means no rule: every day 100 %, no discounts, minors under 18 off the PCs from 22:00 to 06:00 (D-108).
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

    /// <summary>
    /// <c>limits.minorCurfewEnabled</c> (beyond the contract, D-108): the minors' night curfew applies; on unless the owner
    /// turned it off.
    /// </summary>
    public bool CurfewEnabled { get; init; } = true;

    /// <summary><c>limits.minorAge</c>: a player younger than this on the club's local date is a minor (D-108).</summary>
    public int MinorAge { get; init; } = 18;

    /// <summary><c>limits.minorCurfew</c>: the forbidden hours start (<c>HH:mm</c>, club-local).</summary>
    public string MinorCurfew { get; init; } = "22:00";

    /// <summary><c>limits.minorCurfewEnd</c> (beyond the contract, D-108): the forbidden hours end (<c>HH:mm</c>, club-local).</summary>
    public string MinorCurfewEnd { get; init; } = "06:00";

    /// <summary>
    /// <c>limits.guestPostpaid</c> (beyond the contract, owner's choice in the console): the desk may seat a guest postpaid,
    /// to pay at the counter afterwards. On unless the owner turned it off (<c>false</c>). The kiosk never starts a guest's
    /// postpaid session, whatever this says.
    /// </summary>
    public bool GuestPostpaid { get; init; } = true;

    /// <summary>
    /// <c>limits.guestDebtLimit</c> (tiyin): how far below zero a guest's postpaid seat from the desk may run; null or 0 — no
    /// cap.
    /// </summary>
    public long? GuestDebtLimit { get; init; }

    /// <summary>
    /// <c>limits.memberDebtLimit</c> (tiyin, beyond the contract, D-31): how far below zero a member's postpaid seat from the
    /// desk may run; null or 0 — no cap. A member's own pay-as-you-go from the kiosk always has 0: the balance must cover
    /// every minute (<see cref="SessionService.DebtLimitFor"/>).
    /// </summary>
    public long? MemberDebtLimit { get; init; }

    /// <summary><c>limits.cashOutOwnerOnly</c> (beyond the contract, D-40): only the owner takes cash out of the drawer.</summary>
    public bool CashOutOwnerOnly { get; init; }

    /// <summary>
    /// <c>limits.autoExtendMinutes</c> (beyond the contract): when a prepaid session runs out, the tick extends it by this
    /// many minutes from the player's balance, as the cashier's extend does, for as long as the balance pays; 0 — off.
    /// </summary>
    public int AutoExtendMinutes { get; init; }

    /// <summary>
    /// <c>limits.warningMinutes</c> (beyond the contract, D-109): the minutes left at which the player is warned that the
    /// time — prepaid, or what the money still pays for on a capped postpaid — runs out; by default 10, 7 and 5. The last
    /// minute is always warned as well (<see cref="WarningMarks"/>). Unset — the default; <c>[]</c> — the last minute only.
    /// </summary>
    public int[] WarningMinutes { get; init; } = [.. DefaultWarningMinutes];

    /// <summary>The marks without the owner's choice (D-109).</summary>
    public static readonly IReadOnlyList<int> DefaultWarningMinutes = [10, 7, 5];

    /// <summary>The final warning every session gets, whatever the club's list.</summary>
    public const int LastMinuteWarning = 1;

    /// <summary>The most marks a club may set and the most minutes one may be (<c>400 limits.warningMinutes max</c>).</summary>
    public const int MaxWarningMarks = 8;

    public const int MaxWarningMinute = 120;

    /// <summary>The marks in force, descending: <see cref="WarningMinutes"/> and the last minute.</summary>
    public int[] WarningMarks => [.. WarningMinutes.Append(LastMinuteWarning).Distinct().OrderDescending()];

    /// <summary>
    /// The owner's marks as the PC gets them (<c>AgentServerConfig.session.warningMinutes</c>): descending, distinct, in
    /// range, without the last minute (the agent adds it itself, and a list with it would read as an old shipped default
    /// there). A value of the wrong kind cannot be stored (<c>ClubSettingsEndpoints</c> refuses it); here it means the default.
    /// </summary>
    public static int[] WarningMinutesOf(string? settingsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(settingsJson) ? "{}" : settingsJson);
        return WarningMinutesOf(doc.RootElement.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Object
            && limits.TryGetProperty("warningMinutes", out var marks) ? marks : default);
    }

    private static int[] WarningMinutesOf(JsonElement marks) =>
        marks.ValueKind != JsonValueKind.Array || marks.EnumerateArray().Any(m => !m.TryGetInt32(out _))
            ? [.. DefaultWarningMinutes]
            : [.. marks.EnumerateArray().Select(m => m.GetInt32()).Where(m => m is > LastMinuteWarning and <= MaxWarningMinute).Distinct().OrderDescending()];

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
            MinorCurfew = ClubTime.Minutes(doc?.Limits?.MinorCurfew) is null ? "22:00" : doc!.Limits!.MinorCurfew!,
            MinorCurfewEnd = ClubTime.Minutes(doc?.Limits?.MinorCurfewEnd) is null ? "06:00" : doc!.Limits!.MinorCurfewEnd!,
            CurfewEnabled = doc?.Limits?.MinorCurfewEnabled ?? true,
            GuestPostpaid = doc?.Limits?.GuestPostpaid ?? true,
            GuestDebtLimit = doc?.Limits?.GuestDebtLimit is > 0 and var limit ? limit : null,
            MemberDebtLimit = doc?.Limits?.MemberDebtLimit is > 0 and var memberLimit ? memberLimit : null,
            CashOutOwnerOnly = doc?.Limits?.CashOutOwnerOnly ?? false,
            AutoExtendMinutes = Math.Clamp(doc?.Limits?.AutoExtendMinutes ?? 0, 0, 720),
            WarningMinutes = WarningMinutesOf(settingsJson),
        };
    }

    /// <summary>The highest level whose <c>minSpent</c> is reached; null when the club has none.</summary>
    public LoyaltyLevel? LevelOf(long lifetimeSpent) => Loyalty.LastOrDefault(l => l.MinSpent <= lifetimeSpent);

    /// <summary>
    /// Who the minors' curfew holds (D-108, DESIGN §5.2 rule 5): <c>minor</c> — a member younger than <see cref="MinorAge"/>
    /// on the club's local date; <c>noBirthDate</c> — a member whose date of birth is not known (never taken for an adult);
    /// null — anyone else (an adult, a guest of the desk, a staff account) or the curfew is off.
    /// </summary>
    public string? CurfewHolds(string role, DateOnly? birthDate, DateTimeOffset at)
    {
        if (!CurfewEnabled || role is not ("member" or "vip"))
        {
            return null;
        }

        if (birthDate is not { } birth)
        {
            return "noBirthDate";
        }

        return Users.BirthDates.Age(birth, Users.BirthDates.Today(at, TimeZone)) < MinorAge ? "minor" : null;
    }

    /// <summary>
    /// <paramref name="at"/> falls in the forbidden hours, <see cref="MinorCurfew"/> (inclusive) to <see cref="MinorCurfewEnd"/>
    /// (exclusive) club-local; a window over midnight when the start is later than the end, none when they are equal.
    /// </summary>
    public bool InCurfewHours(DateTimeOffset at)
    {
        var local = ClubTime.Local(at, TimeZone);
        var minutes = (local.Hour * 60) + local.Minute;
        var (from, to) = (ClubTime.Minutes(MinorCurfew) ?? 22 * 60, ClubTime.Minutes(MinorCurfewEnd) ?? 6 * 60);
        return from != to && (from < to ? minutes >= from && minutes < to : minutes >= from || minutes < to);
    }

    /// <summary>A minor, or a member without a date of birth, in the forbidden hours: no session may start, resume or extend.</summary>
    public bool InCurfew(string role, DateOnly? birthDate, DateTimeOffset at) =>
        CurfewHolds(role, birthDate, at) is not null && InCurfewHours(at);

    /// <summary>
    /// The next start of the forbidden hours from <paramref name="at"/> on (<paramref name="at"/> itself inside them); null
    /// without a window.
    /// </summary>
    public DateTimeOffset? NextCurfewStart(DateTimeOffset at)
    {
        if (InCurfewHours(at))
        {
            return at;
        }

        var (from, to) = (ClubTime.Minutes(MinorCurfew) ?? 22 * 60, ClubTime.Minutes(MinorCurfewEnd) ?? 6 * 60);
        if (from == to)
        {
            return null;
        }

        var local = ClubTime.Local(at, TimeZone);
        var start = local.Date.AddMinutes(from);
        return ClubTime.Utc(start > local ? start : start.AddDays(1), TimeZone);
    }

    /// <summary>The start of the forbidden hours <paramref name="at"/> falls in; null outside them.</summary>
    public DateTimeOffset? CurfewStartedAt(DateTimeOffset at)
    {
        if (!InCurfewHours(at))
        {
            return null;
        }

        var local = ClubTime.Local(at, TimeZone);
        var start = local.Date.AddMinutes(ClubTime.Minutes(MinorCurfew) ?? 22 * 60);
        return ClubTime.Utc(start > local ? start.AddDays(-1) : start, TimeZone);
    }

    /// <summary>The refusal's text: «Несовершеннолетним нельзя играть с 22:00 до 06:00».</summary>
    public string CurfewText => $"Несовершеннолетним нельзя играть с {MinorCurfew} до {MinorCurfewEnd}";

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
        public string? MinorCurfewEnd { get; init; }
        public bool? MinorCurfewEnabled { get; init; }
        public bool? GuestPostpaid { get; init; }
        public long? GuestDebtLimit { get; init; }
        public long? MemberDebtLimit { get; init; }
        public bool? CashOutOwnerOnly { get; init; }
        public int? AutoExtendMinutes { get; init; }
    }
}

/// <summary>The price of a purchase and how it was reached; stored as <c>meta</c> of the charge (<c>package</c>: never refunded, §5.7).</summary>
public sealed record Quote(int Minutes, long Base, int DayPct, int DiscountPct, string? DiscountReason, long Total, bool Package)
{
    /// <summary>
    /// The kind of discount <see cref="DiscountReason"/> names — <c>group</c>, <c>loyalty</c> or <c>happyHour</c> — or null
    /// without one: the kiosk's <c>SessionQuote.discountReason</c>. Not part of the charge's <c>meta</c>.
    /// </summary>
    [JsonIgnore]
    public string? DiscountKind { get; init; }
}

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

    /// <summary>
    /// D-90, time «по сумме»: the quote of the most whole minutes in <paramref name="minMinutes"/>..<paramref name="maxMinutes"/>
    /// of an hourly tariff whose <see cref="Compute"/> total is at most <paramref name="amount"/>, so the price charged is
    /// never above what the cashier typed (the rest of a minute is not sold); null when even the shortest purchase costs
    /// more. The total never falls as minutes grow (ceil base, then one half-up rounding), so a binary search finds it.
    /// </summary>
    public static Quote? MostFor(
        TariffRow tariff, long amount, int minMinutes, int maxMinutes, string? groupId, long? lifetimeSpent, string zone, DateTimeOffset at, ClubPricing club)
    {
        Quote Price(int minutes) => Compute(tariff, minutes, groupId, lifetimeSpent, zone, at, club);
        var best = Price(minMinutes);
        if (best.Total > amount)
        {
            return null;
        }

        var (lo, hi) = (minMinutes, Math.Max(minMinutes, maxMinutes));
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) / 2);
            var q = Price(mid);
            if (q.Total <= amount)
            {
                (lo, best) = (mid, q);
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    /// <param name="groupId">The buyer's group in this club; null for a walk-in quote.</param>
    /// <param name="lifetimeSpent">The buyer's lifetime spend (loyalty level); null for a walk-in quote.</param>
    public static Quote Compute(TariffRow tariff, int minutes, string? groupId, long? lifetimeSpent, string zone, DateTimeOffset at, ClubPricing club)
    {
        var local = ClubTime.Local(at, club.TimeZone);
        var dayPct = club.Holidays.Contains(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            ? club.HolidayPct
            : club.WeekdayPct[(int)local.DayOfWeek];

        (int Pct, string Name, string Kind)? best = null;
        void Offer(int pct, string name, string kind)
        {
            if (pct > 0 && (best is null || pct > best.Value.Pct))
            {
                best = (pct, name, kind);
            }
        }

        if (groupId is not null && club.Groups.FirstOrDefault(g => g.Id == groupId) is { } group)
        {
            Offer(group.DiscountPct, group.Name, "group");
        }

        if (lifetimeSpent is { } spent && club.LevelOf(spent) is { } level)
        {
            Offer(level.DiscountPct, level.Name, "loyalty");
        }

        var minute = (local.Hour * 60) + local.Minute;
        foreach (var hh in club.HappyHours)
        {
            var (from, to) = (ClubTime.Minutes(hh.From) ?? 0, ClubTime.Minutes(hh.To) ?? 0);
            if (hh.Days.Contains((int)local.DayOfWeek)
                && (hh.Zones.Length == 0 || hh.Zones.Contains(zone, StringComparer.OrdinalIgnoreCase))
                && (from <= to ? minute >= from && minute < to : minute >= from || minute < to))
            {
                Offer(hh.DiscountPct, hh.Name, "happyHour");
            }
        }

        var @base = tariff.IsPackage ? tariff.PackagePrice ?? 0 : Base(tariff.PricePerHour, minutes);
        var discount = best?.Pct ?? 0;
        return new Quote(minutes, @base, dayPct, discount, best?.Name, Total(@base, dayPct, discount), tariff.IsPackage) { DiscountKind = best?.Kind };
    }
}
