using Dapper;
using Npgsql;

namespace ClubShell.Server.Infrastructure;

/// <summary><c>Maintenance:*</c> retention (DESIGN §8).</summary>
public sealed class MaintenanceOptions
{
    /// <summary>Idempotency keys older than this are forgotten (§7.1).</summary>
    public int IdempotencyTtlHours { get; set; } = 24;

    /// <summary><c>pc_metrics</c>: a week of health baseline plus a day.</summary>
    public int MetricsRetentionDays { get; set; } = 8;

    public int TelemetryEventsRetentionDays { get; set; } = 30;

    /// <summary>Delivered or given-up webhook deliveries and automation firings.</summary>
    public int OutboxRetentionDays { get; set; } = 30;
}

/// <summary>
/// Housekeeping (DESIGN §8), every 10 minutes under <c>pg_advisory_lock(CSMant)</c>: expired idempotency keys, old
/// metrics and telemetry events, expired or revoked tokens (agent refresh, player, staff, QR), finished webhook
/// deliveries and old automation firings; once a day the ledger check — every wallet's cached balance against the sum of
/// its ledger rows (§4.3), an error in the log for each mismatch (the balance is never "fixed": corrections are
/// <c>adjustment</c> rows). Tests call <see cref="RunOnceAsync"/>.
/// </summary>
public sealed class MaintenanceWorker(NpgsqlDataSource db, MaintenanceOptions options, TimeProvider clock, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    public static readonly long LockKey = AdvisoryLocks.Key("CSMant");

    private DateTimeOffset _nextLedgerCheck = DateTimeOffset.MinValue;

    /// <summary>One pass; returns the wallets whose balance does not match the ledger (checked at most once a day).</summary>
    public async Task<IReadOnlyList<Guid>> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync(cancellationToken);
        var removed = await c.ExecuteAsync(
            """
            DELETE FROM idempotency_keys WHERE created_at < @keys;
            DELETE FROM pc_metrics WHERE at < @metrics;
            DELETE FROM telemetry_events WHERE received_at < @events;
            DELETE FROM agent_refresh_tokens WHERE expires_at < @tokens;
            DELETE FROM user_tokens WHERE expires_at < @tokens;
            DELETE FROM staff_tokens WHERE expires_at < @now OR revoked_at < @now;
            DELETE FROM qr_logins WHERE expires_at < @tokens;
            DELETE FROM webhook_outbox WHERE sent_at < @outbox;
            DELETE FROM rule_firings WHERE fired_at < @outbox;
            """,
            new
            {
                now,

                // A day past expiry: until then the agent still gets "expired" rather than "invalid" for a token it holds.
                tokens = now - TimeSpan.FromDays(1),
                keys = now - TimeSpan.FromHours(options.IdempotencyTtlHours),
                metrics = now - TimeSpan.FromDays(options.MetricsRetentionDays),
                events = now - TimeSpan.FromDays(options.TelemetryEventsRetentionDays),
                outbox = now - TimeSpan.FromDays(options.OutboxRetentionDays),
            });
        logger.LogDebug("Maintenance removed {Rows} rows", removed);

        if (now < _nextLedgerCheck)
        {
            return [];
        }

        _nextLedgerCheck = now + TimeSpan.FromDays(1);
        var broken = (await c.QueryAsync<(Guid UserId, long Balance, long Sum)>(
            """
            SELECT w.user_id, w.main_balance, coalesce(l.total, 0)::bigint
            FROM wallets w LEFT JOIN (SELECT user_id, sum(amount) AS total FROM ledger_entries GROUP BY user_id) l ON l.user_id = w.user_id
            WHERE w.main_balance <> coalesce(l.total, 0)
            """)).ToList();
        foreach (var (userId, balance, sum) in broken)
        {
            logger.LogError("Wallet {UserId}: cached balance {Balance} differs from the ledger sum {Sum}", userId, balance, sum);
        }

        return [.. broken.Select(b => b.UserId)];
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
                logger.LogWarning(ex, "Maintenance pass failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), clock, stoppingToken);
        }
    }
}
