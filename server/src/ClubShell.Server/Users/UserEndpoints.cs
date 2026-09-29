using System.Text.RegularExpressions;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Sessions;
using ClubShell.Server.Sessions.Billing;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Users;

/// <summary>A player with the wallet columns the wire <c>User</c> needs (DESIGN §4.2 "derived").</summary>
public sealed class UserRow
{
    public const string Select = """
        SELECT u.id, u.username, u.display_name, u.avatar_url, u.role, u.locale, u.flags, u.banned, u.created_at, u.last_seen_at,
               w.main_balance, w.lifetime_spent
        FROM users u JOIN wallets w ON w.user_id = u.id
        """;

    public Guid Id { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public string Role { get; init; } = "member";
    public string Locale { get; init; } = "ru";
    public string[] Flags { get; init; } = [];
    public bool Banned { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public long MainBalance { get; init; }
    public long LifetimeSpent { get; init; }

    /// <summary>One loyalty model (§5.13 item 5): the club's level by lifetime spend, 0-based on the wire; points = spend / 100.</summary>
    public User ToWire(ClubPricing club) => new(
        Id, Username, DisplayName, AvatarUrl, Enum.Parse<UserRole>(Role, ignoreCase: true), Money.Uzs(MainBalance),
        Math.Max(0, (club.LevelOf(LifetimeSpent)?.Level ?? 1) - 1), Points(LifetimeSpent), CreatedAt, LastSeenAt,
        Enum.Parse<Locale>(Locale, ignoreCase: true), Banned ? [.. Flags, UserFlags.Banned] : Flags);

    public static int Points(long tiyin) => (int)Math.Min(int.MaxValue, tiyin / 100);
}

/// <summary>
/// The player's own profile (S2): <c>getUser</c>, <c>updateUser</c>, <c>getUserStats</c>, <c>getUserAchievements</c>,
/// <c>getUserLoyalty</c>, and beyond the contract <c>/users/{userId}/game-settings</c> — list <c>{items:[]}</c> and
/// <c>DELETE</c> 204, the rest 501 (DESIGN §1: player settings are not stored in v1).
/// </summary>
public static partial class UserEndpoints
{
    public static readonly string[] Operations = ["getUser", "updateUser", "getUserStats", "getUserAchievements", "getUserLoyalty"];

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/users/{userId:guid}");
        var own = api.MapGroup("").OwnedByPlayer();
        own.MapGet("", async (HttpContext context, Guid userId, NpgsqlDataSource db) =>
        {
            await using var c = await db.OpenConnectionAsync();
            return TypedResults.Ok(await LoadAsync(c, context, userId));
        });
        own.MapPatch("", UpdateAsync);
        own.MapGet("/stats", StatsAsync);
        own.MapGet("/achievements", () => TypedResults.Ok(new ProfileAchievementsResponse([])));
        own.MapGet("/loyalty", LoyaltyAsync);
        own.MapGet("/game-settings", () => TypedResults.Ok(new PlayerSettingsListResponse([])));
        own.MapDelete("/game-settings/{gameId:guid}", () => Results.NoContent());

        var setting = api.MapGroup("/game-settings/{gameId:guid}").WithMetadata(new AuthRequirement(AuthMode.Agent));
        setting.MapGet("", (RequestDelegate)(_ => throw ApiException.NotImplemented("getPlayerSettings")));
        setting.MapPut("", (RequestDelegate)(_ => throw ApiException.NotImplemented("commitPlayerSettings")));
        setting.MapPost("/upload-target", (RequestDelegate)(_ => throw ApiException.NotImplemented("getPlayerSettingsUploadTarget")));
    }

    private static async Task<User> LoadAsync(NpgsqlConnection c, HttpContext context, Guid userId)
    {
        var club = await SessionService.ClubAsync(c, null, context.Features.GetRequiredFeature<AgentContext>().Pc.ClubId);
        var row = await c.QuerySingleOrDefaultAsync<UserRow>($"{UserRow.Select} WHERE u.id = @userId AND u.deleted_at IS NULL", new { userId });
        return row?.ToWire(club.Pricing) ?? throw ApiException.NotFound("user");
    }

    /// <summary>Partial update, at least one field (§ contract <c>ProfileUpdateRequest</c>); a guest may not (403 guest).</summary>
    private static async Task<IResult> UpdateAsync(HttpContext context, Guid userId, [FromBody] System.Text.Json.JsonElement body, NpgsqlDataSource db)
    {
        var request = Api.Read<ProfileUpdateRequest>(body);
        await using var c = await db.OpenConnectionAsync();
        if (await c.ExecuteScalarAsync<string>("SELECT role FROM users WHERE id = @userId", new { userId }) == "guest")
        {
            throw ApiException.Forbidden("guest", "A guest has no profile to change");
        }

        if (request is { DisplayName: null, AvatarUrl: null, Locale: null, Pin: null })
        {
            throw ApiException.Validation("body", "required", "Nothing to update");
        }

        var name = request.DisplayName?.Trim();
        if (name is { Length: 0 or > 64 })
        {
            throw ApiException.Validation("displayName", name.Length == 0 ? "min" : "max");
        }

        if (request.AvatarUrl is { } url && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            throw ApiException.Validation("avatarUrl", "format");
        }

        if (request.Locale == Locale.Unknown)
        {
            throw ApiException.Validation("locale", "enum");
        }

        if (request.Pin is { } pin && !PinPattern().IsMatch(pin))
        {
            throw ApiException.Validation("pin", "format");
        }

        await c.ExecuteAsync(
            """
            UPDATE users SET display_name = coalesce(@name, display_name), avatar_url = coalesce(@avatar, avatar_url),
                             locale = coalesce(@locale, locale), unlock_pin_hash = coalesce(@pin, unlock_pin_hash)
            WHERE id = @userId
            """,
            new
            {
                userId, name, avatar = request.AvatarUrl, locale = request.Locale is { } l ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(l.ToString()) : null,
                pin = request.Pin is null ? null : Passwords.Hash(request.Pin),
            });
        return TypedResults.Ok(await LoadAsync(c, context, userId));
    }

    /// <summary>
    /// Derived (DESIGN §4.2): hours and count from <c>sessions</c>, spent = lifetime spend, rank by lifetime spend in the
    /// network; favourite games come with launch reports (S3). A player without history gets zeros and rank 0.
    /// </summary>
    private static async Task<IResult> StatsAsync(Guid userId, NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        var (count, seconds, spent, network) = await c.QuerySingleAsync<(int, long, long, Guid)>(
            """
            SELECT (SELECT count(*)::int FROM sessions WHERE user_id = @userId),
                   (SELECT coalesce(sum(used_before_sec), 0)::bigint FROM sessions WHERE user_id = @userId AND state = 'ended'),
                   w.lifetime_spent, w.network_id
            FROM wallets w WHERE w.user_id = @userId
            """,
            new { userId });
        var rank = count == 0 && spent == 0 ? 0
            : 1 + await c.ExecuteScalarAsync<int>("SELECT count(*)::int FROM wallets WHERE network_id = @network AND lifetime_spent > @spent", new { network, spent });
        return TypedResults.Ok(new UserStats(Math.Round(seconds / 3600.0, 2), count, [], Money.Uzs(spent), rank));
    }

    /// <summary>The club's loyalty levels by lifetime spend (§5.13 item 5); perks are the level's discount in the request language.</summary>
    private static async Task<IResult> LoyaltyAsync(HttpContext context, Guid userId, NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        var club = await SessionService.ClubAsync(c, null, context.Features.GetRequiredFeature<AgentContext>().Pc.ClubId);
        var spent = await c.ExecuteScalarAsync<long>("SELECT lifetime_spent FROM wallets WHERE user_id = @userId", new { userId });
        var level = club.Pricing.LevelOf(spent);
        var next = club.Pricing.Loyalty.FirstOrDefault(l => l.MinSpent > spent);
        var language = context.Request.Headers.AcceptLanguage.ToString();
        var perks = level is { DiscountPct: > 0 } l
            ? [language.StartsWith("uz", StringComparison.OrdinalIgnoreCase) ? $"O‘yin vaqtiga {l.DiscountPct}% chegirma"
                : language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? $"{l.DiscountPct}% off play time"
                : $"Скидка {l.DiscountPct}% на игровое время"]
            : Array.Empty<string>();
        return TypedResults.Ok(new Loyalty(
            Math.Max(0, (level?.Level ?? 1) - 1), UserRow.Points(spent), next is null ? UserRow.Points(spent) : UserRow.Points(next.MinSpent), perks));
    }

    [GeneratedRegex("^[0-9]{4,6}$")]
    private static partial Regex PinPattern();
}
