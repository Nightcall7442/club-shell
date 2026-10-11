using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Server.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

namespace ClubShell.Server.Auth;

/// <summary>
/// Agent request signature (DESIGN §3.3, contract base.yaml §3):
/// <c>lowercase hex(HMAC-SHA256(secret, X-Timestamp + METHOD + raw request-target + hex(SHA256(body))))</c>.
/// Mandatory for agent and user modes; there is no log-only mode. A re-registration may carry one as proof of the PC's
/// signing secret (D-78): <see cref="ReadAsync"/> runs in the middleware, <see cref="Check"/> once the HWID names the PC.
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
        var signed = await ReadAsync(context);
        return Check(signed, secret, window, now) switch
        {
            SignatureCheck.Ok => (signed.Timestamp, signed.Signature),
            SignatureCheck.Missing => throw ApiException.Unauthorized("signature", "Missing request signature", "missing"),
            SignatureCheck.Malformed => throw ApiException.Unauthorized("signature", "Malformed X-Timestamp", "mismatch"),

            // X-Server-Time is on every response (ApiErrorMiddleware): the agent learns its offset and retries once.
            SignatureCheck.ClockSkew => throw ApiException.Unauthorized("clockSkew", "Request timestamp outside the allowed window"),
            _ => throw ApiException.Unauthorized("signature", "Request signature mismatch", "mismatch"),
        };
    }

    /// <summary>
    /// The signature headers, method, raw request-target and SHA-256 of the body. The body is buffered and rewound for the
    /// endpoint.
    /// </summary>
    public static async Task<SignedRequest> ReadAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        var bodyHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(context.Request.Body, context.RequestAborted));
        context.Request.Body.Position = 0;
        return new SignedRequest(
            context.Request.Headers[TimestampHeader].ToString(),
            context.Request.Headers[SignatureHeader].ToString(),
            context.Request.Method,
            RawTarget(context),
            bodyHash);
    }

    /// <summary>Checks <paramref name="request"/> against <paramref name="secret"/> within ±<paramref name="window"/> of <paramref name="now"/>.</summary>
    public static SignatureCheck Check(SignedRequest request, ReadOnlySpan<byte> secret, TimeSpan window, DateTimeOffset now)
    {
        if (request.Timestamp.Length == 0 || request.Signature.Length == 0)
        {
            return SignatureCheck.Missing;
        }

        if (!long.TryParse(request.Timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return SignatureCheck.Malformed;
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > window.TotalSeconds)
        {
            return SignatureCheck.ClockSkew;
        }

        var expected = Convert.FromHexString(Compute(secret, request.Timestamp, request.Method, request.Target, request.BodySha256));
        var actual = request.Signature.Length == 64 && IsHex(request.Signature) ? Convert.FromHexString(request.Signature) : [];
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? SignatureCheck.Ok : SignatureCheck.Mismatch;
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

/// <summary>What <see cref="RequestSignature.Check"/> needs of a request, read once by <see cref="RequestSignature.ReadAsync"/>.</summary>
public sealed record SignedRequest(string Timestamp, string Signature, string Method, string Target, string BodySha256);

/// <summary>Outcome of <see cref="RequestSignature.Check"/>.</summary>
public enum SignatureCheck
{
    Ok,

    /// <summary><c>X-Timestamp</c> or <c>X-Signature</c> absent or empty.</summary>
    Missing,

    /// <summary><c>X-Timestamp</c> is not whole seconds.</summary>
    Malformed,

    /// <summary>Outside the window.</summary>
    ClockSkew,

    /// <summary>Another secret, or signed over another method, target or body.</summary>
    Mismatch,
}
