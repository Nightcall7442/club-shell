using System.Globalization;
using ClubShell.Server.Wallet;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.PurchaseRulesTests;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The player's wallet history beyond the contract (<c>GET /wallet/{userId}/transactions</c>, DESIGN §1): only one's own
/// (<c>403 notOwner</c>), newest first, paged like the mock (default 50, at most 200), filtered by kind and period; a counter
/// top-up with its tier bonus and a kiosk session's charge show up in it.
/// </summary>
public sealed class WalletTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Counter_top_up_its_bonus_and_a_session_charge_are_in_the_own_history_newest_first()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 1_000_000);
        var cashier = await LoginAsync(Server, CashierPin);
        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 5_000_000, method = "cash" });
        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        var (status, session) = await Players.StartAsync(agent, player);
        Assert.Equal(201, status);

        var page = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/wallet/{player.Id}/transactions"), 200);
        Assert.Equal((4, 1, 50), (page.GetProperty("total").GetInt32(), page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32()));
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, t => Assert.Equal((player.Id, "UZS"), (t.GetProperty("userId").GetGuid(), t.GetProperty("amount").GetProperty("currency").GetString())));

        var charge = items[0];
        Assert.Equal("charge", charge.GetProperty("type").GetString());
        Assert.True(charge.GetProperty("amount").GetProperty("amount").GetInt64() < 0);
        Assert.Equal(session.GetProperty("id").GetGuid().ToString(), charge.GetProperty("ref").GetString());
        Assert.Equal(await Players.BalanceAsync(Server, player.Id), charge.GetProperty("balanceAfter").GetProperty("amount").GetInt64());

        // The top-up and its 5 % tier bonus are one operation at one instant: between themselves they follow the id.
        Assert.Equal(
            [("bonus", 250_000L), ("topUp", 5_000_000L)],
            items[1..3].Select(t => (t.GetProperty("type").GetString(), t.GetProperty("amount").GetProperty("amount").GetInt64())).Order());
        Assert.Equal(6_250_000L, items[1..3].Max(t => t.GetProperty("balanceAfter").GetProperty("amount").GetInt64()));
        Assert.Equal("Пополнение на кассе наличными", items[1..3].Single(t => t.GetProperty("type").GetString() == "topUp").GetProperty("description").GetString());
        Assert.Equal(("adjustment", 1_000_000L), (items[3].GetProperty("type").GetString(), items[3].GetProperty("balanceAfter").GetProperty("amount").GetInt64()));
    }

    [Fact]
    public async Task Another_players_history_is_403_not_owner()
    {
        var (agent, _) = await Players.SignedInAsync(Server);
        var other = await Players.CreateAsync(Server);
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/wallet/{other.Id}/transactions");
        await Contract.ReadErrorAsync(response, 403, "forbidden", "notOwner");
    }

    [Fact]
    public async Task Pages_kinds_and_period_filter_the_history()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 0);
        var start = Server.Clock.GetUtcNow();
        (string Type, long Amount)[] lines = [("adjustment", 1_000_000), ("topUp", 500_000), ("charge", -200_000), ("purchase", -50_000), ("bonus", 100_000), ("refund", 30_000)];
        for (var i = 0; i < lines.Length; i++)
        {
            await PostAsync(player, lines[i].Type, lines[i].Amount, start.AddMinutes(i));
        }

        var path = $"/api/v1/wallet/{player.Id}/transactions";
        async Task<(int Total, int Page, int PageSize, string Types)> PageAsync(string query)
        {
            var body = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, path + query), 200);
            return (body.GetProperty("total").GetInt32(), body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(),
                string.Join(' ', body.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("type").GetString())));
        }

        Assert.Equal((6, 1, 50, "refund bonus purchase charge topUp adjustment"), await PageAsync(""));
        Assert.Equal((6, 2, 2, "purchase charge"), await PageAsync("?page=2&pageSize=2"));
        Assert.Equal((6, 9, 2, ""), await PageAsync("?page=9&pageSize=2"));
        Assert.Equal((6, 1, 200, "refund bonus purchase charge topUp adjustment"), await PageAsync("?pageSize=500"));
        Assert.Equal((1, 1, 50, "charge"), await PageAsync("?type=charge"));

        // from inclusive, to exclusive.
        var from = Uri.EscapeDataString(start.AddMinutes(1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(start.AddMinutes(3).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        Assert.Equal((2, 1, 50, "charge topUp"), await PageAsync($"?from={from}&to={to}"));
        Assert.Equal((1, 1, 50, "topUp"), await PageAsync($"?from={from}&to={to}&type=topUp"));

        foreach (var (query, field, reason) in new[]
        {
            ("?type=TOPUP", "type", "enum"),
            ("?type=unknown&from=nope", "type", "enum"),
            ("?from=nope", "from", "format"),
            ("?from=" + from + "&to=yesterday", "to", "format"),
        })
        {
            using var response = await agent.SendAsync(HttpMethod.Get, path + query);
            var error = await Contract.ReadErrorAsync(response, 400, "validation");
            Assert.Equal((field, reason), (Details(error).GetProperty("field").GetString(), Details(error).GetProperty("reason").GetString()));
        }
    }

    /// <summary>One ledger row of <paramref name="player"/> at <paramref name="at"/>, through the only writer of money.</summary>
    private async Task PostAsync(TestPlayer player, string type, long amount, DateTimeOffset at)
    {
        await using var c = await Server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await Ledger.PostAsync(c, tx, player.Id, allowOverdraft: false, at, new LedgerLine(type, amount, type));
        await tx.CommitAsync();
    }
}
