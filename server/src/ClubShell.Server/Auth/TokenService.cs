using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ClubShell.Server.Auth;

/// <summary><c>Auth:*</c> (DESIGN §2.5).</summary>
public sealed class AuthOptions
{
    public string Issuer { get; set; } = "clubshell-server";

    public string Audience { get; set; } = "club-agent";

    /// <summary>PKCS#8 PEM, RSA-3072, created on first start; relative paths resolve against the content root.</summary>
    public string SigningKeyPath { get; set; } = "data/jwt-signing-key.pem";

    public int AgentTokenMinutes { get; set; } = 60;

    /// <summary>Lifetime of an agent refresh token, days.</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Lifetime of a player token, hours: the limit of one continuous sign-in (N3, DESIGN §3.4). There is no player refresh.</summary>
    public int UserTokenHours { get; set; } = 12;

    /// <summary>Request signature window, seconds (contract: ±300).</summary>
    public int SignatureWindowSec { get; set; } = 300;
}

/// <summary>
/// Agent access JWT, RS256 (DESIGN §3.2; port of club-server <c>Auth/TokenService.cs</c>). The key lives in a PEM file
/// and survives restarts; rotating it (replace the file, restart) makes every access token <c>invalid</c>, and agents
/// refresh once. Lifetime is checked against <see cref="TimeProvider"/>.
/// </summary>
public sealed class TokenService
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);
    private readonly AuthOptions _options;
    private readonly TimeProvider _clock;
    private readonly RsaSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public TokenService(AuthOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
        _key = LoadOrCreateKey(options.SigningKeyPath);
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Guid pcId, Guid clubId, string hwid, int credentialsVersion)
    {
        var now = _clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AgentTokenMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = pcId.ToString(),
                ["club"] = clubId.ToString(),
                ["hwid"] = hwid,
                ["role"] = "agent",
                ["cv"] = credentialsVersion,
                ["jti"] = Guid.NewGuid().ToString(),
            },
        });
        return (token, expires);
    }

    /// <summary>Opaque refresh token: 32 random bytes, base64url. Only its SHA-256 is stored (DESIGN §3.2).</summary>
    public static string NewRefreshToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] HashRefreshToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>The principal, or <c>null</c> and the 401 reason (<c>expired</c> | <c>invalid</c>).</summary>
    public async Task<(AgentPrincipal? Principal, string? Reason)> ValidateAsync(string token)
    {
        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKey = _key,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore <= now + Skew) && expires > now - Skew;
            },
        });

        if (!result.IsValid)
        {
            return (null, result.Exception is SecurityTokenInvalidLifetimeException or SecurityTokenExpiredException ? "expired" : "invalid");
        }

        var claims = result.ClaimsIdentity;
        if (claims.FindFirst("role")?.Value != "agent"
            || !Guid.TryParse(claims.FindFirst("sub")?.Value, out var pcId)
            || !Guid.TryParse(claims.FindFirst("club")?.Value, out var clubId)
            || !int.TryParse(claims.FindFirst("cv")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var cv)
            || result.SecurityToken is not JsonWebToken jwt)
        {
            return (null, "invalid");
        }

        return (new AgentPrincipal(pcId, clubId, claims.FindFirst("hwid")?.Value ?? "", cv, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero)), null);
    }

    /// <summary>kid = first 16 hex of sha256(modulus): shows in logs which key signed a token.</summary>
    private static RsaSecurityKey LoadOrCreateKey(string path)
    {
        var rsa = RSA.Create();
        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            rsa.KeySize = 3072;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            // Created as 600 in one step (no world-readable window) and never over a key another process just wrote.
            var file = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                file.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            try
            {
                using var stream = new FileStream(path, file);
                stream.Write(Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem()));
            }
            catch (IOException) when (File.Exists(path))
            {
                rsa.ImportFromPem(File.ReadAllText(path));
            }
        }

        var modulus = rsa.ExportParameters(includePrivateParameters: false).Modulus!;
        return new RsaSecurityKey(rsa) { KeyId = Convert.ToHexStringLower(SHA256.HashData(modulus))[..16] };
    }
}
