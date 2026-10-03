using System.Text.Json;
using ClubShell.Server.Admin;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Wallet;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Cashier control (slice S5, port of the mock's <c>control.ts</c>): the seven flags from one cashier's day — money with no
/// shift (from the journal and from a ledger row that got no shift, §4.3), three top-ups of one client, big cash, three
/// early refunds (each and together), a big-discount group, a cash shortfall — ordered by severity; per-cashier totals
/// never narrowed by <c>staffId</c>; <c>days</c> normalized; the <c>suspicious</c> events at the moment the patterns complete.
/// </summary>
public sealed class ControlTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    private static readonly Guid CashierId = DevSeed.Sid("staff:cashier-1");

    [Fact]
    public async Task A_cashiers_day_raises_all_seven_flags()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            notifications = new { bigTopupAt = 5_000_000 },
            webhooks = new[] { new { id = "sus", url = "https://hooks.example.com/s", events = new[] { "suspicious" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null } },
        });
        var client = await Players.CreateAsync(Server);
        var other = await Players.CreateAsync(Server, balance: 0);
        var pcs = new List<TestAgent>();
        for (var i = 0; i < 3; i++)
        {
            pcs.Add(await TestAgent.CreateAsync(Server));
        }

        await NoShiftTopUpAsync(client, 1_000_000); // noShift
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/open", cashier, new { openingCash = 0 });
        for (var i = 0; i < 3; i++)
        {
            await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = client.Id, amount = 6_000_000, method = "cash" }); // bigCash ×3, sameClient
        }

        foreach (var pc in pcs)
        {
            await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = pc.PcId, userId = client.Id, tariffId = Players.Standard, minutes = 60 });
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = pc.PcId }); // earlyEnd ×3, earlyEnds
        }

        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM webhook_outbox WHERE event = 'suspicious'"));
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/clients/{client.Id}", cashier, new { groupId = "staff" }); // discount (50 % ≥ 30 %)
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 17_000_000 }); // shortfall: 18 000 000 expected
        Assert.Equal<string[]>(
            ["Кассир Азиз: 3 сеанса закрыты с возвратом в первые 10 мин", "Кассир Азиз: недостача в кассе 10 000 сум при закрытии смены"],
            (await Players.ScalarAsync<string>(Server, "SELECT string_agg(payload ->> 'text', '|' ORDER BY id) FROM webhook_outbox WHERE event = 'suspicious'")).Split('|'));

        // A cashier's ledger row with no shift and no journal entry that shows it (what a row racing the close gets).
        await using (var c = await Server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var clubId = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(c, "SELECT id FROM clubs", transaction: tx);
            await Ledger.PostAsync(c, tx, other.Id, allowOverdraft: false, Server.Clock.GetUtcNow().AddSeconds(-30),
                new LedgerLine("topUp", 300_000, "Пополнение на кассе (cash)", clubId, Method: "cash", StaffId: CashierId));
            await tx.CommitAsync();
        }

        var report = await ExpectAsync(Server, 200, HttpMethod.Get, "/control", owner);
        var flags = report.GetProperty("flags").EnumerateArray().ToList();
        Assert.Equal(["bigCash", "discount", "earlyEnd", "earlyEnds", "noShift", "sameClient", "shortfall"], flags.Select(f => f.GetProperty("kind").GetString()).Distinct().Order());
        Assert.Equal(["high", "high", "medium", "medium", "medium", "medium", "low", "low", "low", "low", "low", "low"], flags.Select(f => f.GetProperty("severity").GetString()));
        var noShift = flags.Where(f => f.GetProperty("kind").GetString() == "noShift").ToList();
        Assert.Equal(2, noShift.Count);
        Assert.Contains(noShift, f => f.GetProperty("userId").GetGuid() == other.Id && f.GetProperty("amount").GetInt64() == 300_000 && f.GetProperty("staffName").GetString() == "Кассир Азиз");
        var shortfall = flags.Single(f => f.GetProperty("kind").GetString() == "shortfall");
        Assert.Equal((1_000_000, 1_000_000), (shortfall.GetProperty("amount").GetInt64(), shortfall.GetProperty("params").GetProperty("short").GetInt64()));
        var earlyEnds = flags.Single(f => f.GetProperty("kind").GetString() == "earlyEnds");
        Assert.Equal(3, earlyEnds.GetProperty("params").GetProperty("count").GetInt32());
        var discount = flags.Single(f => f.GetProperty("kind").GetString() == "discount").GetProperty("params");
        Assert.Equal((50, "Сотрудник"), (discount.GetProperty("pct").GetInt32(), discount.GetProperty("group").GetString()));
        Assert.Equal((3, 18_000_000), (flags.Single(f => f.GetProperty("kind").GetString() == "sameClient").GetProperty("params").GetProperty("count").GetInt32(),
            flags.Single(f => f.GetProperty("kind").GetString() == "sameClient").GetProperty("amount").GetInt64()));

        var summary = report.GetProperty("staff").EnumerateArray().Single(s => s.GetProperty("staffId").GetString() == CashierId.ToString());
        Assert.Equal((3, 1, 1_000_000, 19_000_000), (summary.GetProperty("earlyEnds").GetInt32(), summary.GetProperty("discounts").GetInt32(),
            summary.GetProperty("shortfall").GetInt64(), summary.GetProperty("topUps").GetInt64()));
        Assert.Equal("{\"high\":2,\"medium\":4,\"low\":6}", summary.GetProperty("flags").GetRawText());

        var log = report.GetProperty("log").EnumerateArray().ToList();
        Assert.All(log, e => Assert.Contains(e.GetProperty("action").GetString(), ControlEndpoints.Actions));
        Assert.Equal(log.Select(e => e.GetProperty("at").GetDateTimeOffset()).OrderDescending(), log.Select(e => e.GetProperty("at").GetDateTimeOffset()));
        Assert.DoesNotContain(log, e => e.GetProperty("action").GetString() == "settingsSave");

        // staffId narrows flags and journal, never the totals.
        var mine = await ExpectAsync(Server, 200, HttpMethod.Get, $"/control?staffId={DevSeed.Sid("staff:owner")}", owner);
        Assert.Empty(mine.GetProperty("flags").EnumerateArray());
        Assert.All(mine.GetProperty("log").EnumerateArray(), e => Assert.Equal(DevSeed.Sid("staff:owner").ToString(), e.GetProperty("staffId").GetString()));
        Assert.Equal(report.GetProperty("staff").GetRawText(), mine.GetProperty("staff").GetRawText());
    }

    [Fact]
    public async Task Days_default_to_7_and_are_clamped_and_the_page_is_the_owners()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        foreach (var (days, expected) in new[] { ("", 7), ("abc", 7), ("0", 1), ("-3", 1), ("1000", 90), ("99999999999", 90), ("30", 30) })
        {
            var report = await ExpectAsync(Server, 200, HttpMethod.Get, $"/control?days={days}", owner);
            var span = report.GetProperty("to").GetDateTimeOffset() - report.GetProperty("from").GetDateTimeOffset();
            Assert.Equal(TimeSpan.FromDays(expected), span);
        }

        using var refused = await Server.Http.SendAsync(Request(HttpMethod.Get, "/control", await LoginAsync(Server, CashierPin)));
        await Contract.ReadErrorAsync(refused, 403, "forbidden", "ownerOnly");
    }

    /// <summary>
    /// The cashier's cash top-up with no shift open, its ledger row and journal entry as the counter wrote them before it
    /// refused money without a shift (<c>409 shiftClosed</c>): what the database of an older server still holds.
    /// </summary>
    private async Task NoShiftTopUpAsync(TestPlayer client, long amount)
    {
        await using var c = await Server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var (clubId, networkId) = await Dapper.SqlMapper.QuerySingleAsync<(Guid, Guid)>(c, "SELECT id, network_id FROM clubs", transaction: tx);
        var now = Server.Clock.GetUtcNow();
        await Ledger.PostAsync(c, tx, client.Id, allowOverdraft: false, now,
            new LedgerLine("topUp", amount, "Пополнение на кассе наличными", clubId, Method: "cash", StaffId: CashierId));
        await Audit.WriteAsync(c, tx, new StaffContext(CashierId, "Кассир Азиз", "cashier", clubId, networkId), now, "topUp", client.Id, amount: amount,
            detail: client.Username, meta: new { method = "cash", bonus = 0 });
        await tx.CommitAsync();
    }
}
