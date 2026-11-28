using ClubShell.Server.Sessions.Billing;

namespace ClubShell.Server.Tests;

/// <summary>The single price function (DESIGN §5.1): the e2e case, rounding, packages, calendar in the club's zone.</summary>
public sealed class PricingTests
{
    private static readonly TariffRow Standard = new() { Id = Guid.NewGuid(), Name = "Standard", PricePerHour = 1_200_000, MinMinutes = 30, MaxMinutes = 720 };

    // Sunday 20:30 UTC is Monday 01:30 in Tashkent (UTC+5).
    private static readonly DateTimeOffset SundayEveningUtc = new(2026, 9, 27, 20, 30, 0, TimeSpan.Zero);

    [Fact]
    public void E2e_case_day_rate_and_only_the_single_best_discount()
    {
        // console.spec.ts: every day +20 %, a -30 % happy hour over the whole clock, groups staff -50 %, student -15 %.
        var club = ClubPricing.Parse("Asia/Tashkent", """
            {"pricing":{"weekdayPct":[120,120,120,120,120,120,120],"holidays":[],"holidayPct":120},
             "happyHours":[{"id":"hh-am","name":"Весь день","days":[0,1,2,3,4,5,6],"from":"00:00","to":"12:00","discountPct":30,"zones":[]},
                           {"id":"hh-pm","name":"Весь день","days":[0,1,2,3,4,5,6],"from":"12:00","to":"00:00","discountPct":30,"zones":[]}],
             "groups":[{"id":"student","name":"Школьник","discountPct":15,"color":"#A855F7"},{"id":"staff","name":"Сотрудник","discountPct":50,"color":"#3B82F6"}]}
            """);

        var walkIn = Pricing.Compute(Standard, 60, null, null, "", SundayEveningUtc, club);
        Assert.Equal((120, 30, 1_008_000L), (walkIn.DayPct, walkIn.DiscountPct, walkIn.Total));

        var staff = Pricing.Compute(Standard, 60, "staff", 0, "", SundayEveningUtc, club);
        Assert.Equal((50, "Сотрудник", 720_000L), (staff.DiscountPct, staff.DiscountReason, staff.Total));

        Assert.Equal(30, Pricing.Compute(Standard, 60, "student", 0, "", SundayEveningUtc, club).DiscountPct);
    }

    [Fact]
    public void Base_is_ceiled_to_a_tiyin_and_total_rounded_half_up_to_a_whole_sum()
    {
        Assert.Equal(16_667, Pricing.Base(1_000_001, 1));
        Assert.Equal(200, Pricing.Total(150, 100, 0));
        Assert.Equal(100, Pricing.Total(149, 100, 0));
        Assert.Equal(0, Pricing.Total(1_000_000, 100, 100));

        // Postpaid: minutes are ceil(used / 60).
        Assert.Equal(Pricing.Total(Pricing.Base(1_200_000, 2), 100, 0), Pricing.Frozen(1_200_000, 61, 100, 0));
    }

    [Fact]
    public void Package_costs_its_price_for_any_minutes()
    {
        var night = new TariffRow { Id = Guid.NewGuid(), Name = "Night", PricePerHour = 800_000, IsPackage = true, PackageMinutes = 300, PackagePrice = 4_000_000 };
        var club = ClubPricing.Parse("Asia/Tashkent", "{}");
        Assert.Equal(4_000_000, Pricing.Compute(night, 300, null, null, "", SundayEveningUtc, club).Total);
        Assert.Equal(4_000_000, Pricing.Compute(night, 30, null, null, "", SundayEveningUtc, club).Total);
    }

    [Fact]
    public void Weekday_holiday_and_happy_hour_are_read_in_the_club_zone()
    {
        const string Week = """{"pricing":{"weekdayPct":[150,110,100,100,100,100,150],"holidays":[],"holidayPct":200}}""";
        Assert.Equal(110, Pricing.Compute(Standard, 60, null, null, "", SundayEveningUtc, ClubPricing.Parse("Asia/Tashkent", Week)).DayPct);
        Assert.Equal(150, Pricing.Compute(Standard, 60, null, null, "", SundayEveningUtc, ClubPricing.Parse("UTC", Week)).DayPct);

        var holiday = ClubPricing.Parse("Asia/Tashkent", """{"pricing":{"weekdayPct":[150,110,100,100,100,100,150],"holidays":["2026-09-28"],"holidayPct":200}}""");
        Assert.Equal(200, Pricing.Compute(Standard, 60, null, null, "", SundayEveningUtc, holiday).DayPct);

        // Monday 01:00-02:00 local, VIP zone only.
        var night = ClubPricing.Parse("Asia/Tashkent", """
            {"happyHours":[{"id":"n","name":"Ночь","days":[1],"from":"01:00","to":"02:00","discountPct":20,"zones":["vip"]}]}
            """);
        Assert.Equal(20, Pricing.Compute(Standard, 60, null, null, "VIP", SundayEveningUtc, night).DiscountPct);
        Assert.Equal(0, Pricing.Compute(Standard, 60, null, null, "Standard", SundayEveningUtc, night).DiscountPct);
    }

