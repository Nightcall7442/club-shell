using ClubShell.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ClubShell.Server.Tests;

/// <summary>
/// Housekeeping (DESIGN §8): expired idempotency keys, old metrics and events, tokens a day past expiry and finished
/// webhook deliveries go; fresh ones stay; the daily ledger check reports a wallet whose cached balance left the ledger sum.
/// </summary>
public sealed class MaintenanceTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Old_rows_go_fresh_ones_stay_and_a_broken_balance_is_reported()
    {
        var agent = await TestAgent.CreateAsync(server);
        var player = await Players.CreateAsync(server, balance: 1_000);
        var now = server.Clock.GetUtcNow();
        foreach (var (key, at) in new[] { (Guid.NewGuid(), now.AddHours(-25)), (Guid.NewGuid(), now.AddHours(-1)) })
        {
            await Players.ExecuteAsync(server,
                "INSERT INTO idempotency_keys (principal, method, path, key, request_hash, created_at) VALUES ('club:x', 'POST', '/p', @key, @hash, @at)",
                new { key, at, hash = new byte[] { 0 } });
        }

        foreach (var at in new[] { now.AddDays(-9), now.AddDays(-1) })
        {
            await Players.ExecuteAsync(server, "INSERT INTO pc_metrics (pc_id, at, data) VALUES (@PcId, @at, '{}')", new { agent.PcId, at });
            await Players.ExecuteAsync(server,
                "INSERT INTO telemetry_events (club_id, pc_id, kind, at, received_at) SELECT club_id, id, 'callAdmin', @at, @at FROM pcs WHERE id = @PcId",
                new { agent.PcId, at = at.AddDays(-22) });
        }

        await Players.ExecuteAsync(server, "UPDATE wallets SET main_balance = main_balance + 1 WHERE user_id = @Id", new { player.Id });
        try
        {
            var broken = await server.Services.GetRequiredService<MaintenanceWorker>().RunOnceAsync();
            Assert.Equal([player.Id], broken);
        }
        finally
        {
            await Players.ExecuteAsync(server, "UPDATE wallets SET main_balance = main_balance - 1 WHERE user_id = @Id", new { player.Id });
        }

        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM idempotency_keys WHERE principal = 'club:x'"));
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM pc_metrics WHERE pc_id = @PcId", new { agent.PcId }));
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM telemetry_events WHERE pc_id = @PcId AND kind = 'callAdmin'", new { agent.PcId }));

        // The ledger check runs once a day: the next pass does not repeat it.
        Assert.Empty(await server.Services.GetRequiredService<MaintenanceWorker>().RunOnceAsync());
        await LedgerInvariant.AssertAsync(server);
    }
}
