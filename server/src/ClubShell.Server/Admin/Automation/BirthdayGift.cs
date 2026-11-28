using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The birthday gift (D-107, beyond the contract): <c>settings.birthdayGift</c>, the automation rule «День рождения» of the
/// console. Turned on, a member whose birthday (club-local date, 29 February — the 28th in a common year) lies within
/// <see cref="WindowDays"/> days of today (before or after; 0 — the day itself) gets <see cref="Amount"/> tiyin on the
/// bonus account (D-106: play time only) the first time they play then — at a session opened on a PC of the club, or by the
/// club minute for a session already running — once per birthday year, and the message <see cref="Text"/> on that PC
/// (<c>{bonus}</c> — the amount; empty — «С днём рождения! +N бонусов»). Not in <c>settings.automation</c>: the contract's
/// <c>AdminRuleTrigger</c> is a closed enum, and every rule the server answers must match it.
/// </summary>
public sealed record BirthdayGift(bool Enabled, long Amount, int WindowDays, string Text)
{
    /// <summary>The rule id of its <c>rule_firings</c> rows (target <c>birthday:{userId}:{year}</c>).</summary>
    public const string RuleId = "birthdayGift";

    /// <summary>Off, 20 000 sum, ±3 days: the example the owner only turns on.</summary>
    public static readonly BirthdayGift Default = new(false, 2_000_000, 3, "");

    public const int MaxWindowDays = 30;
    public const int MaxText = 200;

    public static async Task<BirthdayGift> OfClubAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid clubId) =>
        Read(await c.ExecuteScalarAsync<string?>("SELECT (settings -> 'birthdayGift')::text FROM clubs WHERE id = @clubId", new { clubId }, tx));

    public static BirthdayGift Read(string? json)
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

    /// <summary>The owner's patch, checked: <c>400 validation</c> on <c>birthdayGift.*</c> for what is out of range.</summary>
    public static BirthdayGift Parse(JsonElement e, bool validate = true)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("birthdayGift", "schema", "JSON object expected");
        }

        if (validate && e.TryGetProperty("enabled", out var on) && on.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw ApiException.Validation("birthdayGift.enabled", "format");
        }

        var enabled = e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
        var amount = e.TryGetProperty("amount", out var a) && a.TryGetInt64(out var n) ? n : Default.Amount;
        if (validate && amount is < 0 or > 1_000_000_000)
        {
            throw ApiException.Validation("birthdayGift.amount", amount < 0 ? "min" : "max");
        }

        var window = e.TryGetProperty("windowDays", out var w) && w.TryGetInt32(out var d) ? d : Default.WindowDays;
        if (validate && window is < 0 or > MaxWindowDays)
        {
            throw ApiException.Validation("birthdayGift.windowDays", window < 0 ? "min" : "max");
        }

        var text = e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
        if (validate && text.Length > MaxText)
        {
            throw ApiException.Validation("birthdayGift.text", "max");
        }

        return new BirthdayGift(enabled, Math.Clamp(amount, 0, 1_000_000_000), Math.Clamp(window, 0, MaxWindowDays), text.Length > MaxText ? text[..MaxText] : text);
    }

    /// <summary>The stored section; the console's copy adds the counters (<c>fired</c> this year, <c>lastFiredAt</c>).</summary>
    public JsonObject ToJson() => new() { ["enabled"] = Enabled, ["amount"] = Amount, ["windowDays"] = WindowDays, ["text"] = Text };

    /// <summary>The message on the PC: the owner's text with <c>{bonus}</c>, else «С днём рождения! +20 000 бонусов».</summary>
    public string MessageFor(long amount)
    {
        var sum = (amount / 100).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ", StringComparison.Ordinal);
        return Text.Length == 0 ? $"С днём рождения! +{sum} бонусов" : Text.Replace("{bonus}", sum, StringComparison.Ordinal);
    }
}
