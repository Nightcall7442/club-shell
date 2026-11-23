using System.Globalization;
using System.Text;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Users;

/// <summary>
/// Player login names (D-92): a typed one is trimmed, 3–32 characters and stored lower-cased in any script (PostgreSQL's
/// <c>lower()</c> under the C locale folds only ASCII); one that reads as a phone number is stored as its digits, so the
/// player signs in with the number however they type it (sign-in matches a typed phone by its digits too). Without one the server makes a
/// readable Latin login: the phone's digits when given and free, else the name transliterated (Russian and Uzbek
/// Cyrillic, Latin diacritics dropped) with the smallest number appended that makes it unique in the network
/// (<c>ivan.petrov</c>, <c>ivan.petrov2</c>, …). Unique case-insensitively per network (<c>users_username</c>), guests and
/// deleted accounts included.
/// </summary>
public static class Logins
{
    public const int Min = 3;
    public const int Max = 32;

    /// <summary>The name part of a generated login: room for the digits a collision appends.</summary>
    private const int BaseMax = 24;

    /// <summary>A typed login as stored: <c>400 username required|min|max</c>; a phone-shaped one becomes its digits.</summary>
    public static string Typed(string? typed)
    {
        var trimmed = typed?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw ApiException.Validation("username", "required");
        }

        if (PhoneDigits(trimmed) is { } digits)
        {
            return digits;
        }

        if (trimmed.Length > Max)
        {
            throw ApiException.Validation("username", "max");
        }

        if (trimmed.Length < Min)
        {
            throw ApiException.Validation("username", "min");
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// The digits of a text that reads as a phone number — digits with spaces and <c>+ - ( )</c>, 7–15 digits (E.164 at
    /// most) — else null. A login of bare digits stays itself.
    /// </summary>
    public static string? PhoneDigits(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0 || !t.All(ch => char.IsAsciiDigit(ch) || ch is ' ' or '+' or '-' or '(' or ')'))
        {
            return null;
        }

        var digits = string.Concat(t.Where(char.IsAsciiDigit));
        return digits.Length is >= 7 and <= 15 ? digits : null;
    }

    /// <summary><c>400 username taken</c> when the network already has <paramref name="username"/> (any case).</summary>
    public static async Task EnsureFreeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid networkId, string username)
    {
        if (await c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM users WHERE network_id = @networkId AND lower(username) = @username)", new { networkId, username }, tx))
        {
            throw ApiException.Validation("username", "taken");
        }
    }

    /// <summary>
    /// A free login for a new player of <paramref name="networkId"/>, in the caller's transaction: the phone's digits when
    /// given and free, else <see cref="Slug"/> of the name, numbered from 2 on a collision (from 1 when the name gives fewer
    /// than 3 letters). Generations from one base are serialized by a transaction advisory lock, so two cashiers
    /// registering two «Иван» at once get <c>ivan</c> and <c>ivan2</c>, not a <c>taken</c>.
    /// </summary>
    public static async Task<string> GenerateAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid networkId, string displayName, string? phone)
    {
        if (PhoneDigits(phone) is { } digits)
        {
            await LockAsync(c, tx, networkId, digits);
            if (!await TakenAsync(c, tx, networkId, digits))
            {
                return digits;
            }
        }

        var slug = Slug(displayName);
        await LockAsync(c, tx, networkId, slug);
        var taken = (await c.QueryAsync<string>(
                "SELECT lower(username) FROM users WHERE network_id = @networkId AND lower(username) LIKE @prefix",
                new { networkId, prefix = slug + "%" }, tx))
            .ToHashSet(StringComparer.Ordinal);
        if (slug.Length >= Min && !taken.Contains(slug))
        {
            return slug;
        }

        for (var n = slug.Length >= Min ? 2 : 1; ; n++)
        {
            var candidate = slug + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// The name in lower-case Latin letters and digits: Russian and Uzbek Cyrillic transliterated (passport style:
    /// ж → zh, х → kh, ц → ts, ю → yu; ў → o, қ → q, ғ → g, ҳ → h), Latin diacritics and Uzbek apostrophes dropped, the
    /// first two words joined by a dot, at most 24 characters; <c>player</c> when nothing Latin is left (a name in
    /// another script).
    /// </summary>
    public static string Slug(string displayName)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var ch in displayName.ToLowerInvariant())
        {
            // Cyrillic first: decomposed, ў would read as у + breve and lose its own letter.
            if (Cyrillic(ch) is { } latin)
            {
                word.Append(latin);
                continue;
            }

            if (ch is '\'' or 'ʻ' or 'ʼ' or '’' or '‘' or '`' || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue; // oʻ, gʻ → o, g; a mark typed apart from its letter
            }

            var letter = false;
            foreach (var part in ch.ToString().Normalize(NormalizationForm.FormD))
            {
                if (part is >= 'a' and <= 'z' or >= '0' and <= '9')
                {
                    word.Append(part); // é → e, ş → s
                    letter = true;
                }
            }

            if (!letter && word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        var slug = string.Join('.', words.Take(2));
        if (slug.Length > BaseMax)
        {
            slug = slug[..BaseMax].TrimEnd('.');
        }

        return slug.Length == 0 ? "player" : slug;
    }

    /// <summary>One lower-case Cyrillic letter in Latin (<c>ё</c> as <c>e</c>, as most people type it); null for anything else.</summary>
    private static string? Cyrillic(char ch) => ch switch
    {
        'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'е' => "e", 'ё' => "e", 'ж' => "zh", 'з' => "z", 'и' => "i", 'й' => "y",
        'к' => "k", 'л' => "l", 'м' => "m", 'н' => "n", 'о' => "o", 'п' => "p", 'р' => "r", 'с' => "s", 'т' => "t", 'у' => "u",
        'ф' => "f", 'х' => "kh", 'ц' => "ts", 'ч' => "ch", 'ш' => "sh", 'щ' => "shch", 'ъ' => "", 'ы' => "y", 'ь' => "",
        'э' => "e", 'ю' => "yu", 'я' => "ya", 'і' => "i", 'ї' => "i", 'є' => "e",
        'ў' => "o", 'қ' => "q", 'ғ' => "g", 'ҳ' => "h",
        _ => null,
    };

    private static Task LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid networkId, string name) =>
        c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('login/' || @networkId::text || '/' || @name, 0))", new { networkId, name }, tx);

    private static Task<bool> TakenAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid networkId, string username) =>
        c.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM users WHERE network_id = @networkId AND lower(username) = @username)", new { networkId, username }, tx);
}
