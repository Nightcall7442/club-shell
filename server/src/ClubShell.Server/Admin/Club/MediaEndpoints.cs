using System.Security.Cryptography;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The club's own pictures (D-93, beyond the contract). The owner uploads a logo, wallpaper or banner as the raw body
/// (<c>POST /api/v1/admin/media?kind=logo</c>, <c>Content-Type</c> of the picture, at most <see cref="MaxBytes"/>): only
/// PNG, JPEG, WebP and GIF, recognised by their first bytes, never by the name or the header the browser sent — an SVG
/// or an HTML page cannot pass as a logo. The answer is <c>{id, url}</c> with <c>url</c> = <c>/api/v1/media/{id}</c>,
/// relative to the server: the console and the agent put their own server address in front of it. <c>GET
/// /api/v1/media/{id}</c> serves it to anyone (a PC's lock screen has no staff token), immutable, as the type it is.
/// </summary>
public static class MediaEndpoints
{
    public const int MaxBytes = 5 * 1024 * 1024;

    private static readonly string[] Kinds = ["logo", "wallpaper", "banner", "screensaver", "other"];

    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapApiGroup("/api/v1/admin/media").WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true)).MapPost("", UploadAsync);
        app.MapApiGroup("/api/v1/media").WithMetadata(new AuthRequirement(AuthMode.None)).MapGet("/{id:guid}", ServeAsync);
    }

    /// <summary>The picture's type from its first bytes, or null when it is none of the four.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)) ? "image/gif"
        : null;

    private static async Task<IResult> UploadAsync(HttpContext context, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var kind = context.Request.Query["kind"].ToString();
        if (kind.Length == 0)
        {
            kind = "other";
        }

        if (!Kinds.Contains(kind))
        {
            throw ApiException.Validation("kind", "enum");
        }

        if (context.Request.ContentLength is > MaxBytes)
        {
            throw ApiException.Validation("file", "max", $"At most {MaxBytes / 1024 / 1024} MB");
        }

        using var buffer = new MemoryStream();
        var read = new byte[81920];
        int n;
        while ((n = await context.Request.Body.ReadAsync(read, context.RequestAborted)) > 0)
        {
            if (buffer.Length + n > MaxBytes)
            {
                throw ApiException.Validation("file", "max", $"At most {MaxBytes / 1024 / 1024} MB");
            }

            buffer.Write(read, 0, n);
        }

        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            throw ApiException.Validation("file", "required");
        }

        var type = Sniff(bytes) ?? throw ApiException.Validation("file", "format", "Only PNG, JPEG, WebP or GIF pictures");
        var id = Guid.CreateVersion7(clock.GetUtcNow());
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO club_media (id, club_id, kind, content_type, bytes, sha256, created_at)
            VALUES (@id, @ClubId, @kind, @type, @bytes, @sha, @now)
            """,
            new { id, staff.ClubId, kind, type, bytes, sha = Convert.ToHexStringLower(SHA256.HashData(bytes)), now = clock.GetUtcNow() });
        return Results.Json(new { id, url = Url(id), contentType = type, size = bytes.Length }, ServerJson.Options, statusCode: StatusCodes.Status201Created);
    }

    public static string Url(Guid id) => $"/api/v1/media/{id}";

    private static async Task<IResult> ServeAsync(HttpContext context, Guid id, NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        var media = await c.QuerySingleOrDefaultAsync<(string ContentType, byte[] Bytes)?>(
            "SELECT content_type, bytes FROM club_media WHERE id = @id", new { id })
            ?? throw ApiException.NotFound("media");
        // A media id never changes its bytes: cached for good by the PCs and the console.
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return Results.Bytes(media.Bytes, media.ContentType);
    }
}
