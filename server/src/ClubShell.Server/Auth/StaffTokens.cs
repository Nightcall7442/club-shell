using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Auth;

/// <summary>
/// Staff sign-in and tokens (DESIGN §3.5). A PIN is stored as <c>HMAC-SHA256(pepper, pin)</c>: deterministic, so the login
/// finds the one staff row by index (<c>UNIQUE(network_id, pin_hmac)</c>). The pepper is 32 random bytes in
/// <see cref="AuthOptions.PepperPath"/>, created on first start; without it every PIN is lost. A token is 32 random bytes
/// (<c>st_</c> + base64url), only its SHA-256 is stored; it lives <see cref="AuthOptions.StaffTokenSlidingHours"/> since its
/// last use and at most <see cref="AuthOptions.StaffTokenAbsoluteDays"/>, and dies with logout or the staff member's
/// deactivation (checked on every request). <c>ck_…</c> is the owner (synthetic "API key"), compared in
/// constant time with <c>clubs.api_key_hash</c> and never revoked by logout. Wrong PINs are limited by
/// <see cref="StaffLoginGuard"/>.
/// </summary>
public sealed class StaffTokens(NpgsqlDataSource db, AuthOptions options, TimeProvider clock, StaffLoginGuard guard, ILogger<StaffTokens> logger)
{
    public const string ApiKeyName = "API key";

    private readonly Lazy<byte[]> _pepper = new(() => LoadOrCreatePepper(options.PepperPath));

    public byte[] PinHmac(string pin) => HMACSHA256.HashData(_pepper.Value, Encoding.UTF8.GetBytes(pin));

    /// <summary>
    /// The network source of a PIN attempt (<see cref="StaffLoginGuard"/>): an IPv4 address (also when mapped into IPv6), or
    /// the /64 of an IPv6 one — a single subscriber usually holds a whole /64 and could otherwise rotate addresses; "" when
    /// unknown.
    /// </summary>
    public static string PinLimitKey(IPAddress? ip)
    {
        if (ip is null)
        {
            return "";
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return ip.ToString();
        }

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>
    /// The active staff member of <paramref name="pin"/> with a new token, or null for a wrong PIN; <c>429 rateLimited</c>
    /// when a limit of <see cref="StaffLoginGuard"/> is full for this attempt's source (the browser of
    /// <paramref name="deviceId"/> when the club trusts it, else <paramref name="client"/>). The limits are checked before and
    /// under the club's lock, and the lookup with the failure, or with the token and the trusted browser, is one transaction:
    /// parallel requests cannot all pass the check before a failure is recorded, and a restart forgets nothing (D-76).
    /// </summary>
    /// <remarks>
    /// The PIN is unique only inside its network, so it is looked up in one club: the one of <paramref name="clubCode"/>, or
    /// the only club of the server when no code is sent (400 <c>clubCode required</c> once there are more, counting nothing).
    /// An unknown code or a disabled club is a wrong PIN: it counts as an attempt and does not tell which codes exist.
    /// </remarks>
    public async Task<(string Token, StaffContext Staff)?> LoginAsync(string pin, string? clubCode, IPAddress? client, string? deviceId)
    {
        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        var code = string.IsNullOrWhiteSpace(clubCode) ? null : clubCode.Trim().ToUpperInvariant();
        var clubs = (await c.QueryAsync<(Guid Id, bool Disabled)>(
            "SELECT id, disabled FROM clubs WHERE @code IS NULL OR code = @code ORDER BY created_at, id LIMIT 2", new { code })).ToList();
        if (code is null && clubs.Count > 1)
        {
            throw ApiException.Validation("clubCode", "required", "This server holds several clubs: send the club code with the PIN");
        }

        var clubId = clubs is [{ Disabled: false } club] ? club.Id : Guid.Empty;
        var source = await guard.SourceAsync(c, clubId, client, deviceId, now);
        await guard.CheckAsync(c, null, source, now);
        await using var tx = await c.BeginTransactionAsync();
        await StaffLoginGuard.LockAsync(c, tx, clubId);
        await guard.CheckAsync(c, tx, source, now);
        var staff = clubId == Guid.Empty
            ? null
            : await c.QuerySingleOrDefaultAsync<StaffRow>(
                $"{StaffRow.Select} WHERE s.club_id = @clubId AND s.pin_hmac = @hmac AND s.active AND NOT cl.disabled",
                new { clubId, hmac = PinHmac(pin) }, tx);
        if (staff is null)
        {
            await guard.FailAsync(c, tx, source, now);
            await tx.CommitAsync();

            // Security event (DESIGN §3.7); the PIN itself is never logged.
            logger.LogWarning("Wrong staff PIN from {Ip} for club {ClubId}", client, clubId);
            return null;
        }

        var token = "st_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await c.ExecuteAsync(
            """
            INSERT INTO staff_tokens (token_hash, staff_id, club_id, created_at, last_used_at, expires_at)
            VALUES (@hash, @Id, @ClubId, @now, @now, @expires)
            """,
            new { hash = Hash(token), staff.Id, staff.ClubId, now, expires = now.AddDays(options.StaffTokenAbsoluteDays) }, tx);
        await StaffLoginGuard.TrustAsync(c, tx, source, staff.Id, now);
        await tx.CommitAsync();
        logger.LogInformation("Staff {StaffId} of club {ClubId} signed in from {Ip} (trusted browser: {Trusted})", staff.Id, staff.ClubId, client, source.Trusted);
        return (token, staff.ToContext());
    }