    [Fact]
    public void Loyalty_level_follows_lifetime_spend_and_competes_with_other_discounts()
    {
        var club = ClubPricing.Parse("Asia/Tashkent", """
            {"loyalty":[{"level":2,"name":"Игрок","minSpent":50000000,"discountPct":3},{"level":1,"name":"Новичок","minSpent":0,"discountPct":0},
                        {"level":3,"name":"Про","minSpent":150000000,"discountPct":5}],
             "groups":[{"id":"regular","name":"Постоянный","discountPct":4,"color":"#000"}]}
            """);
        Assert.Equal(1, club.LevelOf(0)!.Level);
        Assert.Equal(3, club.LevelOf(150_000_000)!.Level);
        var quote = Pricing.Compute(Standard, 60, "regular", 150_000_000, "", SundayEveningUtc, club);
        Assert.Equal((5, "Про"), (quote.DiscountPct, quote.DiscountReason));
    }

    [Fact]
    public void Minors_curfew_runs_from_the_configured_hour_to_six_in_the_morning()
    {
        var club = ClubPricing.Parse("Asia/Tashkent", """{"limits":{"minorAge":18,"minorCurfew":"22:00"}}""");
        var minor = new DateOnly(2012, 3, 1);
        Assert.True(club.InCurfew("member", minor, SundayEveningUtc)); // 01:30 local
        Assert.False(club.InCurfew("member", new DateOnly(2000, 1, 1), SundayEveningUtc));
        Assert.False(club.InCurfew("guest", null, SundayEveningUtc)); // the desk answers for a guest
        Assert.True(club.InCurfew("member", null, SundayEveningUtc)); // never taken for an adult (D-108)
        Assert.False(club.InCurfew("member", minor, new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero))); // 15:00 local
    }

    [Fact]
    public void Curfew_hours_come_from_the_settings_and_the_age_from_the_local_date()
    {
        var club = ClubPricing.Parse("Asia/Tashkent", """{"limits":{"minorAge":18,"minorCurfew":"21:30","minorCurfewEnd":"07:00"}}""");
        Assert.Equal(("21:30", "07:00", true), (club.MinorCurfew, club.MinorCurfewEnd, club.CurfewEnabled));
        Assert.Equal("Несовершеннолетним нельзя играть с 21:30 до 07:00", club.CurfewText);

        // 2026-11-28 21:40 local = 16:40 UTC; the next start from 20:00 local is 21:30 local the same day.
        var evening = new DateTimeOffset(2026, 11, 28, 16, 40, 0, TimeSpan.Zero);
        Assert.True(club.InCurfewHours(evening));
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 16, 30, 0, TimeSpan.Zero), club.CurfewStartedAt(evening));
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 16, 30, 0, TimeSpan.Zero), club.NextCurfewStart(new DateTimeOffset(2026, 11, 28, 15, 0, 0, TimeSpan.Zero)));
        Assert.True(club.InCurfewHours(new DateTimeOffset(2026, 11, 29, 1, 30, 0, TimeSpan.Zero))); // 06:30 local
        Assert.False(club.InCurfewHours(new DateTimeOffset(2026, 11, 29, 2, 0, 0, TimeSpan.Zero))); // 07:00 local

        // Eighteen on the club's date: the day before the birthday still a minor, on it an adult.
        var born = new DateOnly(2008, 11, 29);
        Assert.Equal("minor", club.CurfewHolds("member", born, evening));
        Assert.Null(club.CurfewHolds("vip", born, new DateTimeOffset(2026, 11, 28, 19, 5, 0, TimeSpan.Zero))); // 00:05 local on the 29th

        var off = ClubPricing.Parse("Asia/Tashkent", """{"limits":{"minorAge":18,"minorCurfew":"22:00","minorCurfewEnabled":false}}""");
        Assert.Null(off.CurfewHolds("member", born, evening));
    }

    [Fact]
    public void Birthday_years_and_ages()
    {
        var leap = new DateOnly(2008, 2, 29);
        Assert.Equal(17, Users.BirthDates.Age(leap, new DateOnly(2026, 2, 28)));
        Assert.Equal(18, Users.BirthDates.Age(leap, new DateOnly(2026, 3, 1)));
        Assert.Equal(new DateOnly(2026, 2, 28), Users.BirthDates.BirthdayIn(leap, 2026));
        Assert.Equal(2026, Users.BirthDates.BirthdayYear(leap, new DateOnly(2026, 3, 2), 3));
        Assert.Null(Users.BirthDates.BirthdayYear(leap, new DateOnly(2026, 3, 5), 3));

        // Across the new year the gift belongs to the birthday's own year.
        var december = new DateOnly(1999, 12, 30);
        Assert.Equal(2025, Users.BirthDates.BirthdayYear(december, new DateOnly(2026, 1, 2), 3));
        Assert.Equal(2026, Users.BirthDates.BirthdayYear(december, new DateOnly(2026, 12, 28), 3));
        Assert.Null(Users.BirthDates.BirthdayYear(december, new DateOnly(2026, 12, 28), 0));
    }
}
