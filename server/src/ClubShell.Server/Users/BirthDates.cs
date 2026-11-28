using System.Globalization;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Users;

/// <summary>
/// A player's date of birth (D-107, <c>users.birth_date</c>, M0031): <c>YYYY-MM-DD</c> on the wire, an age of
/// <see cref="MinAge"/>–<see cref="MaxAge"/> on the club's local date, never in the future.
/// </summary>
public static class BirthDates
{
    public const int MinAge = 5;
    public const int MaxAge = 100;

    /// <summary>
    /// The given date, else <c>400 birthDate required</c> (none), <c>format</c> (not <c>YYYY-MM-DD</c>), <c>future</c>
    /// (after <paramref name="today"/>), <c>min</c> (younger than <see cref="MinAge"/>) or <c>max</c> (older than
    /// <see cref="MaxAge"/>).
    /// </summary>
    public static DateOnly Required(string? value, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ApiException.Validation("birthDate", "required");
        }

        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw ApiException.Validation("birthDate", "format");
        }

        if (date > today)
        {
            throw ApiException.Validation("birthDate", "future");
        }

        var age = Age(date, today);
        if (age < MinAge)
        {
            throw ApiException.Validation("birthDate", "min");
        }

        return age > MaxAge ? throw ApiException.Validation("birthDate", "max") : date;
    }

    /// <summary>A stored date as text (<c>to_char(birth_date, 'YYYY-MM-DD')</c>); null or malformed — null.</summary>
    public static DateOnly? Parse(string? stored) =>
        DateOnly.TryParseExact(stored, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Whole years on <paramref name="today"/>; one born on 29 February turns a year older on 1 March of a common year.</summary>
    public static int Age(DateOnly birth, DateOnly today)
    {
        var age = today.Year - birth.Year;
        return today.Month < birth.Month || (today.Month == birth.Month && today.Day < birth.Day) ? age - 1 : age;
    }

    /// <summary>The birthday in <paramref name="year"/>: 29 February is the 28th in a common year.</summary>
    public static DateOnly BirthdayIn(DateOnly birth, int year) =>
        new(year, birth.Month, birth.Month == 2 && birth.Day == 29 && !DateTime.IsLeapYear(year) ? 28 : birth.Day);

    /// <summary>
    /// The year whose birthday lies within <paramref name="windowDays"/> days of <paramref name="today"/> (before or after),
    /// or null: the key of the birthday gift (once per year, D-107). A window wider than half a year is not meaningful and
    /// is cut to 30 days by the settings.
    /// </summary>
    public static int? BirthdayYear(DateOnly birth, DateOnly today, int windowDays)
    {
        foreach (var year in new[] { today.Year, today.Year - 1, today.Year + 1 })
        {
            if (year <= birth.Year)
            {
                continue;
            }

            var day = BirthdayIn(birth, year);
            if (Math.Abs(day.DayNumber - today.DayNumber) <= windowDays)
            {
                return year;
            }
        }

        return null;
    }

    /// <summary>The club's local date at <paramref name="at"/>.</summary>
    public static DateOnly Today(DateTimeOffset at, string timeZone) => DateOnly.FromDateTime(ClubTime.Local(at, timeZone));
}