    /// <summary>The staff member of a live token or of the club API key; null when neither.</summary>
    public async Task<StaffContext?> ValidateAsync(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        await using var c = await db.OpenConnectionAsync();
        if (token.StartsWith("ck_", StringComparison.Ordinal))
        {
            var clubs = await c.QueryAsync<(Guid Id, Guid NetworkId, byte[] Hash)>("SELECT id, network_id, api_key_hash FROM clubs WHERE api_key_hash IS NOT NULL AND NOT disabled");
            var presented = ApiKeyHash(token);
            StaffContext? found = null;
            foreach (var club in clubs)
            {
                // Every club is compared: no early exit that would time which one matched.
                if (CryptographicOperations.FixedTimeEquals(presented, club.Hash))
                {
                    found ??= new StaffContext(null, ApiKeyName, "owner", club.Id, club.NetworkId);
                }
            }

            return found;
        }

        var now = clock.GetUtcNow();
        var staff = await c.QuerySingleOrDefaultAsync<StaffRow>(
            $"""
            UPDATE staff_tokens t SET last_used_at = @now
            FROM staff s JOIN clubs cl ON cl.id = s.club_id
            WHERE t.token_hash = @hash AND s.id = t.staff_id AND s.active AND NOT cl.disabled AND t.revoked_at IS NULL
              AND t.expires_at > @now AND t.last_used_at > @idle
            RETURNING s.id, s.name, s.role, s.club_id, cl.network_id
            """,
            new { hash = Hash(token), now, idle = now.AddHours(-options.StaffTokenSlidingHours) });
        return staff?.ToContext();
    }

    /// <summary>Revokes a PIN token; an unknown or already revoked one (and <c>ck_</c>) is a no-op.</summary>
    public async Task RevokeAsync(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE staff_tokens SET revoked_at = @now WHERE token_hash = @hash AND revoked_at IS NULL", new { hash = Hash(token), now = clock.GetUtcNow() });
    }

    /// <summary>
    /// First start with an empty <c>staff</c> table: the owner is created with <paramref name="ownerPin"/> (<c>Club:OwnerPin</c>),
    /// or with a generated 6-digit PIN written to the log once.
    /// </summary>
    public async Task EnsureOwnerAsync(string? ownerPin)
    {
        await using var c = await db.OpenConnectionAsync();
        if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM staff)"))
        {
            return;
        }

