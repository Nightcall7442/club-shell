namespace ClubShell.Server.Infrastructure;

/// <summary>
/// Page normalization (contract base.yaml §7, DESIGN §7.4): the agent forwards shell values unchecked, so the server
/// never answers 400 here. Numbers are read like the mock's <c>parseInt</c> (<c>db.ts</c> <c>paginate</c>): leading
/// whitespace, sign and digits, the rest ignored ("1.5" → 1). <c>page</c> &lt; 1 or not a number → 1; <c>pageSize</c>
/// 0 or not a number → 50; &lt; 0 → 1; above <paramref name="max"/> (however large) → max. The response echoes the
/// applied values.
/// </summary>
public static class Paging
{
    public const int DefaultPageSize = 50;

    public static (int Page, int PageSize) Normalize(string? page, string? pageSize, int max = 200)
    {
        var p = Math.Max(ParseInt(page) ?? 1, 1);
        var size = ParseInt(pageSize) ?? 0;
        size = size switch
        {
            0 => DefaultPageSize,
            < 0 => 1,
            _ => size,
        };
        return (p, Math.Min(size, max));
    }

    /// <summary>JS <c>parseInt(s, 10)</c>, saturated to the int range instead of overflowing; null when no digits lead.</summary>
    private static int? ParseInt(string? s)
    {
        var span = s.AsSpan().TrimStart();
        var negative = span.StartsWith("-");
        if (negative || span.StartsWith("+"))
        {
            span = span[1..];
        }

        var digits = 0;
        long value = 0;
        while (digits < span.Length && char.IsAsciiDigit(span[digits]))
        {
            value = Math.Min(value * 10 + (span[digits++] - '0'), int.MaxValue);
        }

        return digits == 0 ? null : (int)(negative ? -value : value);
    }
}
