using ClubShell.Contracts.Commands;
using ClubShell.Server.Admin;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace ClubShell.Server.Games;

/// <summary>
/// The club server's trailers over HTTP (D-97, beyond the contract). <c>GET /api/v1/trailers/{id}</c> serves a stored copy
/// to anyone (a PC's shell has no staff token) with byte ranges (<c>206</c>, <c>416</c>; the player seeks and WebView2
/// asks in pieces), cached for good (a new copy has a new id) and never sniffed. The owner: <c>POST
/// /api/v1/admin/games/{id}/trailer</c> uploads one as the raw body — an MP4 (H.264 plays everywhere) or WebM of at most
/// <see cref="TrailerOptions.MaxBytes"/>, recognised by its first bytes; it replaces the card's link and is never
/// replaced by the finder or the seed. <c>DELETE</c> takes the game's trailer off (and the finder leaves it alone).
/// <c>POST /api/v1/admin/games/trailers/find</c> — «Найти трейлеры» — searches again for every game without one or whose
/// link failed
/// (<see cref="TrailerStore.SearchAgainAsync"/>) and answers <c>{queued, gameIds}</c>; the console follows their
/// <c>trailer.status</c> in the games list.
/// </summary>
public static class TrailerEndpoints
{
    public static void MapTrailerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapApiGroup("/api/v1/trailers").WithMetadata(new AuthRequirement(AuthMode.None)).MapGet("/{fileId:guid}", ServeAsync);
        var admin = app.MapApiGroup("/api/v1/admin/games").WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        admin.MapPost("/{id:guid}/trailer", UploadAsync);
        admin.MapDelete("/{id:guid}/trailer", RemoveAsync);
        admin.MapPost("/trailers/find", FindAsync);
    }

    private static async Task<IResult> ServeAsync(HttpContext context, Guid fileId, NpgsqlDataSource db, TrailerStore store)
    {
        await using var c = await db.OpenConnectionAsync();
        var row = await c.QuerySingleOrDefaultAsync<TrailerRow>(
            $"SELECT {TrailerRow.Columns} FROM game_trailers WHERE file_id = @fileId AND status = 'ready'", new { fileId });
        var file = store.ReadyFile(row) ?? throw ApiException.NotFound("trailer");
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return Results.File(
            Path.GetFullPath(file), row!.ContentType, enableRangeProcessing: true, lastModified: row.UpdatedAt,
            entityTag: new EntityTagHeaderValue($"\"{row.Sha256}\""));
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context, Guid id, NpgsqlDataSource db, TrailerStore store, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var max = store.Options.MaxBytes;
        var tooLarge = $"At most {max / 1024 / 1024} MB";
        if (context.Request.ContentLength is { } length && length > max)
        {
            throw ApiException.Validation("file", "max", tooLarge);
        }

        await using (var c = await db.OpenConnectionAsync())
        {
            if (!await c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM games WHERE id = @id AND club_id = @ClubId AND deleted_at IS NULL)", new { id, staff.ClubId }))
            {
                throw ApiException.NotFound("game");
            }
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max + 1;
        }

        (Guid FileId, string ContentType, long Size, string Sha256) stored;
        try
        {
            stored = await store.WriteAsync(
                context.Request.Body, context.Request.ContentType, Guid.CreateVersion7(clock.GetUtcNow()), context.RequestAborted);
        }
        catch (TrailerRefusedException refused)
        {
            throw refused.Reason switch
            {
                "empty" => ApiException.Validation("file", "required"),
                "tooLarge" => ApiException.Validation("file", "max", tooLarge),
                _ => ApiException.Validation("file", "format", "Only an MP4 (H.264) or WebM video file"),
            };
        }

        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued;
        TrailerRow row;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            var title = await c.QuerySingleOrDefaultAsync<string>(
                """
                UPDATE games SET data = jsonb_set(data, '{videoUrl}', 'null'::jsonb), origin = 'club', updated_at = @now
                WHERE id = @id AND club_id = @ClubId AND deleted_at IS NULL
                RETURNING title
                """,
                new { id, staff.ClubId, now }, tx);
            if (title is null)
            {
                File.Delete(store.FilePath(stored.FileId, stored.ContentType));
                throw ApiException.NotFound("game");
            }

            row = await c.QuerySingleAsync<TrailerRow>(
                $"""
                INSERT INTO game_trailers (game_id, club_id, origin, source_url, status, file_id, content_type, size, sha256, next_try_at, updated_at)
                VALUES (@id, @ClubId, 'upload', NULL, 'ready', @FileId, @ContentType, @Size, @Sha256, @now, @now)
                ON CONFLICT (game_id) DO UPDATE SET origin = 'upload', source_url = NULL, status = 'ready', reason = NULL, file_id = excluded.file_id,
                    content_type = excluded.content_type, size = excluded.size, sha256 = excluded.sha256, attempts = 0, updated_at = excluded.updated_at,
                    failed_link = NULL, failed_reason = NULL
                RETURNING {TrailerRow.Columns}
                """,
                new { id, staff.ClubId, stored.FileId, stored.ContentType, stored.Size, stored.Sha256, now }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "gameTrailerUpload", detail: title, meta: new { gameId = id, size = stored.Size, contentType = stored.ContentType });
            queued = await TrailerStore.ChangedAsync(c, tx, commands, hub, staff.ClubId, now, staff.StaffId);
            await tx.CommitAsync();
        }

        await ConfigRefresh.SendAsync(commands, queued);
        return AdminJson.Ok(new { trailer = TrailerStore.Wire(row) }, StatusCodes.Status201Created);
    }

    private static async Task<IResult> RemoveAsync(
        HttpContext context, Guid id, NpgsqlDataSource db, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            var title = await c.QuerySingleOrDefaultAsync<string>(
                """
                UPDATE games SET data = jsonb_set(data, '{videoUrl}', 'null'::jsonb), origin = 'club', updated_at = @now
                WHERE id = @id AND club_id = @ClubId AND deleted_at IS NULL
                RETURNING title
                """,
                new { id, staff.ClubId, now }, tx)
                ?? throw ApiException.NotFound("game");
            await c.ExecuteAsync(
                """
                INSERT INTO game_trailers (game_id, club_id, origin, status, reason, next_try_at, updated_at)
                VALUES (@id, @ClubId, 'found', 'failed', 'removed', @now, @now)
                ON CONFLICT (game_id) DO UPDATE SET origin = 'found', source_url = NULL, status = 'failed', reason = 'removed', file_id = NULL,
                    content_type = NULL, size = NULL, sha256 = NULL, attempts = 0, updated_at = @now, failed_link = NULL, failed_reason = NULL
                """,
                new { id, staff.ClubId, now }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "gameTrailerRemove", detail: title, meta: new { gameId = id });
            queued = await TrailerStore.ChangedAsync(c, tx, commands, hub, staff.ClubId, now, staff.StaffId);
            await tx.CommitAsync();
        }

        await ConfigRefresh.SendAsync(commands, queued);
        return AdminJson.Ok(AdminJson.OkBody);
    }

    private static async Task<IResult> FindAsync(HttpContext context, NpgsqlDataSource db, TrailerStore store, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        IReadOnlyList<Guid> ids;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            ids = await store.SearchAgainAsync(c, tx, staff.ClubId, now);
            await Audit.WriteAsync(c, tx, staff, now, "gameTrailersFind", detail: ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await tx.CommitAsync();
        }

        return AdminJson.Ok(new { queued = ids.Count, gameIds = ids });
    }
}
