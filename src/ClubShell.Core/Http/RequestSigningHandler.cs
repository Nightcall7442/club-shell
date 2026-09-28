using System.Globalization;
using ClubShell.Core.Security;

namespace ClubShell.Core.Http;

/// <summary>
/// Signs every attempt of a request that carries a <see cref="RequestSigner"/> in <see cref="SignerOption"/>
/// (SERVER_API.md §2.2): <c>X-Timestamp</c> and <c>X-Signature</c> over the method, path and the SHA-256 of the
/// (buffered) body. Registered inside the resilience pipeline, so each retry gets a fresh signature and does not trip the
/// server's replay check on (<c>pcId</c>, <c>X-Timestamp</c>, <c>X-Signature</c>).
/// </summary>
public sealed class RequestSigningHandler : DelegatingHandler
{
    /// <summary>Request option set by <see cref="ServerClient"/> on signed (agent/user) calls.</summary>
    public static readonly HttpRequestOptionsKey<RequestSigner> SignerOption = new("ClubShell.RequestSigner");

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options.TryGetValue(SignerOption, out var signer))
        {
            var bodyHash = request.Content is null
                ? Signing.EmptyBodySha256
                : Signing.Sha256Hex(await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));

            // X-Timestamp is whole seconds: a retry within the same second would repeat the previous attempt's
            // (timestamp, signature) pair, so every attempt moves at least one second past the previous one.
            var timestamp = signer.Now().ToUnixTimeSeconds();
            if (request.Headers.TryGetValues(Signing.TimestampHeader, out var sent)
                && long.TryParse(sent.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var previous))
            {
                timestamp = Math.Max(timestamp, previous + 1);
            }

            request.Headers.Remove(Signing.TimestampHeader);
            request.Headers.Remove(Signing.SignatureHeader);
            request.Headers.Add(Signing.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(Signing.SignatureHeader, Signing.Sign(signer.Secret, timestamp, request.Method.Method, request.RequestUri!.PathAndQuery, bodyHash));
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>What <see cref="RequestSigningHandler"/> needs to sign a request.</summary>
/// <param name="Secret">HMAC key (decoded <c>signingSecret</c>), taken with the Bearer token it belongs to.</param>
/// <param name="Now">Server-corrected clock (<see cref="ServerClient.ServerNow"/>), read on every attempt.</param>
public sealed record RequestSigner(byte[] Secret, Func<DateTimeOffset> Now);
