using System.Globalization;
using System.Text.Json;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Wallet;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The desk's operations feed (<c>GET /admin/shift/operations</c>, cash desk part 2, D-43): the shift's journal, newest first,
/// one row per desk operation with the payment merged in (a paid seat is one row; its top-up stays in the journal for
/// Control), the drawer effect of each row summing to the expected drawer; a cashier reads the open and the last closed
/// shift, the owner any; a keyset cursor; «сегодня» is the club's local day by method. The clock moves a second between
/// operations, so the order is the order they were made in.
/// </summary>
public sealed class OperationsFeedTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task The_feed_lists_each_desk_operation_once_with_the_payment_merged()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier, openingCash: 100_000);
        var seatPc = await TestAgent.CreateAsync(Server);
        var guestPc = await TestAgent.CreateAsync(Server);
        var seated = await Players.CreateAsync(Server, balance: 0);
        var carded = await Players.CreateAsync(Server, balance: 0);
        var debtor = await Players.CreateAsync(Server, balance: -300_000);

        Tick();
        var opened = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier,
            new { pcId = seatPc.PcId, userId = seated.Id, tariffId = Players.Standard, minutes = 60, payment = new { amount = 1_200_000, method = "cash" } });
        Tick();
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = carded.Id, amount = 500_000, method = "card" });
        Tick();
        await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = debtor.Id, amount = 300_000, method = "cash", settleDebt = true });
        Tick();
        await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "in", amount = 50_000, reasonCode = "change" }, Guid.NewGuid());
        Tick();
        var guestId = (await DeskGuests.SeatAsync(Server, cashier, guestPc.PcId)).GetProperty("user").GetProperty("id").GetGuid();
        Tick();

        // One second played: the refund is pro rata, floored to 100 tiyin (1 199 600), and all of it is payable in cash.
        var payable = Amount((await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = guestPc.PcId })).GetProperty("payable"));
        Assert.Equal(1_199_600, payable);
        Tick();
        await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/payout", cashier, new { userId = guestId, amount = payable }, Guid.NewGuid());

        var page = await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations", cashier);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["payout", "sessionEnd", "sessionOpen", "cashIn", "debtPaid", "topUp", "sessionOpen", "shiftOpen"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next").ValueKind);

        var seat = items[6];
        Assert.Equal((seated.Id, seatPc.PcId, "Standard", 60, true, 1_200_000L), (seat.GetProperty("client").GetProperty("id").GetGuid(), seat.GetProperty("pc").GetProperty("id").GetGuid(),
            seat.GetProperty("tariff").GetString(), seat.GetProperty("minutes").GetInt32(), seat.GetProperty("prepaid").GetBoolean(), seat.GetProperty("charged").GetInt64()));
        Assert.Equal((1_200_000L, "cash", opened.GetProperty("payment").GetProperty("transaction").GetProperty("id").GetGuid(), 1_200_000L),
            (seat.GetProperty("paid").GetProperty("amount").GetInt64(), seat.GetProperty("paid").GetProperty("method").GetString(),
             seat.GetProperty("paid").GetProperty("transactionId").GetGuid(), seat.GetProperty("drawer").GetInt64()));
        Assert.Equal("""{"base":1200000,"dayPct":100,"discountPct":0}""", seat.GetProperty("quote").GetRawText());
        Assert.Equal(opened.GetProperty("session").GetProperty("id").GetGuid(), seat.GetProperty("sessionId").GetGuid());
        Assert.Equal(("card", 0L, carded.Id), (items[5].GetProperty("paid").GetProperty("method").GetString(), items[5].GetProperty("drawer").GetInt64(),
            items[5].GetProperty("client").GetProperty("id").GetGuid()));
        Assert.Equal((300_000L, debtor.Id), (items[4].GetProperty("drawer").GetInt64(), items[4].GetProperty("client").GetProperty("id").GetGuid()));
        Assert.Equal((50_000L, "change", JsonValueKind.Null), (items[3].GetProperty("drawer").GetInt64(), items[3].GetProperty("reasonCode").GetString(), items[3].GetProperty("note").ValueKind));
        Assert.Equal((payable, 0L, 0L), (items[1].GetProperty("amount").GetInt64(), items[1].GetProperty("charged").GetInt64(), items[1].GetProperty("drawer").GetInt64()));
        Assert.Equal((-payable, "guest"), (items[0].GetProperty("drawer").GetInt64(), items[0].GetProperty("client").GetProperty("role").GetString()));
        Assert.Equal(100_000, items[7].GetProperty("drawer").GetInt64());

        // The rows' drawer effects make up the expected drawer: 100 000 + 1 200 000 + 300 000 + 50 000 + 1 200 000 − payout.
        Assert.Equal(2_850_000 - payable, items.Sum(i => i.GetProperty("drawer").GetInt64()));
        Assert.Equal(2_850_000 - payable, await ExpectedCashAsync(Server, cashier));
        var today = page.GetProperty("today");
        Assert.True(today.GetProperty("byMethod").GetProperty("cash").GetInt64() >= 2_700_000 && today.GetProperty("payouts").GetInt64() >= payable, today.GetRawText());
        Assert.Equal(today.GetProperty("taken").GetInt64(), today.GetProperty("byMethod").EnumerateObject().Sum(p => p.Value.GetInt64()));

        // Control still reads the paid seat's top-up from the journal.
        var log = (await ExpectAsync(Server, 200, HttpMethod.Get, "/control?days=1", owner)).GetProperty("log").EnumerateArray().ToList();
        Assert.Contains(log, e => e.GetProperty("action").GetString() == "topUp" && e.GetProperty("userId").GetString() == seated.Id.ToString());
        Assert.Contains(log, e => e.GetProperty("action").GetString() == "sessionOpen" && e.GetProperty("userId").GetString() == seated.Id.ToString());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = seatPc.PcId });
    }

    [Fact]
    public async Task A_cashier_reads_the_open_and_the_last_closed_shift_only()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        var older = await FreshShiftAsync(Server, cashier);
        Tick();
        var last = await FreshShiftAsync(Server, cashier);
        Tick();
        var open = await FreshShiftAsync(Server, cashier);

        Contract.AssertError(await RawExpectAsync(Server, 403, HttpMethod.Get, $"/shift/operations?shiftId={older}", cashier), "forbidden", "ownerOnly");
        Assert.Equal(older, (await RawExpectAsync(Server, 200, HttpMethod.Get, $"/shift/operations?shiftId={older}", owner)).GetProperty("shift").GetProperty("id").GetGuid());
        var lastPage = await RawExpectAsync(Server, 200, HttpMethod.Get, $"/shift/operations?shiftId={last}", cashier);
        Assert.Equal("Кассир Азиз", lastPage.GetProperty("shift").GetProperty("closedBy").GetString());
        Assert.Equal(["shiftClose", "shiftOpen"], lastPage.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(open, (await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations", cashier)).GetProperty("shift").GetProperty("id").GetGuid());
        Assert.Equal("shift", (await RawExpectAsync(Server, 404, HttpMethod.Get, $"/shift/operations?shiftId={Guid.NewGuid()}", owner))
            .GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        foreach (var (query, field, reason) in new[]
        {
            ("limit=0", "limit", "min"), ("limit=201", "limit", "max"), ("limit=many", "limit", "format"), ("before=%21%21", "before", "format"),
            ("before=" + Base64("2026-01-01T00:00:00.000Z|nope"), "before", "format"), ("kinds=topUp,bogus", "kinds", "enum"), ("shiftId=nope", "shiftId", "format"),
        })
        {
            var details = (await RawExpectAsync(Server, 400, HttpMethod.Get, "/shift/operations?" + query, cashier)).GetProperty("error").GetProperty("details");
            Assert.Equal((field, reason), (details.GetProperty("field").GetString(), details.GetProperty("reason").GetString()));
        }

        // Five cash-ins and the opening: pages of two follow the cursor without a gap or a duplicate.
        for (var i = 0; i < 5; i++)
        {
            Tick();
            await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "in", amount = 1_000 + i, reasonCode = "change" }, Guid.NewGuid());
        }

        var all = (await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?limit=200", cashier)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(6, all.Count);
        var paged = new List<Guid>();
        string? next = null;
        var pages = 0;
        do
        {
            var page = await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?limit=2" + (next is null ? "" : "&before=" + next), cashier);
            paged.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            next = page.GetProperty("next").GetString();
            pages++;
        }
        while (next is not null && pages < 10);

        Assert.Equal(all, paged);
        Assert.Equal(3, pages);
        Assert.Equal([1_004L, 1_003, 1_002, 1_001, 1_000], (await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?kinds=cashIn", cashier))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("amount").GetInt64()));
    }

    [Fact]
    public async Task Today_is_the_club_local_day_by_method()
    {
        // Local noon of tomorrow; then no shift is open: «сегодня» still comes.
        const string zone = "Asia/Tashkent";
        var start = Server.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(ClubTime.Local(start, zone)).AddDays(1);
        Server.Clock.Advance(ClubTime.Utc(today.ToDateTime(new TimeOnly(12, 0)), zone) - start);
        DateTimeOffset At(int dayOffset, int hour, int minute) => ClubTime.Utc(today.AddDays(dayOffset).ToDateTime(new TimeOnly(hour, minute)), zone);

        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = await ExpectedCashAsync(Server, cashier) });
        var before = await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations", cashier);
        Assert.Equal((JsonValueKind.Null, 0), (before.GetProperty("shift").ValueKind, before.GetProperty("items").GetArrayLength()));
        Assert.Equal((today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), At(0, 0, 0)),
            (before.GetProperty("today").GetProperty("date").GetString(), before.GetProperty("today").GetProperty("from").GetDateTimeOffset()));

        var player = await Players.CreateAsync(Server, balance: 0);
        var clubId = await Players.ScalarAsync<Guid>(Server, "SELECT id FROM clubs");
        await PostAsync(player.Id, At(-1, 23, 30), new LedgerLine("topUp", 5_000_000, "Вчера", clubId, Method: "cash")); // UTC: today 18:30 − 1 day
        await PostAsync(player.Id, At(0, 0, 30), new LedgerLine("topUp", 700_000, "Сегодня", clubId, Method: "card"));
        await PostAsync(player.Id, At(0, 1, 0), new LedgerLine("adjustment", -200_000, "Выдано наличными на кассе", clubId, Method: "cash"));
        await PostAsync(player.Id, At(0, 2, 0), new LedgerLine("charge", -300_000, "Сеанс", clubId), new LedgerLine("refund", 100_000, "Возврат", clubId));

        var after = (await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations", cashier)).GetProperty("today");
        long Delta(string name) => after.GetProperty(name).GetInt64() - before.GetProperty("today").GetProperty(name).GetInt64();
        long Method(string name) => after.GetProperty("byMethod").GetProperty(name).GetInt64() - before.GetProperty("today").GetProperty("byMethod").GetProperty(name).GetInt64();
        Assert.Equal((0L, 700_000L, 700_000L, 200_000L, 200_000L), (Method("cash"), Method("card"), Delta("taken"), Delta("payouts"), Delta("sessions")));
    }

    private void Tick() => Server.Clock.Advance(TimeSpan.FromSeconds(1));

    private static string Base64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task PostAsync(Guid userId, DateTimeOffset at, params LedgerLine[] lines)
    {
        await using var c = await Server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await Ledger.PostAsync(c, tx, userId, allowOverdraft: true, at, lines);
        await tx.CommitAsync();
    }
}
