using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Server.Admin;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Platform;

/// <summary><c>Platform:*</c>: the platform administrator's key. Shorter than <see cref="MinKeyLength"/> turns the platform off.</summary>
public sealed class PlatformOptions
{
    public const int MinKeyLength = 24;

    public string AdminKey { get; set; } = "";

    public bool Enabled => AdminKey.Length >= MinKeyLength;
}

/// <summary>
/// Platform administration (beyond the contract, DESIGN §11 "Платформа"): the operator of a server holding several clubs
/// creates clubs, issues their enrollment keys, adds owners and resets a forgotten owner PIN, and disables a club. Auth is
/// <c>Authorization: Bearer &lt;Platform:AdminKey&gt;</c>; without a configured key every route answers 404, as if it did not
/// exist. A secret (enrollment key) is returned only by the call that creates it; nothing here is logged with secrets.
/// </summary>
public static class PlatformEndpoints
{
    private const string DefaultTimeZone = "Asia/Tashkent";

    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/platform/clubs");
        var platform = new AuthRequirement(AuthMode.Platform);
        api.MapGet("", ListAsync).WithMetadata(platform);
        api.MapPost("", CreateAsync).WithMetadata(platform);
        api.MapPatch("/{id:guid}", UpdateAsync).WithMetadata(platform);
        api.MapPost("/{id:guid}/enrollment-key", RotateKeyAsync).WithMetadata(platform);
        api.MapPost("/{id:guid}/owners", AddOwnerAsync).WithMetadata(platform);
        api.MapPost("/{id:guid}/owners/{staffId:guid}/pin", ResetPinAsync).WithMetadata(platform);
    }

    /// <summary>Middleware check for <see cref="AuthMode.Platform"/>: 404 when the platform is off, 401 for any other key.</summary>
    public static void Authenticate(HttpContext context, PlatformOptions options, ILogger logger)
    {
        if (!options.Enabled)
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCode.NotFound, "Not found", new { route = context.Request.Path.Value });
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(AgentAuthMiddleware.BearerToken(context) ?? ""));
        if (!CryptographicOperations.FixedTimeEquals(presented, SHA256.HashData(Encoding.UTF8.GetBytes(options.AdminKey))))
        {
            logger.LogWarning("Wrong platform key from {Ip}", context.Connection.RemoteIpAddress);
            throw ApiException.Unauthorized("invalid", "Missing or invalid platform key");
        }
    }

    private static async Task<IResult> ListAsync(NpgsqlDataSource db, ClubRepository clubs, ClubOptions bootstrap)
    {
        await using var c = await db.OpenConnectionAsync();
        var keyClub = string.IsNullOrEmpty(bootstrap.EnrollmentKey) ? null : await clubs.BootstrapClubIdAsync();
        var rows = await c.QueryAsync<(Guid Id, string Name, string? Code, string TimeZone, bool Disabled, DateTime CreatedAt, long Pcs)>(
            """
            SELECT c.id, c.name, c.code, c.time_zone, c.disabled, c.created_at,
                   (SELECT count(*) FROM pcs p WHERE p.club_id = c.id AND p.deleted_at IS NULL)
            FROM clubs c ORDER BY c.created_at, c.id
            """);
        var owners = (await c.QueryAsync<(Guid ClubId, Guid Id, string Name, bool Active)>(
            "SELECT club_id, id, name, active FROM staff WHERE role = 'owner' ORDER BY created_at, id")).ToLookup(o => o.ClubId);
        var items = rows.Select(r => new PlatformClub(
            r.Id, r.Name, r.Code ?? "", r.TimeZone, r.Disabled, new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)), (int)r.Pcs,
            r.Id == keyClub, owners[r.Id].Select(o => new PlatformOwner(o.Id, o.Name, o.Active)).ToList())).ToList();
        return AdminJson.Ok(new { items });
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] JsonElement body, NpgsqlDataSource db, StaffTokens tokens, ClubSeeds seeds, IServiceProvider services, TimeProvider clock,
        ILogger<PlatformLog> logger)
    {
        var r = Api.Read<PlatformClubCreateRequest>(body, "name", "ownerName", "ownerPin");
        var name = AdminInput.Text(r.Name?.Trim(), "name", 80);
        var code = r.Code is null ? ClubRepository.NewCode() : Code(r.Code);
        var timeZone = ZoneOf(r.TimeZone);
        var ownerName = AdminInput.Text(r.OwnerName?.Trim(), "ownerName", 64);
        var hmac = tokens.PinHmac(StaffAdminEndpoints.Pin(r.OwnerPin!));
        var key = ClubRepository.NewEnrollmentKey();

        var now = clock.GetUtcNow();
        Guid clubId;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            (clubId, var networkId) = await CodeWriteAsync(c, tx, () => ClubRepository.CreateAsync(c, tx, name, code, timeZone, key, now));
            await c.ExecuteAsync(
                """
                INSERT INTO staff (id, network_id, club_id, name, role, pin_hmac, active, created_at, updated_at)
                VALUES (@id, @networkId, @clubId, @ownerName, 'owner', @hmac, true, @now, @now)
                """,
                new { id = Guid.CreateVersion7(now), networkId, clubId, ownerName, hmac, now },
                tx);
            await tx.CommitAsync();
        }

        // The policy, games and products seeds of the server, as every club gets them at startup.
        await seeds.ApplyAsync(services);
        logger.LogInformation("Platform: club {ClubId} ({Name}, code {Code}) created", clubId, name, code);
        return AdminJson.Ok(new { club = await ClubAsync(db, clubId), enrollmentKey = key }, StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(Guid id, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock, ILogger<PlatformLog> logger)
    {
        var r = Api.Read<PlatformClubUpdateRequest>(body);
        var name = r.Name is null ? null : AdminInput.Text(r.Name.Trim(), "name", 80);
        var code = r.Code is null ? null : Code(r.Code);
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var rows = await CodeWriteAsync(c, tx, () => c.ExecuteAsync(
                """
                UPDATE clubs SET name = coalesce(@name, name), code = coalesce(@code, code), disabled = coalesce(@Disabled, disabled),
                                 updated_at = @now
                WHERE id = @id
                """,
                new { id, name, code, r.Disabled, now = clock.GetUtcNow() },
                tx));
            if (rows == 0)
            {
                throw ApiException.NotFound("club");
            }

            await tx.CommitAsync();
        }

        if (r.Disabled is { } disabled)
        {
            logger.LogInformation("Platform: club {ClubId} {State}", id, disabled ? "disabled" : "enabled");
        }

        return AdminJson.Ok(new { club = await ClubAsync(db, id) });
    }

    private static async Task<IResult> RotateKeyAsync(Guid id, NpgsqlDataSource db, ClubRepository clubs, ClubOptions bootstrap, TimeProvider clock, ILogger<PlatformLog> logger)
    {
        if (!string.IsNullOrEmpty(bootstrap.EnrollmentKey) && await clubs.BootstrapClubIdAsync() == id)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "This club's key comes from PlatformClub:EnrollmentKey; rotate it there",
                new { reason = "keyFromConfig" });
        }

        var key = ClubRepository.NewEnrollmentKey();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        if (await ClubRepository.RotateEnrollmentKeyAsync(c, tx, id, key, clock.GetUtcNow()) == 0)
        {
            throw ApiException.NotFound("club");
        }

        await tx.CommitAsync();
        logger.LogInformation("Platform: enrollment key of club {ClubId} rotated", id);
        return AdminJson.Ok(new { enrollmentKey = key });
    }

    private static async Task<IResult> AddOwnerAsync(Guid id, [FromBody] JsonElement body, NpgsqlDataSource db, StaffTokens tokens, TimeProvider clock)
    {
        var r = Api.Read<PlatformOwnerRequest>(body, "name", "pin");
        var name = AdminInput.Text(r.Name?.Trim(), "name", 64);
        var hmac = tokens.PinHmac(StaffAdminEndpoints.Pin(r.Pin!));
        var now = clock.GetUtcNow();
        var staffId = Guid.CreateVersion7(now);
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var networkId = await c.QuerySingleOrDefaultAsync<Guid?>("SELECT network_id FROM clubs WHERE id = @id FOR UPDATE", new { id }, tx)
            ?? throw ApiException.NotFound("club");
        await StaffAdminEndpoints.PinWriteAsync(c, tx, () => c.ExecuteAsync(
            """
            INSERT INTO staff (id, network_id, club_id, name, role, pin_hmac, active, created_at, updated_at)
            VALUES (@staffId, @networkId, @id, @name, 'owner', @hmac, true, @now, @now)
            """,
            new { staffId, networkId, id, name, hmac, now },
            tx));
        await tx.CommitAsync();
        return AdminJson.Ok(new { owner = new PlatformOwner(staffId, name, true) }, StatusCodes.Status201Created);
    }

    /// <summary>A forgotten owner PIN: new PIN, the owner active again, every earlier token of theirs revoked.</summary>
    private static async Task<IResult> ResetPinAsync(Guid id, Guid staffId, [FromBody] JsonElement body, NpgsqlDataSource db, StaffTokens tokens, TimeProvider clock, ILogger<PlatformLog> logger)
    {
        var r = Api.Read<PlatformPinRequest>(body, "pin");
        var hmac = tokens.PinHmac(StaffAdminEndpoints.Pin(r.Pin!));
        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var rows = 0;
        await StaffAdminEndpoints.PinWriteAsync(c, tx, async () => rows = await c.ExecuteAsync(
            "UPDATE staff SET pin_hmac = @hmac, active = true, updated_at = @now WHERE id = @staffId AND club_id = @id AND role = 'owner'",
            new { staffId, id, hmac, now },
            tx));
        if (rows == 0)
        {
            throw ApiException.NotFound("owner");
        }

        await c.ExecuteAsync("UPDATE staff_tokens SET revoked_at = @now WHERE staff_id = @staffId AND revoked_at IS NULL", new { staffId, now }, tx);
        await tx.CommitAsync();
        logger.LogInformation("Platform: PIN of owner {StaffId} of club {ClubId} reset", staffId, id);
        return AdminJson.Ok(new { ok = true });
    }

    private static async Task<PlatformClub> ClubAsync(NpgsqlDataSource db, Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        var r = await c.QuerySingleAsync<(Guid Id, string Name, string? Code, string TimeZone, bool Disabled, DateTime CreatedAt, long Pcs)>(
            """
            SELECT c.id, c.name, c.code, c.time_zone, c.disabled, c.created_at,
                   (SELECT count(*) FROM pcs p WHERE p.club_id = c.id AND p.deleted_at IS NULL)
            FROM clubs c WHERE c.id = @id
            """,
            new { id });
        var owners = await c.QueryAsync<PlatformOwner>("SELECT id, name, active FROM staff WHERE club_id = @id AND role = 'owner' ORDER BY created_at, id", new { id });
        return new PlatformClub(r.Id, r.Name, r.Code ?? "", r.TimeZone, r.Disabled, new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)), (int)r.Pcs,
            KeyFromConfig: false, owners.ToList());
    }

    /// <summary>3–12 letters and digits, stored upper-case; else <c>400 code format</c>.</summary>
    private static string Code(string code)
    {
        var upper = code.Trim().ToUpperInvariant();
        return upper.Length is >= 3 and <= 12 && upper.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9') ? upper : throw ApiException.Validation("code", "format");
    }

    private static string ZoneOf(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
        {
            return DefaultTimeZone;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(zone.Trim(), out _) ? zone.Trim() : throw ApiException.Validation("timeZone", "format");
    }

    /// <summary>A code another club holds is <c>400 code taken</c>.</summary>
    private static async Task<T> CodeWriteAsync<T>(NpgsqlConnection c, NpgsqlTransaction tx, Func<Task<T>> write)
    {
        await c.ExecuteAsync("SAVEPOINT code", transaction: tx);
        try
        {
            return await write();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "clubs_code")
        {
            await c.ExecuteAsync("ROLLBACK TO SAVEPOINT code", transaction: tx);
            throw ApiException.Validation("code", "taken");
        }
    }
}

public sealed record PlatformClubCreateRequest(string? Name, string? Code, string? TimeZone, string? OwnerName, string? OwnerPin);

public sealed record PlatformClubUpdateRequest(string? Name, string? Code, bool? Disabled);

public sealed record PlatformOwnerRequest(string? Name, string? Pin);

public sealed record PlatformPinRequest(string? Pin);

public sealed record PlatformOwner(Guid Id, string Name, bool Active);

/// <summary><paramref name="KeyFromConfig"/>: the bootstrap club while <c>PlatformClub:EnrollmentKey</c> is set; its key is rotated in config.</summary>
public sealed record PlatformClub(Guid Id, string Name, string Code, string TimeZone, bool Disabled, DateTimeOffset CreatedAt, int Pcs, bool KeyFromConfig, IReadOnlyList<PlatformOwner> Owners);

/// <summary>Logger category of the platform administration.</summary>
public sealed class PlatformLog;
