using System.Text.Json;
using ClubShell.Contracts.Errors;
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
/// A player's own account (D-92, beyond the contract): <c>POST /auth/register</c> from the agent of a PC of the club,
/// where the owner turned <c>settings.limits.selfSignup</c> on (off by default: an account nobody at the desk saw is the
/// owner's call; <c>403 selfSignupDisabled</c>). The account is a plain <c>member</c> with zero balances — never staff,
/// no group, no card — in the PC's network with the club profile (the phone when the login is one), and the player is
/// signed in at once exactly like <c>POST /auth/login</c> (same answer, same checks: <c>pcOccupied</c>, …), so the agent
/// keeps the tokens as for a sign-in. Abuse limits: <see cref="MaxPerPc"/> accounts per PC in <see cref="Window"/>
/// (<c>429 rateLimited {reason: signupLimit, retryAfterSec}</c>, counted on <c>users.signup_pc_id</c> under the PC's
/// lock), a password of 4–64 characters, the club rules accepted when the club has any (<c>400 acceptRules
/// required</c>), the player's gender (D-104, <c>400 gender required|enum</c>) and date of birth (D-107, <c>400 birthDate
/// required|format|future|min|max</c>). Each account leaves a <c>clientSelfSignup</c> journal row (no staff member, the PC) for the owner.
/// <para>
/// A mobile app of the club would need its own way in: this route trusts the PC's signed agent requests, which a phone
/// does not have (a public route would want a captcha or an SMS code and its own rate limit per address).
/// </para>
/// </summary>
public static class SelfSignup
{
    /// <summary>Accounts one PC may create within <see cref="Window"/>.</summary>
    public const int MaxPerPc = 5;

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary><c>settings.limits.selfSignup</c>: on only when the owner switched it on.</summary>
    public static bool Enabled(JsonElement settings) =>
        settings.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Object
        && limits.TryGetProperty("selfSignup", out var on) && on.ValueKind == JsonValueKind.True;

