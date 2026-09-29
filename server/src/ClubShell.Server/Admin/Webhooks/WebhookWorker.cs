using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// Webhook delivery (DESIGN §8), every second under <c>pg_advisory_lock(CSHook)</c>: due rows of <c>webhook_outbox</c>,
/// oldest first. A URL outside <see cref="WebhookTargets"/> is refused without a request (<c>lastStatus = 0</c>, not
/// retried); otherwise one POST (5 s timeout). A 2xx is done; anything else is tried again after
/// <see cref="RetryDelays"/> — three retries, then given up. Every attempt writes <c>lastStatus</c>/<c>lastAt</c> of the
/// webhook (0 = network error). A webhook disabled meanwhile drops its pending deliveries. Tests call
/// <see cref="RunOnceAsync"/> with the fixture's clock.
/// </summary>
public sealed class WebhookWorker(NpgsqlDataSource db, WebhookClient client, TimeProvider clock, ILogger<WebhookWorker> logger) : BackgroundService
{
    public static readonly long LockKey = AdvisoryLocks.Key("CSHook");

    /// <summary>Delay before retry 1, 2 and 3.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5)];

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        List<Due> due;
        await using (var c = await db.OpenConnectionAsync(cancellationToken))
        {
            due = (await c.QueryAsync<Due>(
                """
                SELECT o.id, o.club_id, o.webhook_id, o.event, o.payload::text AS payload, o.attempts, w.url, w.enabled
                FROM webhook_outbox o JOIN webhooks w ON w.club_id = o.club_id AND w.id = o.webhook_id
                WHERE o.sent_at IS NULL AND o.next_at <= @now
                ORDER BY o.next_at, o.id
                LIMIT 50
                """,
                new { now = clock.GetUtcNow() })).ToList();
        }

        foreach (var row in due)
        {
            await DeliverAsync(row, cancellationToken);
        }
    }

    private async Task DeliverAsync(Due row, CancellationToken cancellationToken)
    {
        if (!row.Enabled)
        {
            await using var off = await db.OpenConnectionAsync(cancellationToken);
            await off.ExecuteAsync("UPDATE webhook_outbox SET sent_at = @now WHERE id = @Id", new { row.Id, now = clock.GetUtcNow() });
            return;
        }

        var allowed = Uri.TryCreate(row.Url, UriKind.Absolute, out var url) && await WebhookTargets.IsAllowedAsync(url, cancellationToken);
        var status = allowed ? await client.PostAsync(url!, row.Event, row.Payload, cancellationToken) : 0;
        var now = clock.GetUtcNow();
        var attempts = row.Attempts + 1;
        var done = !allowed || status is >= 200 and < 300 || attempts > RetryDelays.Length;
        if (!allowed)
        {
            // The host only, never the path or query: they may carry the receiver's secret.
            logger.LogWarning("Webhook {WebhookId} of club {ClubId}: address of {Host} refused", row.WebhookId, row.ClubId, url?.Host ?? "?");
        }
        else if (status is < 200 or >= 300)
        {
            logger.LogInformation("Webhook {WebhookId} of club {ClubId}: attempt {Attempt} answered {Status}", row.WebhookId, row.ClubId, attempts, status);
        }

        await using var c = await db.OpenConnectionAsync(cancellationToken);
        await c.ExecuteAsync(
            """
            UPDATE webhook_outbox SET attempts = @attempts, sent_at = CASE WHEN @done THEN @now END, next_at = @next WHERE id = @Id;
            UPDATE webhooks SET last_status = @status, last_at = @now WHERE club_id = @ClubId AND id = @WebhookId;
            """,
            new { row.Id, row.ClubId, row.WebhookId, attempts, done, now, status, next = done ? now : now + RetryDelays[attempts - 1] });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var held = await db.OpenConnectionAsync(stoppingToken);
        await held.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@key)", new { key = LockKey }, cancellationToken: stoppingToken));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Webhook pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
        }
    }

    private sealed class Due
    {
        public long Id { get; init; }
        public Guid ClubId { get; init; }
        public string WebhookId { get; init; } = "";
        public string Event { get; init; } = "";
        public string Payload { get; init; } = "";
        public int Attempts { get; init; }
        public string Url { get; init; } = "";
        public bool Enabled { get; init; }
    }
}
