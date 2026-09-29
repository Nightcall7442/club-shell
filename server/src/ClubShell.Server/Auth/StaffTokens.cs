using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// with <c>clubs.api_key</c> and never revoked by logout.
/// </summary>
public sealed class StaffTokens(NpgsqlDataSource db, AuthOptions options, TimeProvider clock, ILogger<StaffTokens> logger)
{
    public const string ApiKeyName = "API key";

    private readonly Lazy<byte[]> _pepper = new(() => LoadOrCreatePepper(options.PepperPath));

    /// <summary>Failed PIN logins per client IP (DESIGN §3.6: 5 in 300 s, then 429).</summary>
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();

    public byte[] PinHmac(string pin) => HMACSHA256.HashData(_pepper.Value, Encoding.UTF8.GetBytes(pin));

    /// <summary>
    /// Seconds until <paramref name="ip"/> may try a PIN again, or 0. ponytail: in memory, per instance (one instance,
    /// D-20); a restart forgets the failures.
    /// </summary>
    public int RetryAfter(string ip)
    {
        if (!_failures.TryGetValue(ip, out var times))
        {
            return 0;
        }

        lock (times)
        {
            var now = clock.GetUtcNow();
            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromSeconds(options.PinWindowSec))
            {
                times.Dequeue();
            }

            return times.Count < options.PinAttempts ? 0 : Math.Max(1, (int)Math.Ceiling((times.Peek().AddSeconds(options.PinWindowSec) - now).TotalSeconds));
        }
    }

    /// <summary>The active staff member of <paramref name="pin"/> with a new token, or null (the failure is counted for <paramref name="ip"/>).</summary>
    public async Task<(string Token, StaffContext Staff)?> LoginAsync(string pin, string ip)
    {
        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        var staff = await c.QuerySingleOrDefaultAsync<StaffRow>(
            $"{StaffRow.Select} WHERE s.pin_hmac = @hmac AND s.active", new { hmac = PinHmac(pin) });
        if (staff is null)
        {
            var times = _failures.GetOrAdd(ip, _ => new Queue<DateTimeOffset>());
            lock (times)
            {
                times.Enqueue(now);
            }

            // Security event (DESIGN §3.7); the PIN itself is never logged.
            logger.LogWarning("Wrong staff PIN from {Ip}", ip);
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
            var clubs = await c.QueryAsync<(Guid Id, Guid NetworkId, string Key)>("SELECT id, network_id, api_key FROM clubs WHERE api_key IS NOT NULL");
            var presented = Encoding.UTF8.GetBytes(token);
            foreach (var club in clubs)
            {
                if (CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(club.Key)))
                {
                    return new StaffContext(null, ApiKeyName, "owner", club.Id, club.NetworkId);
                }
            }

            return null;
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
