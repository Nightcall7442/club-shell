using System.Text.Json;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Debt write-off (SHELL_CHANGES 90, <c>POST /admin/wallet/write-off</c>): the owner — or a cashier with the owner's mark —
/// closes a debt without money, with a reason; the drawer, the cash and the revenue stay the same, X and the reports count
/// it apart, the journal has <c>debtWriteOff</c>. Exactly the debt, never during a session, once per key.
/// </summary>
public sealed class DebtWriteOffTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private SessionTickWorker Tick => Server.Services.GetRequiredService<SessionTickWorker>();

    [Fact]
    public async Task The_owner_writes_a_guest_debt_off_without_money_and_x_counts_it_apart()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await FreshShiftAsync(Server, owner);
        var (guestId, sessionId) = await GuestDebtAsync(owner);
        var before = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", owner)).GetProperty("x");

        // A cashier without the owner's mark may not.
        var cashier = await LoginAsync(Server, CashierPin);
        var refused = await RawExpectAsync(Server, 403, HttpMethod.Post, "/wallet/write-off", cashier, Body(guestId, 200_000), Guid.NewGuid());
        Assert.Equal("writeOffNotAllowed", refused.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());

        Assert.Equal(400, (await RawAsync(Server, HttpMethod.Post, "/wallet/write-off", owner, new { userId = guestId, amount = 200_000, reason = " " }, Guid.NewGuid())).Status);
        var changed = await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/write-off", owner, Body(guestId, 150_000), Guid.NewGuid());
        Assert.Equal(200_000, changed.GetProperty("error").GetProperty("details").GetProperty("debt").GetProperty("amount").GetInt64());

        var key = Guid.NewGuid();
        var done = await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/write-off", owner, Body(guestId, 200_000), key);
        Assert.Equal(0, done.GetProperty("balance").GetProperty("amount").GetInt64());
        Assert.Equal(0, await Players.BalanceAsync(Server, guestId));
        Assert.DoesNotContain(await DebtsAsync(owner), d => d.GetProperty("userId").GetGuid() == guestId);

        // The same key again is the same answer; a new one finds no debt.
        var (_, again, replayed) = await RawAsync(Server, HttpMethod.Post, "/wallet/write-off", owner, Body(guestId, 200_000), key);
        Assert.True(replayed);
        Assert.Equal(done.GetProperty("transactionId").GetGuid(), again.GetProperty("transactionId").GetGuid());
        await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/write-off", owner, Body(guestId, 200_000), Guid.NewGuid());

        // No money moved: the drawer and the top-ups are as before, the write-off is its own line.
        var x = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", owner)).GetProperty("x");
        Assert.Equal(before.GetProperty("topUpCash").GetInt64(), x.GetProperty("topUpCash").GetInt64());
        Assert.Equal(before.GetProperty("payouts").GetInt64(), x.GetProperty("payouts").GetInt64());
        // The owner's write-off is the owner's own operation (D-101): in no shift, so the cashier's X does not list it.
        Assert.Equal((0L, 0), (x.GetProperty("writeOffs").GetInt64(), x.GetProperty("writeOffCount").GetInt32()));
        Assert.True(await Players.ScalarAsync<bool>(Server,
            "SELECT owner_op AND shift_id IS NULL FROM ledger_entries WHERE id = @id", new { id = done.GetProperty("transactionId").GetGuid() }));

        var journal = await Players.ScalarAsync<string>(Server,
            "SELECT detail || '|' || amount || '|' || (meta ->> 'sessionId') FROM audit_entries WHERE action = 'debtWriteOff' AND user_id = @guestId", new { guestId });
        Assert.EndsWith($": тестовый сеанс|200000|{sessionId}", journal);

        var reports = await RawExpectAsync(Server, 200, HttpMethod.Get, "/reports?days=1", owner);
        Assert.True(reports.GetProperty("totals").GetProperty("writeOffs").GetInt64() >= 200_000);
    }

    [Fact]
    public async Task A_cashier_with_the_mark_may_and_a_debt_in_a_session_waits()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await FreshShiftAsync(Server, owner);
        var cashierId = (await ExpectAsync(Server, 200, HttpMethod.Get, "/staff", owner)).GetProperty("items").EnumerateArray()
            .Single(s => s.GetProperty("role").GetString() == "cashier" && s.GetProperty("name").GetString() == "Кассир Азиз").GetProperty("id").GetString();
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/staff/{cashierId}", owner, new { debtWriteOff = true });
        try
        {
            var cashier = await LoginAsync(Server, CashierPin);
            Assert.True((await ExpectAsync(Server, 200, HttpMethod.Get, "/me", cashier)).GetProperty("staff").GetProperty("debtWriteOff").GetBoolean());

            // Still playing postpaid: the debt is not final yet.
            var agent = await Seats.OnlineAsync(Server);
            var opened = await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions/guest", owner, new { pcId = agent.PcId, tariffId = Players.Standard, minutes = 60, prepaid = false }, Guid.NewGuid());
            var guestId = opened.GetProperty("user").GetProperty("id").GetGuid();
            Server.Clock.Advance(TimeSpan.FromMinutes(5));
            await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
            await Tick.RunOnceAsync();
            var open = await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/write-off", cashier, Body(guestId, 100_000), Guid.NewGuid());
            Assert.Equal("sessionOpen", open.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());

            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = opened.GetProperty("session").GetProperty("id").GetGuid() });
            var debt = -await Players.BalanceAsync(Server, guestId);
            Assert.True(debt > 0);
            await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/write-off", cashier, Body(guestId, debt), Guid.NewGuid());
            Assert.Equal(0, await Players.BalanceAsync(Server, guestId));
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, $"/staff/{cashierId}", owner, new { debtWriteOff = false });
        }
    }

    private static object Body(Guid userId, long amount) => new { userId, amount, reason = "тестовый сеанс" };

    private async Task<(Guid GuestId, Guid SessionId)> GuestDebtAsync(string owner)
    {
        var agent = await Seats.OnlineAsync(Server);
        var opened = await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions/guest", owner, new { pcId = agent.PcId, tariffId = Players.Standard, minutes = 60, prepaid = false }, Guid.NewGuid());
        var id = opened.GetProperty("session").GetProperty("id").GetGuid();
        var guestId = opened.GetProperty("user").GetProperty("id").GetGuid();
        Server.Clock.Advance(TimeSpan.FromMinutes(10));
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Tick.RunOnceAsync();
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
        Assert.Equal(-200_000, await Players.BalanceAsync(Server, guestId));
        return (guestId, id);
    }

    private async Task<List<JsonElement>> DebtsAsync(string token) =>
        (await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", token)).GetProperty("guestDebts").EnumerateArray().ToList();
}
