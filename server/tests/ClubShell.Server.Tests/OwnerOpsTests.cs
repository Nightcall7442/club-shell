using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The owner's own money at the desk (D-101): a top-up, a paid seat, a bar sale or a write-off by the owner joins no cashier
/// shift — not in X/Z, the expected drawer, «Принято за смену» or the cashier's feed — and works with no shift open; the
/// owner reads them in <c>/shift/operations?owner=true</c> and the journal (<c>owner: true</c>). A cashier's money is as
/// before. Also D-102 (delete a client and write the balance off) and D-103 (the password the desk sets is permanent).
/// </summary>
public sealed class OwnerOpsTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    private static async Task NoShiftAsync(ServerFixture server, string token)
    {
        var state = await ExpectAsync(server, 200, HttpMethod.Get, "/shift", token);
        if (state.GetProperty("shift").ValueKind != JsonValueKind.Null)
        {
            await ExpectAsync(server, 200, HttpMethod.Post, "/shift/close", token, new { closingCash = state.GetProperty("expectedCash").GetInt64() });
        }
    }

    private static List<JsonElement> Items(JsonElement page) => [.. page.GetProperty("items").EnumerateArray()];

    [Fact]
    public async Task An_owner_top_up_stays_out_of_the_cashiers_shift_and_feed_and_the_owner_sees_it()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        var shiftId = await FreshShiftAsync(Server, cashier, openingCash: 100_000);
        var player = await Players.CreateAsync(Server, balance: 0);

        // The cashier's top-up: in the shift and the drawer, as always.
        var mine = (await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 500_000, method = "cash" }))
            .GetProperty("transaction").GetProperty("id").GetGuid();
        // The owner's cash: the owner took it in person.
        var owners = (await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = player.Id, amount = 1_000_000, method = "cash" }))
            .GetProperty("transaction").GetProperty("id").GetGuid();
        Assert.Equal(1_500_000, await Players.BalanceAsync(Server, player.Id));

        var state = await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier);
        Assert.Equal((500_000L, 600_000L), (state.GetProperty("x").GetProperty("topUpCash").GetInt64(), state.GetProperty("expectedCash").GetInt64()));
        Assert.Equal(shiftId, await Players.ScalarAsync<Guid>(Server, "SELECT shift_id FROM ledger_entries WHERE id = @mine", new { mine }));
        Assert.True(await Players.ScalarAsync<bool>(Server, "SELECT owner_op AND shift_id IS NULL FROM ledger_entries WHERE id = @owners", new { owners }));
        Assert.True(await Players.ScalarAsync<bool>(Server,
            "SELECT owner_op AND shift_id IS NULL FROM audit_entries WHERE action = 'topUp' AND meta ->> 'transactionId' = @id", new { id = owners.ToString() }));

        // The cashier's feed and «Принято за смену» have only the cashier's; the owner's list is the owner's.
        var feed = await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?kinds=topUp", cashier);
        Assert.Equal([mine], Items(feed).Select(i => i.GetProperty("paid").GetProperty("transactionId").GetGuid()));
        Assert.Equal(500_000, feed.GetProperty("today").GetProperty("byMethod").GetProperty("cash").GetInt64());
        Contract.AssertError(await RawExpectAsync(Server, 403, HttpMethod.Get, "/shift/operations?owner=true", cashier), "forbidden", "ownerOnly");
        var list = await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?owner=true&kinds=topUp", owner);
        Assert.Equal(JsonValueKind.Null, list.GetProperty("shift").ValueKind);
        var row = Assert.Single(Items(list), i => i.GetProperty("paid").GetProperty("transactionId").GetGuid() == owners);
        Assert.True(row.GetProperty("owner").GetBoolean());
        Assert.DoesNotContain(Items(list), i => i.GetProperty("paid").GetProperty("transactionId").GetGuid() == mine);

        // The journal marks it; cashier control raises nothing for it (no noShift).
        var control = await RawExpectAsync(Server, 200, HttpMethod.Get, "/control?days=1", owner);
        var entry = control.GetProperty("log").EnumerateArray().Single(e => e.GetProperty("action").GetString() == "topUp"
            && e.GetProperty("meta").TryGetProperty("transactionId", out var t) && t.GetString() == owners.ToString());
        Assert.True(entry.GetProperty("owner").GetBoolean());
        Assert.DoesNotContain(control.GetProperty("flags").EnumerateArray(), f => f.GetProperty("id").GetString()!.EndsWith(entry.GetProperty("id").GetString()!));

        // Only the owner takes it back, and the drawer does not move.
        Contract.AssertError(await RawExpectAsync(Server, 403, HttpMethod.Post, "/wallet/topup/cancel", cashier,
            new { transactionId = owners, reasonCode = "wrongAmount" }, Guid.NewGuid()), "forbidden", "ownerOnly");
        await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup/cancel", owner, new { transactionId = owners, reasonCode = "wrongAmount" }, Guid.NewGuid());
        Assert.Equal(500_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(600_000, await ExpectedCashAsync(Server, cashier));
        Assert.True(Items(await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?owner=true&kinds=topUp", owner))
            .Single(i => i.GetProperty("paid").GetProperty("transactionId").GetGuid() == owners).GetProperty("voided").GetBoolean());
        Assert.Empty(Items(await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?kinds=topUpCancel", cashier)));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 600_000 });
    }

    [Fact]
    public async Task The_owner_takes_money_with_no_shift_open_and_the_next_shift_starts_clean()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await NoShiftAsync(Server, cashier);
        var player = await Players.CreateAsync(Server, balance: 0);

        Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 100_000 }),
            "conflict", "shiftClosed");
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = player.Id, amount = 2_000_000, method = "cash" });

        // A paid seat, a bar sale and its void — all the owner's own, none in a shift.
        var agent = await TestAgent.CreateAsync(Server);
        var seated = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner,
            new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60, payment = new { amount = 1_200_000, method = "cash" } });
        var sessionId = seated.GetProperty("session").GetProperty("id").GetGuid();
        var product = await Bar.ProductAsync(Server, owner, 300_000, null);
        var saleId = Guid.NewGuid();
        var sold = await Bar.SoldAsync(Server, owner, Bar.Sale(saleId, 300_000, "cash", null, null, (product, 1)));
        Assert.Equal(JsonValueKind.Null, sold.GetProperty("sale").GetProperty("shiftId").ValueKind);
        // A cashier: no shift, no void (and with one, still only the owner's to take back).
        await Bar.VoidAsync(Server, cashier, saleId, status: 409);
        await Bar.VoidAsync(Server, owner, saleId);
        Assert.Equal(0, await Players.ScalarAsync<int>(Server,
            "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id AND (shift_id IS NOT NULL OR NOT owner_op)", new { player.Id }));
        Assert.Equal(2, await Players.ScalarAsync<int>(Server,
            "SELECT count(*)::int FROM shop_sales WHERE (id = @saleId OR void_of = @saleId) AND owner_op AND shift_id IS NULL", new { saleId }));
        var kinds = Items(await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations?owner=true", owner))
            .Where(i => i.GetProperty("client").ValueKind == JsonValueKind.Null || i.GetProperty("client").GetProperty("id").GetGuid() == player.Id)
            .Select(i => i.GetProperty("kind").GetString()).ToList();
        Assert.Contains("sessionOpen", kinds);
        Assert.Contains("shopSale", kinds);
        Assert.Contains("shopVoid", kinds);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId });

        // The cashier's next shift knows nothing of it.
        Server.Clock.Advance(TimeSpan.FromSeconds(1));
        await FreshShiftAsync(Server, cashier);
        var state = await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier);
        var x = state.GetProperty("x");
        Assert.Equal((0L, 0L, 0L, 0L), (x.GetProperty("topUpCash").GetInt64(), x.GetProperty("sessions").GetInt64(), x.GetProperty("shop").GetInt64(),
            state.GetProperty("expectedCash").GetInt64()));
        Assert.DoesNotContain(Items(await RawExpectAsync(Server, 200, HttpMethod.Get, "/shift/operations", cashier)),
            i => i.GetProperty("kind").GetString() is "topUp" or "sessionOpen" or "shopSale" or "shopVoid");
    }

    [Fact]
    public async Task The_owner_deletes_a_client_writing_the_balance_or_the_debt_off()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await NoShiftAsync(Server, cashier);
        var rich = await Players.CreateAsync(Server, balance: 1_500_000);

        // Only the very balance the owner saw; a cashier never.
        await RawExpectAsync(Server, 403, HttpMethod.Delete, $"/clients/{rich.Id}?writeOff=1500000", cashier);
        var changed = await RawExpectAsync(Server, 409, HttpMethod.Delete, $"/clients/{rich.Id}?writeOff=1000000", owner);
        Assert.Equal(1_500_000, changed.GetProperty("error").GetProperty("details").GetProperty("balance").GetProperty("amount").GetInt64());
        await RawExpectAsync(Server, 400, HttpMethod.Delete, $"/clients/{rich.Id}?writeOff=x", owner);
        await RawExpectAsync(Server, 200, HttpMethod.Delete, $"/clients/{rich.Id}?writeOff=1500000", owner);
        Assert.Equal(0, await Players.BalanceAsync(Server, rich.Id));
        Assert.Equal("-1500000|clientDelete|true|true", await Players.ScalarAsync<string>(Server,
            """
            SELECT amount || '|' || (meta ->> 'kind') || '|' || owner_op || '|' || (method IS NULL AND shift_id IS NULL)
            FROM ledger_entries WHERE user_id = @Id AND type = 'adjustment' AND amount < 0
            """, new { rich.Id }));
        Assert.Equal("1500000|1500000", await Players.ScalarAsync<string>(Server,
            "SELECT amount || '|' || (meta ->> 'writtenOff') FROM audit_entries WHERE action = 'clientDelete' AND user_id = @Id", new { rich.Id }));

        // A debt likewise, with no shift open; a client still playing never.
        var owing = await Players.CreateAsync(Server, balance: -300_000);
        await RawExpectAsync(Server, 200, HttpMethod.Delete, $"/clients/{owing.Id}?writeOff=-300000", owner);
        Assert.Equal(0, await Players.BalanceAsync(Server, owing.Id));
        Assert.Equal(300_000, await Players.ScalarAsync<long>(Server, "SELECT amount FROM audit_entries WHERE action = 'clientDelete' AND user_id = @Id", new { owing.Id }));

        var (agent, playing) = await Players.SignedInAsync(Server, balance: 5_000_000);
        var (status, _) = await Players.StartAsync(agent, playing);
        Assert.Equal(201, status);
        var balance = await Players.BalanceAsync(Server, playing.Id);
        Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Delete, $"/clients/{playing.Id}?writeOff={balance}", owner), "conflict", "playing");
        Assert.Equal(balance, await Players.BalanceAsync(Server, playing.Id));
    }

    [Fact]
    public async Task The_password_the_desk_sets_is_the_clients_own_from_then_on()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var (agent, player) = await Players.SignedInAsync(Server);
        await ExpectAsync(Server, 200, HttpMethod.Post, $"/clients/{player.Id}/password", cashier, new { password = "my-own-77" });
        // Signed in with it twice: nothing asks for another password at the next sign-in.
        for (var i = 0; i < 2; i++)
        {
            using var signedIn = await agent.PostAsync("/api/v1/auth/login",
                new { kind = "password", username = player.Username, password = "my-own-77", pcId = agent.PcId, hwid = agent.Hwid });
            var body = await Players.ReadAsync(signedIn, 200);
            Assert.DoesNotContain("mustChange", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
