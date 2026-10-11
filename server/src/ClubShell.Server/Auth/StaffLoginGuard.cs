using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClubShell.Contracts.Errors;
using ClubShell.Server.Admin;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Auth;

/// <summary>
/// The wrong-PIN limits of <c>POST /admin/login</c> (D-76, DESIGN §3.6), counted in <c>staff_login_failures</c> per club, so
/// they survive a restart and add up across addresses:
/// <list type="number">
/// <item>one source — a browser the club trusts, else the IP (IPv6: its /64) — gets <see cref="AuthOptions.PinAttempts"/>
/// wrong PINs within <see cref="AuthOptions.PinWindowSec"/> (<c>scope=source</c>);</item>
/// <item>the club's untrusted sources together get <see cref="AuthOptions.PinClubAttempts"/> within
/// <see cref="AuthOptions.PinClubWindowSec"/> (<c>scope=club</c>): an attacker's many addresses share one budget, and a player
/// on the club's Wi-Fi cannot lock out the desks, which the club trusts;</item>
/// <item>the club's trusted browsers together get <see cref="AuthOptions.PinTrustedAttempts"/> in the same window
/// (<c>scope=trusted</c>): a cashier who signs in from many browsers to guess the owner's PIN still has one budget.</item>
/// </list>
/// Past a limit every attempt of that source, the right PIN too, is <c>429 rateLimited</c> with <c>retryAfterSec</c>,
/// <c>scope</c> and <c>Retry-After</c> until enough failures leave the window. A refusal is not recorded, and a right PIN
/// clears nothing: a guesser holding a PIN of their own could otherwise start over (as with cards, D-75). A browser is
/// trusted by a club for <see cref="AuthOptions.PinDeviceTrustDays"/> after its last sign-in there with its
/// <c>deviceId</c> (random bytes the console keeps); only the SHA-256 of the id is stored. An unknown club code or a disabled
/// club is counted in the <see cref="Guid.Empty"/> bucket under the first limit only. The first trip of a club-wide limit in
/// a window enqueues the club's <c>suspicious</c> webhook and logs an error, which reaches Sentry (club and counts only:
/// never a PIN or an address, which stay in the <see cref="StaffTokens"/> log).
/// </summary>
public sealed partial class StaffLoginGuard(AuthOptions options, ILogger<StaffLoginGuard> logger)
{
    /// <summary>Where an attempt comes from: the club it counts in, the source key, and the hash of a well-formed device id.</summary>
    public sealed record Source(Guid ClubId, string Key, bool Trusted, byte[]? DeviceHash);

    /// <summary>SHA-256 of a console's <c>deviceId</c>: 22–86 base64url characters (16–64 random bytes); null for anything else, which is ignored.</summary>
    public static byte[]? DeviceHash(string? deviceId) =>
        deviceId is not null && DeviceIdPattern().IsMatch(deviceId) ? SHA256.HashData(Encoding.ASCII.GetBytes(deviceId)) : null;

