using System.Security.Cryptography;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Server.Admin;
using ClubShell.Server.Realtime;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Games;

/// <summary>A <c>game_trailers</c> row (M0026).</summary>
public sealed class TrailerRow
{
    public const string Columns =
        "game_id, club_id, origin, source_url, status, reason, file_id, content_type, size, sha256, attempts, next_try_at, updated_at, failed_link, failed_reason";

    /// <summary>A card's link failing for one of these is replaced by a search (M0027): it gives no trailer as it is.</summary>
    public static readonly string[] ReplaceableReasons = ["notAVideo", "streamingFormat", "tooLarge", "unreachable", "unavailable"];

    public Guid GameId { get; init; }
    public Guid ClubId { get; init; }
    public string Origin { get; init; } = "link";
    public string? SourceUrl { get; init; }
    public string Status { get; init; } = "pending";
    public string? Reason { get; init; }
    public Guid? FileId { get; init; }
    public string? ContentType { get; init; }
    public long? Size { get; init; }
    public string? Sha256 { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset NextTryAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? FailedLink { get; init; }
    public string? FailedReason { get; init; }
}

/// <summary>
/// A game's trailer as the console shows it (beyond the contract, D-97): <c>status</c> — <c>searching</c> (the finder
/// has not looked yet), <c>downloading</c>, <c>ready</c> (<c>url</c> is the stored copy on this server, relative), or
/// <c>failed</c> with a <c>reason</c> (<c>notAVideo</c>, <c>streamingFormat</c>, <c>tooLarge</c>, <c>unavailable</c>,
/// <c>unreachable</c>, <c>notFound</c> — the finder found none, <c>removed</c> — the owner took it off, <c>fileLost</c>);
/// <c>origin</c> — <c>link</c> (the owner's or the seed's), <c>found</c> (the finder's), <c>upload</c>. <c>failedLink</c>
/// and <c>failedReason</c>: the card's link that gave no trailer and why, when the finder looks (or found one) instead.
/// </summary>
public sealed record AdminTrailer(
    string Status, string Origin, string? SourceUrl, string? Url, string? ContentType, long? Size, string? Reason, string? FailedLink = null,
    string? FailedReason = null);

/// <summary>A file that is not a trailer the server keeps, with the reason the console shows.</summary>
public sealed class TrailerRefusedException(string reason) : Exception($"Trailer refused: {reason}")
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The club server's trailers (D-97): rows in <c>game_trailers</c>, files under <see cref="TrailerOptions.StoragePath"/>.
/// <see cref="SyncAsync"/> keeps the rows in step with the cards: a game whose card has an http(s) link gets that link
/// to download (again, when the link changed); a game without one gets a search (once; the owner's «Найти трейлеры»
/// asks again); a link taken off a card lets the finder look again. An upload is never replaced by a search or the seed.
/// <see cref="Apply"/> is what a PC gets: the stored copy once it is ready, else the original link while it is being
/// fetched; a link known not to be a video (a YouTube page, a playlist) is not sent at all.
/// </summary>
public sealed class TrailerStore(TrailerOptions options)
{
    public const string UrlPrefix = "/api/v1/trailers/";

    public static string Url(Guid fileId) => UrlPrefix + fileId;

    public TrailerOptions Options => options;

    public string FilePath(Guid fileId, string contentType) => Path.Combine(options.StoragePath, fileId + TrailerFormat.Extension(contentType));

    /// <summary>The stored copy's file, when the row has one and it is still on disk.</summary>
    public string? ReadyFile(TrailerRow? row) =>
        row is { Status: "ready", FileId: { } id, ContentType: { } type } && File.Exists(FilePath(id, type)) ? FilePath(id, type) : null;

    /// <summary>
    /// <paramref name="game"/> (its card as stored) with the trailer a PC plays: the stored copy (and the original link
    /// as <c>videoSourceUrl</c>), else the card's link — not when the server found it is no video — else a link the
    /// finder found that is still being downloaded.
    /// </summary>
    public Game Apply(Game game, TrailerRow? row)
    {
        if (ReadyFile(row) is not null)
        {
            return game with { VideoUrl = Url(row!.FileId!.Value), VideoSourceUrl = IsWebLink(row.SourceUrl) ? row.SourceUrl : null };
        }

        var link = IsWebLink(game.VideoUrl) ? game.VideoUrl : null;
        if ((row is { Status: "failed", Reason: "notAVideo" or "streamingFormat" } && row.SourceUrl == link)
            || (row is { FailedReason: "notAVideo" or "streamingFormat" } && row.FailedLink == link))
        {
            link = null;
        }

        if (link is null && row is { Origin: "found", Status: "pending", SourceUrl: { } found } && IsWebLink(found))
        {
            link = found;
        }

        return game with { VideoUrl = link, VideoSourceUrl = null };
    }