    /// <summary>Whether the club wrote rules in any language (<c>settings.rulesText</c>): then they are accepted first.</summary>
    public static bool HasRules(JsonElement settings) =>
        settings.TryGetProperty("rulesText", out var rules) && rules.ValueKind == JsonValueKind.Object
        && rules.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()));

    internal static async Task<IResult> RegisterAsync(
        HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, UserTokens tokens, SessionService sessions, ClubOptions options, TimeProvider clock)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        var request = Api.Read<RegisterAuthRequest>(body, "pcId", "hwid");
        if (request.PcId != pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "pcId does not match the agent token");
        }

        await using var c = await db.OpenConnectionAsync();
        var settingsJson = await c.ExecuteScalarAsync<string>("SELECT settings::text FROM clubs WHERE id = @ClubId", new { pc.ClubId }) ?? "{}";
        using var settings = JsonDocument.Parse(settingsJson);
        if (!Enabled(settings.RootElement))
        {
            throw ApiException.Forbidden("selfSignupDisabled", "This club registers new players at the desk");
        }

        var displayName = request.DisplayName?.Trim() ?? "";
        if (displayName.Length == 0)
        {
            throw ApiException.Validation("displayName", "required");
        }

        if (displayName.Length > 64)
        {
            throw ApiException.Validation("displayName", "max");
        }

        var typed = string.IsNullOrWhiteSpace(request.Username) ? null : Logins.Typed(request.Username);
        var phone = Logins.PhoneDigits(request.Username) is not null ? request.Username!.Trim() : "";
        var password = request.Password ?? "";
        if (password.Length == 0)
        {
            throw ApiException.Validation("password", "required");
        }

        if (password.Length is < 4 or > 64)
        {
            throw ApiException.Validation("password", password.Length < 4 ? "min" : "max");
        }

        // D-104: every new account says the player's gender; D-107: and the date of birth.
        var gender = Genders.Required(request.Gender);
        var club = await SessionService.ClubAsync(c, null, pc.ClubId);
        var birthDate = BirthDates.Format(BirthDates.Required(request.BirthDate, BirthDates.Today(clock.GetUtcNow(), club.Pricing.TimeZone)));

        if (HasRules(settings.RootElement) && request.AcceptRules != true)
        {
            throw ApiException.Validation("acceptRules", "required");
        }

        if (request.Locale == Locale.Unknown)
        {
            throw ApiException.Validation("locale", "enum");
        }

        var locale = request.Locale is { } l ? JsonNamingPolicy.CamelCase.ConvertName(l.ToString()) : "ru";

        // PBKDF2 outside the transaction: it holds no lock while it derives.
        var hash = Passwords.Hash(password);
        var offlineHash = options.OfflineLogin ? Passwords.Hash(password) : null;
        return TypedResults.Ok(await PlayerAuthEndpoints.SignInAsync(c, tokens, sessions, club, pc, async tx =>
        {
            // Under the PC's lock (SignInAsync takes it first): parallel presses on one PC cannot all pass the count.
            var now = clock.GetUtcNow();
            var recent = await c.QuerySingleAsync<(int Count, DateTimeOffset? Oldest)>(
                "SELECT count(*)::int, min(created_at) FROM users WHERE signup_pc_id = @Id AND created_at > @since",
                new { pc.Id, since = now - Window }, tx);
            if (recent.Count >= MaxPerPc)
            {
                var retryAfter = recent.Oldest is { } oldest ? (int)Math.Ceiling((oldest + Window - now).TotalSeconds) : (int)Window.TotalSeconds;
                throw new ApiException(StatusCodes.Status429TooManyRequests, ErrorCode.RateLimited,
                    "Too many accounts were created on this PC, try again later", new { reason = "signupLimit", retryAfterSec = Math.Max(1, retryAfter) });
            }

            string username;
            if (typed is not null)
            {
                await Logins.EnsureFreeAsync(c, tx, club.NetworkId, typed);
                username = typed;
            }
            else
            {
                username = await Logins.GenerateAsync(c, tx, club.NetworkId, displayName, phone);
            }

            var id = Guid.CreateVersion7(now);
            try
            {
                await c.ExecuteAsync("SAVEPOINT self_signup", transaction: tx);
                await c.ExecuteAsync(
                    """
                    INSERT INTO users (id, network_id, username, display_name, role, password_hash, locale, signup_pc_id, gender, birth_date, created_at, last_seen_at)
                    VALUES (@id, @NetworkId, @username, @displayName, 'member', @hash, @locale, @pcId, @gender, @birthDate::date, @now, @now);
                    INSERT INTO wallets (user_id, network_id, updated_at) VALUES (@id, @NetworkId, @now);
                    INSERT INTO client_profiles (club_id, user_id, phone, birth_year, updated_at)
                    VALUES (@ClubId, @id, @phone, extract(year FROM @birthDate::date)::int, @now);
                    INSERT INTO audit_entries (id, club_id, at, staff_id, staff_name, action, user_id, pc_id, detail, meta)
                    VALUES (@auditId, @ClubId, @now, NULL, '', 'clientSelfSignup', @id, @pcId, @detail, @meta::jsonb);
                    """,
                    new
                    {
                        id, club.NetworkId, pc.ClubId, username, displayName, hash, locale, pcId = pc.Id, now, phone, gender, birthDate,
                        auditId = Guid.CreateVersion7(now), detail = $"{displayName} · {username} · {pc.Name}",
                        meta = JsonSerializer.Serialize(new { username, pcName = pc.Name, generated = typed is null }, ServerJson.Options),
                    },
                    tx);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "users_username")
            {
                // A login another request took after the check.
                await c.ExecuteAsync("ROLLBACK TO SAVEPOINT self_signup", transaction: tx);
                throw ApiException.Validation("username", "taken");
            }

            return id;
        }, offlineHash, endElsewhere: false));
    }
}
