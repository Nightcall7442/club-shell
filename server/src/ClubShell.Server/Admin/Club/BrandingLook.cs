using System.Text.Json;
using System.Text.RegularExpressions;
using ClubShell.Contracts.Pcs;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Admin;

/// <summary>
/// The player-screen designer of «Интерфейс и логотипы» (D-105, beyond the contract): <c>settings.branding.lock</c>,
/// <c>.idle</c>, <c>.desktop</c>, <c>.font</c> and <c>.tint</c>. Every field is optional with today's look as its
/// default: a club that never opened the designer sends no <see cref="ShellLook"/> at all.
/// </summary>
public static partial class BrandingLook
{
    public static readonly string[] Screens = ["lock", "idle", "desktop"];
    public static readonly string[] Fonts = ["unbounded", "inter", "mono"];
    public static readonly string[] Sizes = ["s", "m", "l", "xl"];

    /// <summary>Where the logo may stand on each screen: never over the sign-in card, the clock or the price list.</summary>
    public static IReadOnlyList<string> PositionsOf(string screen) => screen switch
    {
        "lock" => ["topLeft", "topCenter", "topRight", "center"],
        "idle" => ["topLeft", "topCenter", "topRight", "center", "bottomLeft", "bottomRight"],
        _ => ["topLeft"],
    };

    /// <summary>The top bar is too low for the largest logo.</summary>
    public static IReadOnlyList<string> SizesOf(string screen) => screen == "desktop" ? ["s", "m", "l"] : Sizes;

    /// <summary>The screen's own switches (beyond the logo and the picture).</summary>
    public static IReadOnlyList<string> FlagsOf(string screen) => screen switch
    {
        "lock" => ["clock", "language"],
        "idle" => ["clock", "prices", "freeBadge"],
        _ => [],
    };

    /// <summary>The screen's own texts and their longest length.</summary>
    public static IReadOnlyList<(string Name, int Max)> TextsOf(string screen) => screen switch
    {
        "lock" => [("title", 60), ("subtitle", 120)],
        "idle" => [("cta", 60)],
        _ => [],
    };

    public const int MaxDim = 80;
    public const int MaxUrl = 2048;
    private static readonly string[] Languages = ["ru", "uz", "en"];

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();

    /// <summary>The owner's branding patch, checked: <c>400 validation</c> on <c>branding.&lt;field&gt;</c>.</summary>
    public static void Validate(JsonElement branding)
    {
        if (branding.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (branding.TryGetProperty("font", out var font) && font.ValueKind != JsonValueKind.Null
            && (font.ValueKind != JsonValueKind.String || !Fonts.Contains(font.GetString())))
        {
            throw ApiException.Validation("branding.font", "enum");
        }

        if (branding.TryGetProperty("tint", out var tint) && tint.ValueKind != JsonValueKind.Null
            && (tint.ValueKind != JsonValueKind.String || !Hex().IsMatch(tint.GetString()!)))
        {
            throw ApiException.Validation("branding.tint", "format", "#RRGGBB expected");
        }

        foreach (var screen in Screens)
        {
            if (branding.TryGetProperty(screen, out var look) && look.ValueKind != JsonValueKind.Null)
            {
                ValidateScreen(screen, look);
            }
        }
    }

    private static void ValidateScreen(string screen, JsonElement look)
    {
        var at = "branding." + screen;
        if (look.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation(at, "schema", "JSON object expected");
        }

        if (look.TryGetProperty("logo", out var logo) && logo.ValueKind != JsonValueKind.Null)
        {
            if (logo.ValueKind != JsonValueKind.Object)
            {
                throw ApiException.Validation(at + ".logo", "schema", "JSON object expected");
            }

            Flag(logo, at + ".logo", "show");
            Flag(logo, at + ".logo", "name");
            OneOf(logo, at + ".logo", "position", PositionsOf(screen));
            OneOf(logo, at + ".logo", "size", SizesOf(screen));
        }

        if (look.TryGetProperty("wallpaperUrl", out var url) && url.ValueKind != JsonValueKind.Null)
        {
            if (url.ValueKind != JsonValueKind.String)
            {
                throw ApiException.Validation(at + ".wallpaperUrl", "type");
            }

            var text = url.GetString()!;
            if (text.Length > MaxUrl)
            {
                throw ApiException.Validation(at + ".wallpaperUrl", "max");
            }

            // A picture on the club's server (an upload, D-93) or a web link; anything else the PC could not load.
            if (text.Length > 0 && !(text.StartsWith('/') && !text.StartsWith("//", StringComparison.Ordinal))
                && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                throw ApiException.Validation(at + ".wallpaperUrl", "format", "an uploaded picture or an http(s) link expected");
            }
        }

        if (look.TryGetProperty("dim", out var dim) && dim.ValueKind != JsonValueKind.Null)
        {
            if (dim.ValueKind != JsonValueKind.Number || !dim.TryGetInt32(out var pct))
            {
                throw ApiException.Validation(at + ".dim", "type", "whole percent expected");
            }

            if (pct < 0)
            {
                throw ApiException.Validation(at + ".dim", "min");
            }

            if (pct > MaxDim)
            {
                throw ApiException.Validation(at + ".dim", "max", $"at most {MaxDim}%: the picture would turn black");
            }
        }

        Flag(look, at, "blur");
        foreach (var name in FlagsOf(screen))
        {
            Flag(look, at, name);
        }

        foreach (var (name, max) in TextsOf(screen))
        {
            if (!look.TryGetProperty(name, out var text) || text.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (text.ValueKind != JsonValueKind.Object)
            {
                throw ApiException.Validation($"{at}.{name}", "schema", "{ru, uz, en} expected");
            }

            foreach (var lang in Languages)
            {
                if (!text.TryGetProperty(lang, out var line) || line.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (line.ValueKind != JsonValueKind.String)
                {
                    throw ApiException.Validation($"{at}.{name}.{lang}", "type");
                }

                if (line.GetString()!.Trim().Length > max)
                {
                    throw ApiException.Validation($"{at}.{name}.{lang}", "max", $"at most {max} characters");
                }
            }
        }
    }

    private static void Flag(JsonElement e, string at, string name)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
        {
            throw ApiException.Validation($"{at}.{name}", "type");
        }
    }

    private static void OneOf(JsonElement e, string at, string name, IReadOnlyList<string> allowed)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            && (v.ValueKind != JsonValueKind.String || !allowed.Contains(v.GetString())))
        {
            throw ApiException.Validation($"{at}.{name}", "enum");
        }
    }

