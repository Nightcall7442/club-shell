using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Sessions;
using ClubShell.Server.Users;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Auth;

/// <summary>
/// Player sign-in on a PC (DESIGN §3.4, slice S2): <c>login</c> (password | card | token), <c>guestLogin</c>, the QR pair
/// (<c>startQrLogin</c>/<c>getQrLoginStatus</c>, D-18: nothing confirms it in v1, so it goes pending → expired) and
/// <c>logout</c>. Every sign-in displaces the previous player of the PC (<c>user_tokens UNIQUE(pc_id)</c>), but none is let
/// onto a PC that holds another player's open session (<c>403 pcOccupied</c>, D-26).
/// </summary>
public static class PlayerAuthEndpoints
{
    public static readonly string[] Operations = ["login", "startQrLogin", "getQrLoginStatus", "guestLogin", "logout"];

    /// <summary>Failures of one user within <see cref="FailureWindow"/> before the login is refused outright (§3.4).</summary>
    public const int MaxFailures = 5;

    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan QrTtl = TimeSpan.FromSeconds(120);

    public static void MapPlayerAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/auth");
        api.MapPost("/login", LoginAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
        api.MapPost("/guest", GuestAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
        api.MapPost("/qr/start", QrStartAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
        api.MapGet("/qr/{token}", QrStatusAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
        api.MapPost("/logout", LogoutAsync).WithMetadata(new AuthRequirement(AuthMode.User));
    }

    /// <summary>
    /// <c>kind</c>: <c>password</c> (username, any case) or <c>card</c> (<c>card_id</c>, any case, wrong cards counted per
    /// PC); <c>token</c> has no
    /// issuer in the contract, so it is always <c>401 badCredentials</c>; <c>qr</c>/<c>guest</c> have their own operations
    /// (<c>400 kind enum</c>). <c>offlineHash</c> is derived anew from the presented password (the agent's PBKDF2 format)
    /// for password logins when <c>Club:OfflineLogin</c>; the stored hash never leaves the server.
    /// </summary>
    private static async Task<IResult> LoginAsync(
        HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, UserTokens tokens, ClubOptions options, TimeProvider clock)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        var request = Api.Read<AuthRequest>(body, "kind", "pcId", "hwid");
        if (request.Kind is AuthKind.Qr or AuthKind.Guest)
        {
            throw ApiException.Validation("kind", "enum");
        }

        if (request.PcId != pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "pcId does not match the agent token");
        }

        await using var c = await db.OpenConnectionAsync();
        var club = await SessionService.ClubAsync(c, null, pc.ClubId);
        Guid userId;
        string? offlineHash = null;
        switch (request.Kind)
        {
            case AuthKind.Password:
                if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
                {
                    throw ApiException.Validation(string.IsNullOrWhiteSpace(request.Username) ? "username" : "password", "required");
                }

                userId = await CheckPasswordAsync(c, club.NetworkId, request.Username.Trim(), request.Password, Guid.Parse(ApiErrorWriter.TraceId(context)), clock.GetUtcNow());
                offlineHash = options.OfflineLogin ? Passwords.Hash(request.Password) : null;
                break;
            case AuthKind.Card when !string.IsNullOrWhiteSpace(request.CardId):
                userId = await CheckCardAsync(c, club.NetworkId, pc.Id, request.CardId.Trim(), Guid.Parse(ApiErrorWriter.TraceId(context)), clock.GetUtcNow());
                break;
            case AuthKind.Card:
                throw ApiException.Validation("cardId", "required");
            default:
                throw BadCredentials(MaxFailures);
        }

        return TypedResults.Ok(await SignInAsync(c, tokens, clock, club, pc, _ => Task.FromResult(userId), offlineHash));
    }

    /// <summary>
    /// «Гость» on the kiosk. A guest seat the desk opened on this PC (D-25: <c>origin = cashier</c>, a staff member, a
    /// transient account) is taken over: that guest is signed in and gets its session, even when <c>Club:GuestLogin</c> is
    /// off. Any other open session of the PC is <c>403 pcOccupied</c> (D-26). Otherwise a temporary <c>guest</c> account
    /// (<see cref="Guests"/>) signed in at once; <c>403 guestDisabled</c> when <c>Club:GuestLogin</c> is off. All of it runs
    /// in the sign-in's transaction under the PC's lock, so a refused press creates no account. No <c>offlineHash</c>.
    /// </summary>
    private static async Task<IResult> GuestAsync(
        HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, UserTokens tokens, ClubOptions options, TimeProvider clock)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        var request = Api.Read<GuestAuthRequest>(body, "pcId", "hwid");
        if (request.PcId != pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "pcId does not match the agent token");
        }

