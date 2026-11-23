using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The top-up promotion «x2» (D-84, beyond the contract): <c>settings.topUpPromo</c>. Turned on, a desk top-up of at
/// least <see cref="MinAmount"/> gets <see cref="BonusPct"/> % on top (100 = «x2») on the listed weekdays (0 = Sunday … 6;
/// empty = every day), between <see cref="From"/> and <see cref="To"/> of the club's time (both null = all day; <c>to</c>
/// before <c>from</c> runs past midnight) and until <see cref="Until"/> (null = no end). It does not add up with the
/// tier bonus (<c>bonusTiers</c>): the top-up gets the larger of the two.
/// </summary>
public sealed record TopUpPromo(bool Enabled, int BonusPct, long MinAmount, int[] Days, string? From, string? To, DateTimeOffset? Until)
{
    public static readonly TopUpPromo Default = new(false, 100, 0, [], null, null, null);

    /// <summary>The promotion of the club (<see cref="Default"/> — off — when the owner never saved one).</summary>
    public static async Task<TopUpPromo> OfClubAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid clubId) =>
        Read(await c.ExecuteScalarAsync<string?>("SELECT (settings -> 'topUpPromo')::text FROM clubs WHERE id = @clubId", new { clubId }, tx));

    public static TopUpPromo Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Default;
        }

        try
        {
            return Parse(JsonElement.Parse(json), validate: false);
        }
        catch (ApiException)
        {
            return Default;
        }
    }

    /// <summary>The owner's patch, checked: <c>400 validation</c> on <c>topUpPromo.*</c> for what is out of range.</summary>
    public static TopUpPromo Parse(JsonElement e, bool validate = true)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("topUpPromo", "schema", "JSON object expected");
        }

        var enabled = e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
        var pct = e.TryGetProperty("bonusPct", out var p) && p.TryGetInt32(out var n) ? n : Default.BonusPct;
        if (validate && pct is < 1 or > 400)
        {
            throw ApiException.Validation("topUpPromo.bonusPct", pct < 1 ? "min" : "max");
        }

        var min = e.TryGetProperty("minAmount", out var m) && m.TryGetInt64(out var mm) ? mm : 0;
        if (validate && min < 0)
        {
            throw ApiException.Validation("topUpPromo.minAmount", "min");
        }

        var days = e.TryGetProperty("days", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(x => x.TryGetInt32(out var v) ? v : -1).ToArray()
            : [];
        if (validate && days.Any(x => x is < 0 or > 6))
        {
            throw ApiException.Validation("topUpPromo.days", "enum");
        }

        var from = Time(e, "from", validate);
        var to = Time(e, "to", validate);
        if (validate && (from is null) != (to is null))
        {
            throw ApiException.Validation(from is null ? "topUpPromo.from" : "topUpPromo.to", "required");
        }

        DateTimeOffset? until = null;
        if (e.TryGetProperty("until", out var u) && u.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                until = at;
            }
            else if (validate)
            {
                throw ApiException.Validation("topUpPromo.until", "format");
            }
        }

        return new TopUpPromo(enabled, Math.Clamp(pct, 1, 400), Math.Max(0, min), [.. days.Where(x => x is >= 0 and <= 6).Distinct().Order()], from, to, until);
    }

    private static string? Time(JsonElement e, string name, bool validate)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = v.GetString();
        if (ClubTime.Minutes(s) is null)
        {
            return validate ? throw ApiException.Validation("topUpPromo." + name, "format") : null;
        }

        return s;
    }

    public JsonNode ToJson() => new JsonObject
    {
        ["enabled"] = Enabled, ["bonusPct"] = BonusPct, ["minAmount"] = MinAmount, ["days"] = new JsonArray([.. Days.Select(x => (JsonNode)x)]),
        ["from"] = From, ["to"] = To, ["until"] = Until is { } at ? ServerJson.FormatTime(at) : null,
    };

    /// <summary>Whether the promotion runs at <paramref name="now"/> (the club's <paramref name="timeZone"/>).</summary>
    public bool ActiveAt(DateTimeOffset now, string timeZone)
    {
        if (!Enabled || (Until is { } until && now >= until))
        {
            return false;
        }

        var local = ClubTime.Local(now, timeZone);
        var minute = (local.Hour * 60) + local.Minute;
        if (ClubTime.Minutes(From) is not { } from || ClubTime.Minutes(To) is not { } to || from == to)
        {
            return Days.Length == 0 || Days.Contains((int)local.DayOfWeek);
        }

        // Past midnight, the minutes after it belong to the window opened the day before.
        var overnight = to < from;
        var inside = overnight ? minute >= from || minute < to : minute >= from && minute < to;
        var day = overnight && minute < to ? (int)local.AddDays(-1).DayOfWeek : (int)local.DayOfWeek;
        return inside && (Days.Length == 0 || Days.Contains(day));
    }

    /// <summary>The promotion's bonus percent for a top-up of <paramref name="amount"/> at <paramref name="now"/>, 0 when it does not apply.</summary>
    public int PctFor(long amount, DateTimeOffset now, string timeZone) => amount > 0 && amount >= MinAmount && ActiveAt(now, timeZone) ? BonusPct : 0;
}
