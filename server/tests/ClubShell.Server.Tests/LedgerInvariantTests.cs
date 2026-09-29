using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>
/// The ledger (DESIGN §4.3): append-only by trigger, and <c>SUM(ledger) = wallets</c> after money moved every way S2
/// moves it (seeded balances, prepaid charge, extension, admin refund, postpaid overdraft). Every S2 test class also
/// asserts the invariant after each of its tests (<see cref="LedgerCheckedTest"/>).
/// </summary>
public sealed class LedgerInvariantTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Ledger_rows_cannot_be_changed_or_deleted()
    {
        await Players.CreateAsync(Server, balance: 1_000);
        foreach (var sql in new[] { "UPDATE ledger_entries SET amount = amount + 1", "DELETE FROM ledger_entries", "TRUNCATE ledger_entries CASCADE" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Players.ExecuteAsync(Server, sql));
            Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Balances_match_the_ledger_after_every_kind_of_money_move()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 2_000_000);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/extend", new { minutes = 30 }, Guid.NewGuid()), 200);
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/end", new { reason = "admin", secondsUsed = 0 }), 200);
        var (_, postpaid) = await Players.StartAsync(agent, player, prepaid: false);
        Server.Clock.Advance(TimeSpan.FromMinutes(59));
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{postpaid.GetProperty("id").GetGuid()}/end", new { reason = "user", secondsUsed = 0 }), 200);

        Assert.Equal(2_000_000 - 1_180_000, await Players.BalanceAsync(Server, player.Id));
        await LedgerInvariant.AssertAsync(Server);
    }
}
