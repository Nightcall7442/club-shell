using ClubShell.Contracts.Commands;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The club minute (DESIGN §8), every 60 s under <c>pg_advisory_lock(CSClub)</c>, per club: when the set of banners live on
/// the club's local date differs from the one last sent (<c>clubs.banners_hash</c>, OQ-21) — a banner's <c>from</c>/<c>to</c>
/// passed at midnight — <c>config_version + 1</c> and <c>refreshConfig {config:true}</c> to the connected PCs; then the
/// <c>minutesLeft</c> automation (<see cref="AutomationService.MinutesLeftAsync"/>). Tests call <see cref="RunOnceAsync"/>.
/// </summary>
public sealed class ClubTickWorker(
    NpgsqlDataSource db, AutomationService automation, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock, ILogger<ClubTickWorker> logger)
    : BackgroundService
{
    public static readonly long LockKey = AdvisoryLocks.Key("CSClub");

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> clubs;
        await using (var c = await db.OpenConnectionAsync(cancellationToken))
        {
            clubs = [.. await c.QueryAsync<Guid>("SELECT id FROM clubs WHERE NOT disabled ORDER BY created_at")];
        }

        foreach (var clubId in clubs)
        {
            await BannersAsync(clubId, cancellationToken);
            await automation.MinutesLeftAsync(clubId);
        }
    }

    private async Task BannersAsync(Guid clubId, CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued = [];
        await using (var c = await db.OpenConnectionAsync(cancellationToken))
        await using (var tx = await c.BeginTransactionAsync(cancellationToken))
        {
            var now = clock.GetUtcNow();
            var (settings, timeZone, stored) = await c.QuerySingleAsync<(string, string, string?)>(
                "SELECT settings::text, time_zone, banners_hash FROM clubs WHERE id = @clubId FOR UPDATE", new { clubId }, tx);
            var hash = ClubSettingsEndpoints.BannersHash(settings, DateOnly.FromDateTime(ClubTime.Local(now, timeZone)));
            if (hash == stored)
            {
                return;
            }

            // The first hash of a club (none stored yet) only records: nothing was announced that could be stale.
            var bump = stored is null ? 0 : 1;
            await c.ExecuteAsync(
                "UPDATE clubs SET banners_hash = @hash, config_version = config_version + @bump, updated_at = @now WHERE id = @clubId",
                new { clubId, hash, bump, now }, tx);
            if (bump == 1)
            {
                queued = await ConfigRefresh.QueueAsync(c, tx, commands, hub, clubId, new RefreshConfigCommand(Config: true));
            }

            await tx.CommitAsync(cancellationToken);
        }

        await ConfigRefresh.SendAsync(commands, queued);
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
                logger.LogWarning(ex, "Club tick failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), clock, stoppingToken);
        }
    }
}