    public static AdminTrailer? Wire(TrailerRow? row) =>
        row is null || row.Reason == "removed"
            ? null
            : new AdminTrailer(
                row.Status switch { "ready" => "ready", "failed" => "failed", _ => row.SourceUrl is null ? "searching" : "downloading" },
                row.Origin, row.SourceUrl, row is { Status: "ready", FileId: { } id } ? Url(id) : null, row.ContentType, row.Size, row.Reason,
                row.FailedLink, row.FailedReason);

    public static bool IsWebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public static async Task<Dictionary<Guid, TrailerRow>> RowsAsync(NpgsqlConnection c, Guid clubId, NpgsqlTransaction? tx = null) =>
        (await c.QueryAsync<TrailerRow>($"SELECT {TrailerRow.Columns} FROM game_trailers WHERE club_id = @clubId", new { clubId }, tx))
            .ToDictionary(r => r.GameId);

    public static Task<TrailerRow?> RowAsync(NpgsqlConnection c, Guid gameId, NpgsqlTransaction? tx = null) =>
        c.QuerySingleOrDefaultAsync<TrailerRow>($"SELECT {TrailerRow.Columns} FROM game_trailers WHERE game_id = @gameId", new { gameId }, tx);

    /// <summary>
    /// Brings the rows of the live games (of <paramref name="clubId"/>, or only <paramref name="gameId"/>; null — all)
    /// in step with their cards; see the class. Searches are added only with <see cref="TrailerOptions.AutoFind"/>.
    /// </summary>
    public async Task SyncAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid? clubId, Guid? gameId, DateTimeOffset now)
    {
        const string Games = "g.deleted_at IS NULL AND (@clubId::uuid IS NULL OR g.club_id = @clubId) AND (@gameId::uuid IS NULL OR g.id = @gameId)";
        const string Link = "coalesce(g.data ->> 'videoUrl', '') ~* '^https?://'";
        var args = new { clubId, gameId, now, auto = options.AutoFind };
        await c.ExecuteAsync(
            $"""
            UPDATE game_trailers t SET origin = 'found', source_url = NULL, status = 'pending', reason = NULL, file_id = NULL, content_type = NULL,
                                       size = NULL, sha256 = NULL, attempts = 0, next_try_at = @now, updated_at = @now, failed_link = NULL,
                                       failed_reason = NULL
            FROM games g
            WHERE g.id = t.game_id AND (t.origin = 'link' OR t.failed_link IS NOT NULL) AND NOT {Link} AND {Games};

            INSERT INTO game_trailers (game_id, club_id, origin, source_url, status, next_try_at, updated_at)
            SELECT g.id, g.club_id, 'link', g.data ->> 'videoUrl', 'pending', @now, @now
            FROM games g LEFT JOIN game_trailers t ON t.game_id = g.id
            WHERE {Games} AND {Link} AND (t.game_id IS NULL OR (t.source_url IS DISTINCT FROM g.data ->> 'videoUrl'
                                                               AND t.failed_link IS DISTINCT FROM g.data ->> 'videoUrl'))
            ON CONFLICT (game_id) DO UPDATE SET origin = 'link', source_url = excluded.source_url, status = 'pending', reason = NULL, file_id = NULL,
                                                content_type = NULL, size = NULL, sha256 = NULL, attempts = 0, next_try_at = @now, updated_at = @now,
                                                failed_link = NULL, failed_reason = NULL;

            INSERT INTO game_trailers (game_id, club_id, origin, status, next_try_at, updated_at)
            SELECT g.id, g.club_id, 'found', 'pending', @now, @now
            FROM games g
            WHERE @auto AND {Games} AND NOT {Link} AND NOT EXISTS (SELECT 1 FROM game_trailers t WHERE t.game_id = g.id)
            ON CONFLICT (game_id) DO NOTHING;
            """,
            args, tx);
    }