        if (request.Locale == Locale.Unknown)
        {
            throw ApiException.Validation("locale", "enum");
        }

        var locale = request.Locale is { } l ? JsonNamingPolicy.CamelCase.ConvertName(l.ToString()) : "ru";
        await using var c = await db.OpenConnectionAsync();
        var club = await SessionService.ClubAsync(c, null, pc.ClubId);
        return TypedResults.Ok(await SignInAsync(c, tokens, clock, club, pc, async tx =>
        {
            if (await Guests.DeskGuestOfPcAsync(c, tx, pc.Id) is { } deskGuest)
            {
                await c.ExecuteAsync("UPDATE users SET locale = @locale WHERE id = @deskGuest", new { deskGuest, locale }, tx);
                return deskGuest;
            }

            if (await SessionService.OpenOfPcAsync(c, pc.Id, tx) is not null)
            {
                throw PcOccupied();
            }

            if (!options.GuestLogin)
            {
                throw ApiException.Forbidden("guestDisabled", "Guest login is disabled in this club");
            }

            var name = request.DisplayName?.Trim() is { Length: > 0 } given ? given : $"Гость {pc.Number}";
            if (name.Length > 64)
            {
                throw ApiException.Validation("displayName", "max");
            }

            return await Guests.CreateAsync(c, tx, club.NetworkId, pc.Number, name, locale, clock.GetUtcNow());
        }, offlineHash: null));
    }

    /// <summary>
    /// Ends the player's open session on this PC with <c>reason</c> (settlement §5.7) and drops the player's token of this
    /// PC. The agent has usually ended the session itself already. No <c>userRevoked</c>: the PC asked for it, and the
    /// agent treats that push as a forced revocation (the lock screen would say the sign-in expired).
    /// </summary>
    private static async Task<IResult> LogoutAsync(
        HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, SessionService sessions, TimeProvider clock)
    {
        var request = Api.Read<LogoutRequest>(body, "reason");
        if (request.Reason == SessionEndReason.Unknown)
        {
            throw ApiException.Validation("reason", "enum");
        }

        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        var user = context.Features.GetRequiredFeature<UserContext>();
        var effects = new SessionEffects();
        var (c, tx) = await SessionService.BeginAsync(db);
        await using (c)
        await using (tx)
        {
            var now = clock.GetUtcNow();
            var open = await c.QuerySingleOrDefaultAsync<SessionRow>(
                $"SELECT {SessionRow.Columns} FROM sessions WHERE user_id = @UserId AND pc_id = @pcId AND state <> 'ended' FOR UPDATE",
                new { user.UserId, pcId = pc.Id }, tx);
            if (open is not null)
            {
                await sessions.SettleAsync(c, tx, open, now, request.Reason, effects);
            }

            await c.ExecuteAsync(
                """
                DELETE FROM user_tokens WHERE user_id = @UserId AND pc_id = @pcId;
                UPDATE users SET last_seen_at = @now WHERE id = @UserId;
                """,
                new { user.UserId, pcId = pc.Id, now }, tx);
            await tx.CommitAsync();
        }

        await sessions.PublishAsync(effects);
        return Results.NoContent();
    }

    /// <summary>A QR token for the club app (<c>qrUrl</c> = <c>&lt;server&gt;/q/&lt;token&gt;</c>), valid 120 s, polled every 2 s.</summary>
    private static async Task<IResult> QrStartAsync(HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        if (Api.Read<QrStartRequest>(body, "pcId").PcId != pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "pcId does not match the agent token");
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "INSERT INTO qr_logins (token_hash, club_id, pc_id, created_at, expires_at) VALUES (@hash, @ClubId, @Id, @now, @expires)",
            new { hash = UserTokens.HashOf(token), pc.ClubId, pc.Id, now, expires = now + QrTtl });
        var url = $"{context.Request.Scheme}://{context.Request.Host}/q/{token}";
        return TypedResults.Ok(new QrLoginStart(token, url, now + QrTtl, PollIntervalSec: 2));
    }

    /// <summary>D-18: no operation confirms a QR login in the contract, so a live token is <c>pending</c>, then <c>expired</c>.</summary>
    private static async Task<IResult> QrStatusAsync(HttpContext context, string token, NpgsqlDataSource db, TimeProvider clock)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        await using var c = await db.OpenConnectionAsync();
        var qr = await c.QuerySingleOrDefaultAsync<QrRow>(
            "SELECT pc_id, expires_at, consumed_at FROM qr_logins WHERE token_hash = @hash", new { hash = UserTokens.HashOf(token) })
            ?? throw ApiException.NotFound("qrToken");
        if (qr.PcId != pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "The QR token was issued to another PC");
        }

        var live = qr.ConsumedAt is null && qr.ExpiresAt > clock.GetUtcNow();
        return TypedResults.Ok(new QrLoginStatus(live ? QrStatus.Pending : QrStatus.Expired));
    }

    /// <summary>
    /// Password check with the lockout (§3.4, D-17): failures are counted per lower-cased username of the network, known
    /// or not (so <c>attemptsLeft</c> does not tell which names exist; an unknown one costs a dummy derivation); 5 in 15 min
    /// refuse even the right password with <c>attemptsLeft = 0</c> until the window passes. The agent repeats a 401 once
    /// after a refresh with the same <c>X-Trace-Id</c> (N2): that first repeat is free, every further one counts. Attempts
    /// on one name are serialized (transaction advisory lock), so parallel guesses cannot all pass the count.
    /// </summary>
    private static Task<Guid> CheckPasswordAsync(NpgsqlConnection c, Guid network, string username, string password, Guid traceId, DateTimeOffset now) =>
        CountFailuresAsync(c, network, username.ToLowerInvariant()[..Math.Min(username.Length, 64)], traceId, now, forgetOnSuccess: true, async tx =>
        {
            var user = await c.QuerySingleOrDefaultAsync<Credentials>(
                """
                SELECT id, password_hash FROM users
                WHERE network_id = @network AND lower(username) = lower(@username) AND deleted_at IS NULL AND NOT transient
                """,
                new { network, username }, tx);
            return Passwords.Verify(user?.PasswordHash, password) && user is not null ? user.Id : null;
        });

    /// <summary>
    /// Card check with a lockout per PC (D-75): card numbers are printed on the cards and often run in sequence, and the
    /// kiosk takes any keyboard, so wrong cards are counted like wrong passwords, under the key <c>card:&lt;pcId&gt;</c>
    /// (41 characters: no username, at most 32, can take that form). 5 in 15 min refuse even a bound card on that PC with
    /// <c>attemptsLeft = 0</c> until the window passes; other PCs, and passwords on this one, are not affected. A right
    /// card does not clear the count: a guesser holding a card of their own could otherwise start over after every four.
    /// </summary>
    private static Task<Guid> CheckCardAsync(NpgsqlConnection c, Guid network, Guid pcId, string cardId, Guid traceId, DateTimeOffset now) =>
        CountFailuresAsync(c, network, $"card:{pcId}", traceId, now, forgetOnSuccess: false, tx =>
            c.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM users WHERE network_id = @network AND lower(card_id) = lower(@cardId) AND deleted_at IS NULL",
                new { network, cardId }, tx));

    /// <summary>
    /// The failure count of <paramref name="key"/> in <c>login_failures</c> around <paramref name="check"/>: refused with
    /// <c>attemptsLeft = 0</c> at <see cref="MaxFailures"/> within <see cref="FailureWindow"/>, a miss recorded once per
    /// trace (the agent's first repeat is free), a match returned (and the count dropped when
    /// <paramref name="forgetOnSuccess"/>). Serialized per key by a transaction advisory lock.
    /// </summary>
    private static async Task<Guid> CountFailuresAsync(
        NpgsqlConnection c, Guid network, string key, Guid traceId, DateTimeOffset now, bool forgetOnSuccess, Func<NpgsqlTransaction, Task<Guid?>> check)
    {
        const string Count = """
            SELECT coalesce(sum(greatest(attempts - 1, 1)), 0)::int FROM login_failures
            WHERE network_id = @network AND username = @name AND at > @since
            """;
        var args = new { network, name = key, since = now - FailureWindow, traceId, now };
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@network::text || '/' || @name, 0))", args, tx);
        if (await c.ExecuteScalarAsync<int>(Count, args, tx) >= MaxFailures)
        {
            throw BadCredentials(0);
        }

        if (await check(tx) is { } userId)
        {
            if (forgetOnSuccess)
            {
                await c.ExecuteAsync("DELETE FROM login_failures WHERE network_id = @network AND username = @name", args, tx);
            }

            await tx.CommitAsync();
            return userId;
        }

        await c.ExecuteAsync(
            """
            INSERT INTO login_failures (network_id, username, trace_id, at) VALUES (@network, @name, @traceId, @now)
            ON CONFLICT (network_id, username, trace_id) DO UPDATE SET attempts = login_failures.attempts + 1, at = excluded.at
            """,
            args, tx);
        var failures = await c.ExecuteScalarAsync<int>(Count, args, tx);
        await tx.CommitAsync();
        throw BadCredentials(Math.Max(0, MaxFailures - failures));
    }

    /// <summary>
    /// The checks after the credentials (§3.4) and the token, in one transaction under the PC's advisory lock (D-27): the
    /// player (<paramref name="who"/>: known, or found or created by the guest press in this transaction), <c>403 banned</c>
    /// (banned or blacklisted in this club), <c>403 ageRestricted</c> (a minor in the curfew), <c>409
    /// activeSessionElsewhere</c>, <c>403 pcOccupied</c> when the PC holds another player's open session (D-26: otherwise the
    /// session resync would unlock the desktop under the wrong name); then the token of this PC and the player's open session
    /// on this PC, if any. The refresh token is opaque and never accepted: players have none.
    /// </summary>
    private static async Task<AuthResponse> SignInAsync(
        NpgsqlConnection c, UserTokens tokens, TimeProvider clock, ClubInfo club, Agents.PcRow pc, Func<NpgsqlTransaction, Task<Guid>> who,
        string? offlineHash)
    {
        var now = clock.GetUtcNow();
        Guid userId;
        SessionRow? open;
        string token;
        DateTimeOffset expiresAt;
        await using (var tx = await c.BeginTransactionAsync())
        {
            await c.ExecuteAsync("SET LOCAL lock_timeout = '10s'", transaction: tx);
            await AdvisoryLocks.PcAsync(c, tx, pc.Id);
            userId = await who(tx);
            var buyer = await SessionService.BuyerAsync(c, tx, club, userId) ?? throw BadCredentials(MaxFailures);
            if (buyer.Banned || buyer.Blacklisted)
            {
                throw ApiException.Forbidden("banned", "The account is banned");
            }

            if (club.Pricing.InCurfew(buyer.BirthYear, now))
            {
                throw ApiException.Forbidden("ageRestricted", "Minors may not play at this hour");
            }

            open = await c.QuerySingleOrDefaultAsync<SessionRow>(
                $"SELECT {SessionRow.Columns} FROM sessions WHERE user_id = @userId AND state <> 'ended'", new { userId }, tx);
            if (open is not null && open.PcId != pc.Id)
            {
                var pcName = await c.ExecuteScalarAsync<string>("SELECT name FROM pcs WHERE id = @PcId", new { open.PcId }, tx);
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "The player has an open session on another PC",
                    new { reason = "activeSessionElsewhere", pcId = open.PcId, pcName });
            }

            if (await SessionService.OpenOfPcAsync(c, pc.Id, tx) is { } occupant && occupant.UserId != userId)
            {
                throw PcOccupied();
            }

            (token, expiresAt) = await tokens.IssueAsync(c, tx, userId, pc.Id);
            await c.ExecuteAsync("UPDATE users SET last_seen_at = @now WHERE id = @userId", new { userId, now }, tx);
            await tx.CommitAsync();
        }

        var user = await c.QuerySingleAsync<UserRow>($"{UserRow.Select} WHERE u.id = @userId", new { userId });
        return new AuthResponse(user.ToWire(club.Pricing), open?.ToWire(now), token, UserTokens.NewToken(), expiresAt, offlineHash);
    }

    private static ApiException BadCredentials(int attemptsLeft) =>
        new(StatusCodes.Status401Unauthorized, ErrorCode.Unauthorized, "Wrong credentials", new { reason = "badCredentials", attemptsLeft });

    /// <summary><c>403 forbidden reason=pcOccupied</c>: the PC holds another player's open session; the cashier ends it first.</summary>
    private static ApiException PcOccupied() => ApiException.Forbidden("pcOccupied", "The PC holds another player's open session");

    private sealed class Credentials
    {
        public Guid Id { get; init; }
        public string? PasswordHash { get; init; }
    }

    private sealed class QrRow
    {
        public Guid PcId { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? ConsumedAt { get; init; }
    }
}
