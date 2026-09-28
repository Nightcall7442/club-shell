using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Server.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

namespace ClubShell.Server.Auth;

/// <summary>
/// Agent request signature (DESIGN §3.3, contract base.yaml §3):
/// <c>lowercase hex(HMAC-SHA256(secret, X-Timestamp + METHOD + raw request-target + hex(SHA256(body))))</c>.
/// Mandatory for agent and user modes; there is no log-only mode.
/// </summary>
public static class RequestSignature
{
    public const string TimestampHeader = "X-Timestamp";
    public const string SignatureHeader = "X-Signature";

    public static string Compute(ReadOnlySpan<byte> secret, string timestamp, string method, string target, string bodySha256Hex) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(timestamp + method.ToUpperInvariant() + target + bodySha256Hex)));

    /// <summary>
    /// Verifies the request against <paramref name="secret"/>; throws <c>401 signature</c> (<c>missing</c> | <c>mismatch</c>)
    /// or <c>401 clockSkew</c>. The body is buffered and rewound for the endpoint. Returns the replay tuple parts.
    /// </summary>
    public static async Task<(string Timestamp, string Signature)> VerifyAsync(HttpContext context, byte[] secret, TimeSpan window, DateTimeOffset now)
    {
        var timestamp = context.Request.Headers[TimestampHeader].ToString();
        var signature = context.Request.Headers[SignatureHeader].ToString();
        if (timestamp.Length == 0 || signature.Length == 0)
        {
            throw ApiException.Unauthorized("signature", "Missing request signature", "missing");
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            throw ApiException.Unauthorized("signature", "Malformed X-Timestamp", "mismatch");
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > window.TotalSeconds)
        {
            // X-Server-Time is on every response (ApiErrorMiddleware): the agent learns its offset and retries once.
            throw ApiException.Unauthorized("clockSkew", "Request timestamp outside the allowed window");
        }

        context.Request.EnableBuffering();
        var bodyHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(context.Request.Body, context.RequestAborted));
        context.Request.Body.Position = 0;

        var expected = Convert.FromHexString(Compute(secret, timestamp, context.Request.Method, RawTarget(context), bodyHash));
        var actual = signature.Length == 64 && IsHex(signature) ? Convert.FromHexString(signature) : [];
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw ApiException.Unauthorized("signature", "Request signature mismatch", "mismatch");
        }

        return (timestamp, signature);
    }

    /// <summary>
    /// The request-target exactly as sent (<c>IHttpRequestFeature.RawTarget</c>): with <c>/api/v1</c> and query, no
    /// decoding. Absolute-form (via a proxy) is reduced to path and query; a transport without RawTarget rebuilds it.
    /// </summary>
    public static string RawTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw))
        {
            return context.Request.PathBase.ToUriComponent() + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
        }

        if (!raw.StartsWith('/') && Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
        {
            var authority = absolute.GetLeftPart(UriPartial.Authority);
            return raw.StartsWith(authority, StringComparison.OrdinalIgnoreCase) ? raw[authority.Length..] : absolute.PathAndQuery;
        }

        return raw;
    }

    private static bool IsHex(string value) => value.All(char.IsAsciiHexDigit);
}