    /// <summary>The source of an attempt on <paramref name="clubId"/>: the presented browser when the club trusts it, else the network.</summary>
    public async Task<Source> SourceAsync(NpgsqlConnection c, Guid clubId, IPAddress? client, string? deviceId, DateTimeOffset now)
    {
        var hash = DeviceHash(deviceId);
        if (hash is not null && clubId != Guid.Empty && await c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM staff_devices WHERE club_id = @clubId AND device_hash = @hash AND last_login_at > @since)",
                new { clubId, hash, since = now.AddDays(-options.PinDeviceTrustDays) }))
        {
            return new Source(clubId, "dev:" + Base64Url.EncodeToString(hash), Trusted: true, hash);
        }

        return new Source(clubId, "net:" + StaffTokens.PinLimitKey(client), Trusted: false, hash);
    }

    /// <summary>
    /// Serializes the attempts on one club for the rest of <paramref name="tx"/>, so parallel ones cannot all pass the count
    /// before any failure is recorded. The wait is capped at 5 s: past it the attempt is 503 «Database unavailable».
    /// </summary>
    public static Task LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId) =>
        c.ExecuteAsync("SET LOCAL lock_timeout = '5s'; SELECT pg_advisory_xact_lock(hashtextextended('staff-pin:' || @clubId::text, 0));", new { clubId }, tx);

    /// <summary>
    /// <c>429 rateLimited</c> when a limit of <paramref name="source"/> is full. Run once without <paramref name="tx"/> before
    /// the lock, so a flood is refused before it queues on it, and once under the lock, where it decides.
    /// </summary>
    public async Task CheckAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Source source, DateTimeOffset now)
    {
        var window = TimeSpan.FromSeconds(options.PinWindowSec);
        if (await FullSinceAsync(c, tx, "source = @Key", source, options.PinAttempts, now - window) is { } at)
        {
            throw Limited(at + window - now, "source");
        }

        var (limit, scope) = source.Trusted ? (options.PinTrustedAttempts, "trusted") : (options.PinClubAttempts, "club");
        var clubWindow = TimeSpan.FromSeconds(options.PinClubWindowSec);
        if (source.ClubId != Guid.Empty && await FullSinceAsync(c, tx, "trusted = @Trusted", source, limit, now - clubWindow) is { } clubAt)
        {
            throw Limited(clubAt + clubWindow - now, scope);
        }
    }

    /// <summary>
    /// A wrong PIN of <paramref name="source"/>. When it fills the club-wide limit of its kind and no failure of the window has
    /// alerted yet, the club's <c>suspicious</c> webhook goes out with this transaction and an error is logged.
    /// </summary>
    public async Task FailAsync(NpgsqlConnection c, NpgsqlTransaction tx, Source source, DateTimeOffset now)
    {
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO staff_login_failures (club_id, source, trusted, at) VALUES (@ClubId, @Key, @Trusted, @now) RETURNING id",
            new { source.ClubId, source.Key, source.Trusted, now }, tx);
        var (limit, scope) = source.Trusted ? (options.PinTrustedAttempts, "trusted") : (options.PinClubAttempts, "club");
        if (source.ClubId == Guid.Empty || limit <= 0)
        {
            return;
        }

        var (failures, alerted) = await c.QuerySingleAsync<(int Failures, bool Alerted)>(
            "SELECT count(*)::int, coalesce(bool_or(alerted), false) FROM staff_login_failures WHERE club_id = @ClubId AND trusted = @Trusted AND at > @since",
            new { source.ClubId, source.Trusted, since = now.AddSeconds(-options.PinClubWindowSec) }, tx);
        if (failures < limit || alerted)
        {
            return;
        }

        await c.ExecuteAsync("UPDATE staff_login_failures SET alerted = true WHERE id = @id", new { id }, tx);
        var window = options.PinClubWindowSec % 3600 == 0
            ? $"{options.PinClubWindowSec / 3600} ч"
            : $"{(options.PinClubWindowSec + 59) / 60} мин";
        var text = source.Trusted
            ? $"Вход в кассу: {failures} неверных PIN за {window} с компьютеров, где уже входили; вход с них закрыт до конца окна"
            : $"Вход в кассу: {failures} неверных PIN за {window} с новых устройств; вход с них закрыт, компьютеры кассы работают";
        await Webhooks.EnqueueAsync(c, tx, source.ClubId, "suspicious", now, text,
            new { reason = "pinLimit", scope, failures, windowSec = options.PinClubWindowSec });
        logger.LogError("Club {ClubId}: {Failures} wrong staff PINs from {Scope} sources within {WindowSec} s; their sign-ins are refused until the window passes",
            source.ClubId, failures, scope == "club" ? "untrusted" : "trusted", options.PinClubWindowSec);
    }

    /// <summary>A right PIN: the browser of a well-formed device id becomes, or stays, trusted by the club.</summary>
    public static Task TrustAsync(NpgsqlConnection c, NpgsqlTransaction tx, Source source, Guid staffId, DateTimeOffset now) =>
        source.DeviceHash is null
            ? Task.CompletedTask
            : c.ExecuteAsync(
                """
                INSERT INTO staff_devices (club_id, device_hash, last_staff_id, first_login_at, last_login_at)
                VALUES (@ClubId, @DeviceHash, @staffId, @now, @now)
                ON CONFLICT (club_id, device_hash) DO UPDATE SET last_staff_id = excluded.last_staff_id, last_login_at = excluded.last_login_at
                """,
                new { source.ClubId, source.DeviceHash, staffId, now }, tx);

    /// <summary>
    /// When the bucket holds <paramref name="limit"/> failures since <paramref name="since"/>: the time of the limit-th newest,
    /// whose leaving the window lets the next attempt through; null when it holds fewer or the limit is off (0).
    /// </summary>
    private static async Task<DateTimeOffset?> FullSinceAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string bucket, Source source, int limit, DateTimeOffset since) =>
        limit <= 0
            ? null
            : await c.ExecuteScalarAsync<DateTimeOffset?>(
                $"SELECT at FROM staff_login_failures WHERE club_id = @ClubId AND {bucket} AND at > @since ORDER BY at DESC OFFSET @skip LIMIT 1",
                new { source.ClubId, source.Key, source.Trusted, since, skip = limit - 1 }, tx);

    private static ApiException Limited(TimeSpan wait, string scope)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        return new ApiException(StatusCodes.Status429TooManyRequests, ErrorCode.RateLimited, "Too many wrong PINs", new { retryAfterSec = seconds, scope })
        {
            Headers = { ["Retry-After"] = seconds.ToString(CultureInfo.InvariantCulture) },
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{22,86}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdPattern();
}