    /// <summary>
    /// What the PCs get, or null when the owner never touched the designer. Each screen's picture is resolved here: its
    /// own, else the club wallpaper (lock and desktop; the idle screen keeps its slides unless given one).
    /// </summary>
    public static ShellLook? ToShell(JsonElement branding)
    {
        if (branding.ValueKind != JsonValueKind.Object
            || !new[] { "lock", "idle", "desktop", "font", "tint" }.Any(k => branding.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null))
        {
            return null;
        }

        var wallpaper = Text(branding, "wallpaperUrl");
        var font = Text(branding, "font") is { } f && Fonts.Contains(f) ? f : "unbounded";
        var tint = Text(branding, "tint") is { } t && Hex().IsMatch(t) ? t.ToUpperInvariant() : null;
        return new ShellLook(
            Screen(branding, "lock", wallpaper),
            Screen(branding, "idle", null),
            Screen(branding, "desktop", wallpaper),
            font,
            tint);
    }

    /// <summary>
    /// The picture that carries the server's address to the PC (<c>shell.club.wallpaperUrl</c>; the Agent puts its
    /// server in front of a server path there, not inside the look): the club wallpaper, unless it is a web link while
    /// a screen has an uploaded picture of its own — then that one, so the shell can find the server for the others.
    /// </summary>
    public static string? AnchorWallpaper(JsonElement branding, ShellLook? look)
    {
        var wallpaper = Text(branding, "wallpaperUrl");
        if (look is null || IsServerPath(wallpaper) || IsServerPath(Text(branding, "logoUrl")))
        {
            return wallpaper;
        }

        return new[] { look.Lock.WallpaperUrl, look.Idle.WallpaperUrl, look.Desktop.WallpaperUrl }.FirstOrDefault(IsServerPath) ?? wallpaper;
    }

    private static bool IsServerPath(string? url) => url is { Length: > 1 } && url[0] == '/' && url[1] != '/';

    private static ShellScreenLook Screen(JsonElement branding, string screen, string? fallbackWallpaper)
    {
        var look = branding.TryGetProperty(screen, out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
        var logo = look.ValueKind == JsonValueKind.Object && look.TryGetProperty("logo", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
        var position = Text(logo, "position") is { } p && PositionsOf(screen).Contains(p) ? p : "topLeft";
        var size = Text(logo, "size") is { } s && SizesOf(screen).Contains(s) ? s : "m";
        int? dim = look.ValueKind == JsonValueKind.Object && look.TryGetProperty("dim", out var d) && d.TryGetInt32(out var pct)
            ? Math.Clamp(pct, 0, MaxDim)
            : null;
        bool? blur = look.ValueKind == JsonValueKind.Object && look.TryGetProperty("blur", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? b.GetBoolean()
            : null;
        return new ShellScreenLook(
            new ShellLogo(Bool(logo, "show"), position, size, Bool(logo, "name")),
            Text(look, "wallpaperUrl") ?? fallbackWallpaper,
            dim,
            blur,
            Clock: Bool(look, "clock"),
            Language: Bool(look, "language"),
            Prices: Bool(look, "prices"),
            FreeBadge: Bool(look, "freeBadge"),
            Title: LocalText(look, "title"),
            Subtitle: LocalText(look, "subtitle"),
            Cta: LocalText(look, "cta"));
    }

    private static ShellText? LocalText(JsonElement look, string name)
    {
        if (look.ValueKind != JsonValueKind.Object || !look.TryGetProperty(name, out var t) || t.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var text = new ShellText(Text(t, "ru")?.Trim(), Text(t, "uz")?.Trim(), Text(t, "en")?.Trim());
        return text is { Ru: null or "", Uz: null or "", En: null or "" } ? null : text;
    }

    /// <summary>Every switch is on unless the owner turned it off.</summary>
    private static bool Bool(JsonElement e, string name) =>
        !(e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.False);

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text
            ? text
            : null;
}