        var pin = string.IsNullOrEmpty(ownerPin) ? RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture) : ownerPin;
        await c.ExecuteAsync(
            """
            INSERT INTO staff (id, network_id, club_id, name, role, pin_hmac)
            SELECT @id, network_id, id, 'Владелец', 'owner', @hmac FROM clubs ORDER BY created_at, id LIMIT 1
            ON CONFLICT DO NOTHING
            """,
            new { id = Guid.CreateVersion7(clock.GetUtcNow()), hmac = PinHmac(pin) });
        if (string.IsNullOrEmpty(ownerPin))
        {
            logger.LogWarning("No staff yet: owner created with PIN {Pin}; change it in the console", pin);
        }
    }

    /// <summary>
    /// The club API key (<c>adminApiKey</c>): the current <c>ck_&lt;32 hex&gt;</c>, created on first read (a fresh club has
    /// none; the mock creates one with the club). Runs in the caller's transaction, which must hold the <c>clubs</c> row.
    /// </summary>
    public async Task<string> CurrentApiKeyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId)
    {
        var sealedKey = await c.ExecuteScalarAsync<byte[]?>("SELECT api_key_sealed FROM clubs WHERE id = @clubId", new { clubId }, tx);
        return sealedKey is null ? await RotateApiKeyAsync(c, tx, clubId) : Unseal(sealedKey);
    }

    /// <summary>
    /// A new club API key (<c>adminRotateApiKey</c>); the previous one stops at this commit, without a grace period
    /// (D-6). Stored as an HMAC under the pepper for <see cref="ValidateAsync"/> and sealed (AES-GCM, key derived from the
    /// pepper) for <see cref="CurrentApiKeyAsync"/>: never in plain text, never logged.
    /// </summary>
    public async Task<string> RotateApiKeyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId)
    {
        var key = "ck_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await c.ExecuteAsync(
            "UPDATE clubs SET api_key_hash = @hash, api_key_sealed = @sealed, updated_at = @now WHERE id = @clubId",
            new { clubId, hash = ApiKeyHash(key), @sealed = Seal(key), now = clock.GetUtcNow() },
            tx);
        return key;
    }

    private byte[] ApiKeyHash(string key) => HMACSHA256.HashData(_pepper.Value, Encoding.UTF8.GetBytes("api-key:" + key));

    /// <summary>The AES-256 key of the sealed API key: derived from the pepper, so a database dump alone does not reveal it.</summary>
    private byte[] SealingKey() => HMACSHA256.HashData(_pepper.Value, "clubshell/api-key/seal/v1"u8);

    /// <summary><c>nonce(12) ‖ ciphertext ‖ tag(16)</c>.</summary>
    private byte[] Seal(string key)
    {
        var plain = Encoding.UTF8.GetBytes(key);
        var box = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(box.AsSpan(0, 12));
        using var aes = new AesGcm(SealingKey(), 16);
        aes.Encrypt(box.AsSpan(0, 12), plain, box.AsSpan(12, plain.Length), box.AsSpan(12 + plain.Length));
        return box;
    }

    private string Unseal(byte[] box)
    {
        var plain = new byte[box.Length - 28];
        using var aes = new AesGcm(SealingKey(), 16);
        aes.Decrypt(box.AsSpan(0, 12), box.AsSpan(12, plain.Length), box.AsSpan(12 + plain.Length), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>32 random bytes created once (0600 on Unix), never over a file another process just wrote.</summary>
    private static byte[] LoadOrCreatePepper(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var file = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                file.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            try
            {
                using var stream = new FileStream(path, file);
                stream.Write(RandomNumberGenerator.GetBytes(32));
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        var pepper = File.ReadAllBytes(path);
        return pepper.Length >= 32 ? pepper : throw new InvalidOperationException($"PIN pepper {path} is shorter than 32 bytes");
    }

    private sealed class StaffRow
    {
        public const string Select = "SELECT s.id, s.name, s.role, s.club_id, cl.network_id FROM staff s JOIN clubs cl ON cl.id = s.club_id";

        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Role { get; init; } = "";
        public Guid ClubId { get; init; }
        public Guid NetworkId { get; init; }

        public StaffContext ToContext() => new(Id, Name, Role, ClubId, NetworkId);
    }
}
