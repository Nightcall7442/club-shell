using System.Net;
using ClubShell.Server.Realtime;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Games;

/// <summary>
/// The trailer worker (D-97), every few seconds: the due <c>pending</c> rows of <c>game_trailers</c>, oldest first. A row
/// without a link is a search (<see cref="ITrailerFinder"/>): a found link stays <c>pending</c> for the download, none is
/// <c>failed notFound</c>. A row with a link is downloaded once into the store (<see cref="TrailerStore.WriteAsync"/>, at
/// most <see cref="TrailerOptions.MaxBytes"/> within <see cref="TrailerOptions.DownloadTimeoutSec"/>): a video page
/// (YouTube…) is refused without a request and anything but an mp4/webm by its first bytes — a web page as
/// <c>notAVideo</c>, an HLS/DASH playlist as <c>streamingFormat</c>; 404/410 and other 4xx are <c>unavailable</c>. No
/// answer, a timeout or a 5xx is tried again after 1, 10 and 60 minutes, then <c>unreachable</c>. A row the owner
/// changed meanwhile keeps the owner's change (the copy is swept). Each outcome a PC sees moves <c>catalog_version</c>
/// and reloads the club's connected PCs. At start, ready copies lost with the disk are fetched again. Tests call
/// <see cref="RunOnceAsync"/>.
/// </summary>
public sealed class TrailerWorker(
    NpgsqlDataSource db, TrailerStore store, ITrailerFinder finder, ITrailerWeb web, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock,
    ILogger<TrailerWorker> logger) : BackgroundService
{
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(60)];

    private DateTime _swept = DateTime.MinValue;

    /// <summary>One pass over the due rows; returns how many it handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        List<TrailerRow> due;
        await using (var c = await db.OpenConnectionAsync(cancellationToken))
        {
            due = (await c.QueryAsync<TrailerRow>(
                $"""
                SELECT {TrailerRow.Columns} FROM game_trailers t
                WHERE status = 'pending' AND next_try_at <= @now
                  AND EXISTS (SELECT 1 FROM games g WHERE g.id = t.game_id AND g.deleted_at IS NULL)
                ORDER BY next_try_at, game_id
                LIMIT 20
                """,
                new { now = clock.GetUtcNow() })).ToList();
            if (DateTime.UtcNow - _swept > TimeSpan.FromHours(1))
            {
                _swept = DateTime.UtcNow;
                await store.SweepAsync(c);
            }
        }

        foreach (var row in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.SourceUrl is null)
            {
                await SearchAsync(row, cancellationToken);
            }

            else
            {
                await DownloadAsync(row, cancellationToken);
            }
        }

        return due.Count;
    }

    /// <summary>Ready copies whose file is gone (a new disk): downloaded again from their link, an upload is <c>fileLost</c>.</summary>
    public async Task CheckFilesAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenConnectionAsync(cancellationToken);
        foreach (var row in await c.QueryAsync<TrailerRow>($"SELECT {TrailerRow.Columns} FROM game_trailers WHERE status = 'ready'"))
        {
            if (store.ReadyFile(row) is null)
            {
                logger.LogWarning("Trailer of game {GameId} is missing on disk", row.GameId);
                await c.ExecuteAsync(
                    """
                    UPDATE game_trailers SET status = CASE WHEN source_url IS NULL THEN 'failed' ELSE 'pending' END,
                                             reason = CASE WHEN source_url IS NULL THEN 'fileLost' END, file_id = NULL, attempts = 0,
                                             next_try_at = @now, updated_at = @now
                    WHERE game_id = @GameId AND file_id = @FileId
                    """,
                    new { row.GameId, row.FileId, now = clock.GetUtcNow() });
            }
        }
    }

    private async Task SearchAsync(TrailerRow row, CancellationToken cancellationToken)
    {
        var game = await GameAsync(row.GameId, cancellationToken);
        if (game is null)
        {
            return;
        }

        var found = await finder.FindAsync(game.Value.Title, game.Value.Launcher, game.Value.AppId, cancellationToken);
        if (found.Transient)
        {
            await RetryAsync(row, "unreachable", cancellationToken);
            return;
        }

        logger.LogInformation("Trailer of {Title}: {Result}", game.Value.Title, found.Url ?? "none found");
        await using var c = await db.OpenConnectionAsync(cancellationToken);
        await c.ExecuteAsync(
            found.Url is { } url
                ? "UPDATE game_trailers SET source_url = @url, attempts = 0, next_try_at = @now, updated_at = @now WHERE game_id = @GameId AND status = 'pending' AND origin = 'found' AND source_url IS NULL"
                : "UPDATE game_trailers SET status = 'failed', reason = 'notFound', updated_at = @now WHERE game_id = @GameId AND status = 'pending' AND origin = 'found' AND source_url IS NULL",
            new { row.GameId, url = found.Url, now = clock.GetUtcNow() });
    }

    private async Task DownloadAsync(TrailerRow row, CancellationToken cancellationToken)
    {
        var url = new Uri(row.SourceUrl!);
        if (TrailerFormat.IsVideoPage(url))
        {
            await FinishAsync(row, failure: "notAVideo", cancellationToken: cancellationToken);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(store.Options.DownloadTimeoutSec));
        try
        {
            using var response = await web.GetAsync(url, timeout.Token);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await RetryAsync(row, "unreachable", cancellationToken);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                await FinishAsync(row, failure: "unavailable", cancellationToken: cancellationToken);
                return;
            }

            if (response.Content.Headers.ContentLength > store.Options.MaxBytes)
            {
                await FinishAsync(row, failure: "tooLarge", cancellationToken: cancellationToken);
                return;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var stored = await store.WriteAsync(body, response.Content.Headers.ContentType?.MediaType, Guid.CreateVersion7(clock.GetUtcNow()), timeout.Token);
            logger.LogInformation("Trailer of game {GameId} stored: {Size} bytes from {Host}", row.GameId, stored.Size, url.Host);
            await FinishAsync(row, stored: stored, cancellationToken: cancellationToken);
        }
        catch (TrailerRefusedException refused)
        {
            logger.LogInformation("Trailer of game {GameId} from {Host} refused: {Reason}", row.GameId, url.Host, refused.Reason);
            await FinishAsync(row, failure: refused.Reason == "empty" ? "notAVideo" : refused.Reason, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogInformation("Trailer of game {GameId} from {Host} not downloaded: {Error}", row.GameId, url.Host, ex.Message);
            await RetryAsync(row, "unreachable", cancellationToken);
        }
    }

    /// <summary>No answer: again after the next delay, or given up.</summary>
    private async Task RetryAsync(TrailerRow row, string reason, CancellationToken cancellationToken)
    {
        if (row.Attempts >= RetryDelays.Length)
        {
            await FinishAsync(row, failure: reason, cancellationToken: cancellationToken);
            return;
        }

        await using var c = await db.OpenConnectionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        await c.ExecuteAsync(
            """
            UPDATE game_trailers SET attempts = attempts + 1, reason = @reason, next_try_at = @next, updated_at = @now
            WHERE game_id = @GameId AND status = 'pending' AND source_url IS NOT DISTINCT FROM @SourceUrl
            """,
            new { row.GameId, row.SourceUrl, reason, now, next = now + RetryDelays[row.Attempts] });
    }

    /// <summary>The download's outcome, unless the owner changed the row meanwhile; the PCs reload the catalog.</summary>
    private async Task FinishAsync(
        TrailerRow row, (Guid FileId, string ContentType, long Size, string Sha256)? stored = null, string? failure = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(Guid, ClubShell.Contracts.Commands.ServerCommandEnvelope)> queued = [];
        await using (var c = await db.OpenConnectionAsync(cancellationToken))
        await using (var tx = await c.BeginTransactionAsync(cancellationToken))
        {
            var now = clock.GetUtcNow();
            var changed = await c.ExecuteAsync(
                """
                UPDATE game_trailers SET status = @status, reason = @failure, file_id = @fileId, content_type = @type, size = @size, sha256 = @sha,
                                         updated_at = @now
                WHERE game_id = @GameId AND status = 'pending' AND source_url IS NOT DISTINCT FROM @SourceUrl
                """,
                new
                {
                    row.GameId, row.SourceUrl, now, failure, status = stored is null ? "failed" : "ready", fileId = stored?.FileId, type = stored?.ContentType,
                    size = stored?.Size, sha = stored?.Sha256,
                },
                tx);
            if (changed > 0)
            {
                queued = await TrailerStore.ChangedAsync(c, tx, commands, hub, row.ClubId, now);
            }

            await tx.CommitAsync(cancellationToken);
        }

        await Admin.ConfigRefresh.SendAsync(commands, queued);
    }

    private async Task<(string Title, string? Launcher, string? AppId)?> GameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        await using var c = await db.OpenConnectionAsync(cancellationToken);
        return await c.QuerySingleOrDefaultAsync<(string, string?, string?)?>(
            "SELECT title, data ->> 'launcher', data ->> 'launcherAppId' FROM games WHERE id = @gameId AND deleted_at IS NULL", new { gameId });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CheckFilesAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Checking the stored trailers failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                handled = await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Trailer pass failed");
            }

            if (handled == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), clock, stoppingToken);
            }
        }
    }
}
