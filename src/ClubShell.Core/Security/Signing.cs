using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Core.Abstractions;

namespace ClubShell.Core.Security;

/// <summary>
/// Cryptographic helpers: HMAC-SHA256 request signing (SERVER_API.md §2.2), SHA-256 hashing, RSA-PSS verification of
/// update packages / manifests (ARCHITECTURE.md §3, §11) and account-pool secret decryption (SERVER_API.md §4.6).
/// </summary>
public static class Signing
{
    /// <summary>SHA-256 of zero bytes, the <c>BODY_HASH</c> of an empty request body.</summary>
    public const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Request header carrying the Unix-seconds timestamp.</summary>
    public const string TimestampHeader = "X-Timestamp";

    /// <summary>Request header carrying the signature.</summary>
    public const string SignatureHeader = "X-Signature";

    /// <summary>Response header with the server clock, returned on <c>clockSkew</c> rejections.</summary>
    public const string ServerTimeHeader = "X-Server-Time";

    /// <summary>HKDF <c>info</c> for the account-pool key.</summary>
    public const string AccountPoolInfo = "account-pool";

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string HexAlphabet = "0123456789abcdef";
    private const int GcmNonceBytes = 12;
    private const int GcmTagBytes = 16;

    /// <summary>Accepted clock skew when verifying signatures (±5 min).</summary>
    public static TimeSpan DefaultSkew { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Lower-case hex encoding.</summary>
    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length * 2];
        for (var i = 0; i < data.Length; i++)
        {
            chars[2 * i] = HexAlphabet[data[i] >> 4];
            chars[(2 * i) + 1] = HexAlphabet[data[i] & 0xF];
        }