    /// <summary>
    /// The owner's «Найти трейлеры»: a new search for every live game of the club without a working trailer — none yet,
    /// one the finder did not find or could not fetch, or a card's link that failed (<see cref="TrailerRow.ReplaceableReasons"/>;
    /// a file found replaces it). An upload, a ready link and a trailer the owner took off stay as they are. Returns the
    /// games searched for.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SearchAgainAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, DateTimeOffset now)
    {
        await SyncAsync(c, tx, clubId, null, now);
        var links = await c.QueryAsync<Guid>(
            """
            UPDATE game_trailers SET origin = 'found', failed_link = source_url, failed_reason = reason, source_url = NULL, status = 'pending',
                                     reason = NULL, file_id = NULL, content_type = NULL, size = NULL, sha256 = NULL, attempts = 0,
                                     next_try_at = @now, updated_at = @now
            WHERE club_id = @clubId AND origin = 'link' AND status = 'failed' AND reason = ANY(@reasons)
              AND EXISTS (SELECT 1 FROM games g WHERE g.id = game_id AND g.deleted_at IS NULL)
            RETURNING game_id
            """,
            new { clubId, now, reasons = TrailerRow.ReplaceableReasons }, tx);
        List<Guid> ids = [.. links, .. await c.QueryAsync<Guid>(
            """
            INSERT INTO game_trailers (game_id, club_id, origin, status, next_try_at, updated_at)
            SELECT g.id, g.club_id, 'found', 'pending', @now, @now
            FROM games g LEFT JOIN game_trailers t ON t.game_id = g.id
            WHERE g.club_id = @clubId AND g.deleted_at IS NULL AND coalesce(g.data ->> 'videoUrl', '') !~* '^https?://'
              AND (t.game_id IS NULL OR (t.origin = 'found' AND t.status = 'failed' AND t.reason IS DISTINCT FROM 'removed') OR (t.origin = 'found' AND t.status = 'pending' AND t.source_url IS NULL))
            ON CONFLICT (game_id) DO UPDATE SET source_url = NULL, status = 'pending', reason = NULL, file_id = NULL, content_type = NULL, size = NULL,
                                                sha256 = NULL, attempts = 0, next_try_at = @now, updated_at = @now
            RETURNING game_id
            """,
            new { clubId, now }, tx), .. await c.QueryAsync<Guid>(
            """
            UPDATE game_trailers t SET source_url = NULL, status = 'pending', reason = NULL, file_id = NULL, content_type = NULL, size = NULL,
                                       sha256 = NULL, attempts = 0, next_try_at = @now, updated_at = @now
            FROM games g
            WHERE g.id = t.game_id AND t.club_id = @clubId AND g.deleted_at IS NULL AND t.failed_link IS NOT NULL AND t.origin = 'found'
              AND (t.status = 'failed' OR (t.status = 'pending' AND t.source_url IS NULL)) AND t.reason IS DISTINCT FROM 'removed'
              AND g.data ->> 'videoUrl' = t.failed_link
            RETURNING t.game_id
            """,
            new { clubId, now }, tx)];
        return ids.Distinct().ToList();
    }

    /// <summary>
    /// Streams <paramref name="body"/> into a new stored file: refused (<see cref="TrailerRefusedException"/>) unless its
    /// first bytes are an mp4/webm, or when it grows past <see cref="TrailerOptions.MaxBytes"/>. Nothing is kept of a
    /// refused file.
    /// </summary>
    public async Task<(Guid FileId, string ContentType, long Size, string Sha256)> WriteAsync(
        Stream body, string? declaredType, Guid fileId, CancellationToken cancellationToken)
    {
        var head = new byte[TrailerFormat.HeadBytes];
        var filled = 0;
        int n;
        while (filled < head.Length && (n = await body.ReadAsync(head.AsMemory(filled), cancellationToken)) > 0)
        {
            filled += n;
        }

        if (filled == 0)
        {
            throw new TrailerRefusedException("empty");
        }

        var type = TrailerFormat.Sniff(head.AsSpan(0, filled)) ?? throw new TrailerRefusedException(TrailerFormat.Refusal(head.AsSpan(0, filled), declaredType));
        Directory.CreateDirectory(options.StoragePath);
        var file = FilePath(fileId, type);
        var temp = file + ".part";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = filled;
        try
        {
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                if (size > options.MaxBytes)
                {
                    throw new TrailerRefusedException("tooLarge");
                }

                hash.AppendData(head, 0, filled);
                await target.WriteAsync(head.AsMemory(0, filled), cancellationToken);
                var buffer = new byte[81920];
                while ((n = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += n;
                    if (size > options.MaxBytes)
                    {
                        throw new TrailerRefusedException("tooLarge");
                    }

                    hash.AppendData(buffer, 0, n);
                    await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                }
            }

            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }

        return (fileId, type, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>A trailer a PC plays changed: <c>catalog_version</c> moves and the club's connected PCs reload the games.</summary>
    public static async Task<IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)>> ChangedAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, CommandDispatcher commands, AgentSocketHub hub, Guid clubId, DateTimeOffset now, Guid? staffId = null)
    {
        await c.ExecuteAsync("UPDATE clubs SET catalog_version = catalog_version + 1, updated_at = @now WHERE id = @clubId", new { clubId, now }, tx);
        return await ConfigRefresh.QueueAsync(c, tx, commands, hub, clubId, new RefreshConfigCommand(Games: true), staffId);
    }

    /// <summary>
    /// Files no row names any more (a replaced or removed copy, a download the owner overtook) and half-written ones, older
    /// than an hour; a copy a PC is still playing has had its hour.
    /// </summary>
    public async Task SweepAsync(NpgsqlConnection c)
    {
        if (!Directory.Exists(options.StoragePath))
        {
            return;
        }

        var kept = (await c.QueryAsync<Guid>("SELECT file_id FROM game_trailers WHERE file_id IS NOT NULL")).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(options.StoragePath))
        {
            var name = Path.GetFileName(file);
            var id = Guid.TryParse(name.Split('.')[0], out var parsed) ? parsed : (Guid?)null;
            if ((id is null || !kept.Contains(id.Value) || name.EndsWith(".part", StringComparison.Ordinal))
                && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Still open (being served): the next sweep.
                }
            }
        }
    }
}
