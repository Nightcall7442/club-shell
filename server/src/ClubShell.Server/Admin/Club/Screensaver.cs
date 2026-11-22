using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Contracts.Pcs;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Admin;

/// <summary>One picture or clip of the screensaver as the owner saved it.</summary>
public sealed record ScreensaverSlide(string Url, string Type, int DurationSec);

/// <summary>
/// The free PCs' screensaver (D-87, beyond the contract): <c>settings.screensaver</c>. On, a PC with nobody signed in
/// shows the attract screen after <see cref="AfterMinutes"/> minutes without input, playing the owner's pictures and
/// clips (none — the shell's built-in slides). It reaches the PCs as <c>shell.club.screensaver</c> of the agent config;
/// a save bumps the config version like the branding does.
/// </summary>
public sealed record Screensaver(bool Enabled, int AfterMinutes, IReadOnlyList<ScreensaverSlide> Items)
{
    public const int MaxItems = 20;

    public static readonly Screensaver Default = new(false, 5, []);

    public static Screensaver Read(string? json)
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

    /// <summary>The owner's patch, checked: <c>400 validation</c> on <c>screensaver.*</c>.</summary>
    public static Screensaver Parse(JsonElement e, bool validate = true)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("screensaver", "schema", "JSON object expected");
        }

        var enabled = e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
        var after = e.TryGetProperty("afterMinutes", out var a) && a.TryGetInt32(out var n) ? n : Default.AfterMinutes;
        if (validate && after is < 1 or > 120)
        {
            throw ApiException.Validation("screensaver.afterMinutes", after < 1 ? "min" : "max");
        }

        var items = new List<ScreensaverSlide>();
        if (e.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in list.EnumerateArray())
            {
                var field = $"screensaver.items[{i++}]";
                if (validate && items.Count >= MaxItems)
                {
                    throw ApiException.Validation("screensaver.items", "max");
                }

                var url = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()!.Trim() : "";
                if (!IsUrl(url))
                {
                    if (validate)
                    {
                        throw ApiException.Validation(field + ".url", "format");
                    }

                    continue;
                }

                var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "image";
                if (type is not ("image" or "video"))
                {
                    if (validate)
                    {
                        throw ApiException.Validation(field + ".type", "enum");
                    }

                    type = "image";
                }

                var seconds = item.TryGetProperty("durationSec", out var d) && d.TryGetInt32(out var s) ? s : 10;
                if (validate && seconds is < 3 or > 600)
                {
                    throw ApiException.Validation(field + ".durationSec", seconds < 3 ? "min" : "max");
                }

                items.Add(new ScreensaverSlide(url, type!, Math.Clamp(seconds, 3, 600)));
            }
        }

        return new Screensaver(enabled, Math.Clamp(after, 1, 120), items.Take(MaxItems).ToList());
    }

    /// <summary>http(s) only: the PC loads it from the internet or the club's own server.</summary>
    private static bool IsUrl(string url) =>
        url.Length <= 2048 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public JsonNode ToJson() => new JsonObject
    {
        ["enabled"] = Enabled,
        ["afterMinutes"] = AfterMinutes,
        ["items"] = new JsonArray([.. Items.Select(i => (JsonNode)new JsonObject { ["url"] = i.Url, ["type"] = i.Type, ["durationSec"] = i.DurationSec })]),
    };

    /// <summary>What the PC gets (<c>shell.club.screensaver</c>).</summary>
    public ShellScreensaver ToShell() => new(Enabled, AfterMinutes * 60, [.. Items.Select(i => new ScreensaverItem(i.Url, i.Type, i.DurationSec))]);
}
