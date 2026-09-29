using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Contracts.Errors;
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
/// deactivation (checked on every request). <c>ck_…</c> is the owner (synthetic "API key"), compared in constant time
/// with <c>clubs.api_key_hash</c> and never revoked by logout.
/// </summary>
public sealed class StaffTokens(NpgsqlDataSource db, AuthOptions options, TimeProvider clock, ILogger<StaffTokens> logger)
{
    public const string ApiKeyName = "API key";

    private readonly Lazy<byte[]> _pepper = new(() => LoadOrCreatePepper(options.PepperPath));

    /// <summary>
    /// Wrong PINs per client (DESIGN §3.6: 5 in 300 s, then 429), keyed by <see cref="PinLimitKey"/>, oldest first. One
    /// lock guards the map: login attempts are rare, and a key emptied by a right PIN or swept once per window is dropped,
    /// so the map holds only clients with failures inside the window.
    /// </summary>
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = [];

    private DateTimeOffset _nextSweep;

    public byte[] PinHmac(string pin) => HMACSHA256.HashData(_pepper.Value, Encoding.UTF8.GetBytes(pin));

    /// <summary>
    /// The PIN limiter's client key: an IPv4 address (also when mapped into IPv6), or the /64 of an IPv6 one — a single
    /// subscriber usually holds a whole /64 and could otherwise rotate addresses; "" when unknown.
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
    /// when <paramref name="client"/> used up its attempts. The attempt is counted as a failure before the lookup and given
    /// back unless the PIN turned out wrong: check and count are one step, so parallel requests cannot all pass the check
    /// before any failure is recorded. ponytail: in memory, per instance (one instance, D-20); a restart forgets the failures.
    /// </summary>
    public async Task<(string Token, StaffContext Staff)?> LoginAsync(string pin, IPAddress? client)
    {
        var key = PinLimitKey(client);
        var attempt = TakeAttempt(key);
        var wrong = false;
        try
        {
            var now = clock.GetUtcNow();
            await using var c = await db.OpenConnectionAsync();
            var staff = await c.QuerySingleOrDefaultAsync<StaffRow>(
                $"{StaffRow.Select} WHERE s.pin_hmac = @hmac AND s.active", new { hmac = PinHmac(pin) });
            if (staff is null)
            {
                wrong = true;

                // Security event (DESIGN §3.7); the PIN itself is never logged.
                logger.LogWarning("Wrong staff PIN from {Ip}", client);
                return null;
            }

            var token = "st_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            await c.ExecuteAsync(
                """
                INSERT INTO staff_tokens (token_hash, staff_id, club_id, created_at, last_used_at, expires_at)
                VALUES (@hash, @Id, @ClubId, @now, @now, @expires)
                """,
                new { hash = Hash(token), staff.Id, staff.ClubId, now, expires = now.AddDays(options.StaffTokenAbsoluteDays) });
            return (token, staff.ToContext());
        }
        finally
        {
            if (!wrong)
            {
                GiveBack(key, attempt);
            }
        }
    }

    /// <summary>Counts one attempt of <paramref name="key"/> and returns its time, or throws 429 with <c>Retry-After</c> when the window is full.</summary>
    private DateTimeOffset TakeAttempt(string key)
    {
        var window = TimeSpan.FromSeconds(options.PinWindowSec);
        lock (_failures)
        {
            var now = clock.GetUtcNow();
            if (now >= _nextSweep)
            {
                foreach (var (other, old) in _failures)
                {
                    old.RemoveAll(t => now - t >= window);
                    if (old.Count == 0)
                    {
                        _failures.Remove(other);
                    }
                }

                _nextSweep = now + window;
            }

            if (!_failures.TryGetValue(key, out var times))
            {
                _failures[key] = times = [];
            }

            times.RemoveAll(t => now - t >= window);
            if (times.Count >= options.PinAttempts)
            {
                var wait = Math.Max(1, (int)Math.Ceiling((times[0] + window - now).TotalSeconds));
                throw new ApiException(StatusCodes.Status429TooManyRequests, ErrorCode.RateLimited, "Too many wrong PINs", new { retryAfterSec = wait })
                {
                    Headers = { ["Retry-After"] = wait.ToString(CultureInfo.InvariantCulture) },
                };
            }

            times.Add(now);
            return now;
        }
    }

    /// <summary>Takes back the attempt of a login that was not a wrong PIN.</summary>
    private void GiveBack(string key, DateTimeOffset attempt)
    {
        lock (_failures)
        {
            if (_failures.TryGetValue(key, out var times) && times.Remove(attempt) && times.Count == 0)
            {
                _failures.Remove(key);
            }
        }
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
            var clubs = await c.QueryAsync<(Guid Id, Guid NetworkId, byte[] Hash)>("SELECT id, network_id, api_key_hash FROM clubs WHERE api_key_hash IS NOT NULL");
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
            WHERE t.token_hash = @hash AND s.id = t.staff_id AND s.active AND t.revoked_at IS NULL
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
            SELECT @id, network_id, id, 'Владелец', 'owner', @hmac FROM clubs ORDER BY created_at LIMIT 1
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