        return new string(chars);
    }

    /// <summary>Lower-case hex SHA-256 of <paramref name="data"/>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) => ToHex(SHA256.HashData(data));

    /// <summary>Lower-case hex SHA-256 of a file, streamed.</summary>
    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            return ToHex(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>Decodes a base64 signing secret (<c>signingSecret</c> of the register/refresh responses).</summary>
    public static byte[] DecodeSecret(string secretBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretBase64);
        return Convert.FromBase64String(secretBase64);
    }

    /// <summary>The signed message: <c>X-Timestamp + METHOD + PATH + BODY_HASH</c>, no separators.</summary>
    public static string BuildSigningString(long unixSeconds, string method, string pathAndQuery, string bodySha256Hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentException.ThrowIfNullOrEmpty(pathAndQuery);
        ArgumentException.ThrowIfNullOrEmpty(bodySha256Hex);
        return string.Concat(unixSeconds.ToString(CultureInfo.InvariantCulture), method.ToUpperInvariant(), pathAndQuery, bodySha256Hex);
    }

    /// <summary>
    /// Computes <c>X-Signature</c>: lower-case hex <c>HMAC-SHA256(secret, timestamp + METHOD + PATH + BODY_HASH)</c>.
    /// <paramref name="pathAndQuery"/> is the path including query, starting with <c>/api/v1</c>, exactly as sent.
    /// </summary>
    public static string Sign(ReadOnlySpan<byte> secret, long unixSeconds, string method, string pathAndQuery, string bodySha256Hex)
    {
        var message = Encoding.UTF8.GetBytes(BuildSigningString(unixSeconds, method, pathAndQuery, bodySha256Hex));
        return ToHex(HMACSHA256.HashData(secret, message));
    }

    /// <summary>
    /// Verifies a signature produced by <see cref="Sign"/> with a constant-time comparison and a
    /// <paramref name="skew"/> window (default ±5 min) around <paramref name="clock"/>.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> secret, string signatureHex, long unixSeconds, string method, string pathAndQuery, string bodySha256Hex, IClock clock, TimeSpan? skew = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (string.IsNullOrEmpty(signatureHex))
        {
            return false;
        }

        var window = skew ?? DefaultSkew;
        var now = clock.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - unixSeconds) > window.TotalSeconds)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(secret, unixSeconds, method, pathAndQuery, bodySha256Hex).ToUpperInvariant());
        var actual = Encoding.ASCII.GetBytes(signatureHex.Trim().ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>
    /// Canonical JSON signed for manifest-level signatures: <c>{"component","version","url","sha256","size","publishedAt"}</c>
    /// in that order, no whitespace, UTF-8.
    /// </summary>
    public static byte[] ManifestCanonicalBytes(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("component", JsonNamingPolicy.CamelCase.ConvertName(manifest.Component.ToString()));
            writer.WriteString("version", manifest.Version);
            writer.WriteString("url", manifest.Url);
            writer.WriteString("sha256", manifest.Sha256);
            writer.WriteNumber("size", manifest.Size);
            writer.WriteString("publishedAt", manifest.PublishedAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Verifies <see cref="UpdateManifest.Signature"/> (base64 RSA-PSS-SHA256) over <see cref="ManifestCanonicalBytes"/>.</summary>
    public static bool VerifyManifest(UpdateManifest manifest, RSA publicKey) =>
        VerifySignature(ManifestCanonicalBytes(manifest), manifest.Signature, publicKey);

    /// <summary>Verifies a base64 RSA-PSS-SHA256 signature over <paramref name="data"/>. Malformed base64 counts as invalid.</summary>
    public static bool VerifySignature(ReadOnlySpan<byte> data, string signatureBase64, RSA publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (!TryDecodeBase64(signatureBase64, out var signature))
        {
            return false;
        }

        return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    /// <summary>Verifies a base64 RSA-PSS-SHA256 signature over the raw bytes of a file (<see cref="UpdateManifest.Signature"/> semantics).</summary>
    public static async Task<bool> VerifyFileSignatureAsync(string path, string signatureBase64, RSA publicKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(publicKey);
        if (!TryDecodeBase64(signatureBase64, out var signature))
        {
            return false;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            return await Task.Run(() => publicKey.VerifyData(stream, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Loads an RSA public key from PEM text: <c>PUBLIC KEY</c>, <c>RSA PUBLIC KEY</c> or an X.509 <c>CERTIFICATE</c>.</summary>
    public static RSA LoadRsaPublicKey(string pem)
    {
        ArgumentException.ThrowIfNullOrEmpty(pem);
        if (pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return certificate.GetRSAPublicKey() ?? throw new CryptographicException("Certificate does not contain an RSA public key");
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>Loads an RSA public key from a PEM file (<c>updates.publicKeyPath</c>).</summary>
    public static async Task<RSA> LoadRsaPublicKeyFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var pem = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return LoadRsaPublicKey(pem);
    }

    /// <summary>Derives the 32-byte AES key used for account-pool secrets: <c>HKDF-SHA256(signingSecret, info = "account-pool")</c>.</summary>
    public static byte[] DeriveAccountPoolKey(byte[] signingSecret)
    {
        ArgumentNullException.ThrowIfNull(signingSecret);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, signingSecret, 32, null, Encoding.ASCII.GetBytes(AccountPoolInfo));
    }

    /// <summary>Decrypts an account-pool <c>secret</c>: base64 <c>nonce(12) || ciphertext || tag(16)</c>, AES-256-GCM with <see cref="DeriveAccountPoolKey"/>.</summary>
    public static string DecryptAccountPoolSecret(byte[] signingSecret, string payloadBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(payloadBase64);
        var payload = Convert.FromBase64String(payloadBase64);
        if (payload.Length < GcmNonceBytes + GcmTagBytes)
        {
            throw new CryptographicException("Account-pool payload too short");
        }

        var key = DeriveAccountPoolKey(signingSecret);
        try
        {
            var nonce = payload.AsSpan(0, GcmNonceBytes);
            var tag = payload.AsSpan(payload.Length - GcmTagBytes, GcmTagBytes);
            var ciphertext = payload.AsSpan(GcmNonceBytes, payload.Length - GcmNonceBytes - GcmTagBytes);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, GcmTagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool TryDecodeBase64(string? value, out byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        var buffer = new byte[value.Length];
        if (Convert.TryFromBase64String(value.Trim(), buffer, out var written))
        {
            bytes = buffer.AsSpan(0, written).ToArray();
            return true;
        }

        bytes = Array.Empty<byte>();
        return false;
    }
}
