using System.Text.Json;
using ClubShell.Server.Wallet;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The cash desk's money rules: the counter takes money only in an open shift — top-up, open and extend answer
/// <c>409 shiftClosed</c> without one and leave nothing behind (no ledger row, no journal entry, no stored key), ending a
/// session needs none; the X and Z reports split the top-ups by method (<c>topUpByMethod</c>) while <c>expectedCash</c>
/// still counts cash only, and a Z report stored before the split reads with the rest of its top-ups as <c>other</c>.
/// </summary>
public sealed class ShiftGateTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Top_up_open_and_extend_need_an_open_shift_and_end_does_not()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 5_000_000);
        var key = Guid.NewGuid();
        var topUp = new { userId = player.Id, amount = 1_000_000, method = "card" };
        var open = new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 };

        // The vendored contract declares no 409 for adminTopUp: a client without the response validator.
        var (status, body) = await SendRawAsync(Server, HttpMethod.Post, "/wallet/topup", cashier, topUp, key);
        Assert.Equal(409, status);
        Contract.AssertError(body, "conflict", "shiftClosed");
        Contract.AssertError(await ExpectAsync(Server, 409, HttpMethod.Post, "/sessions", cashier, open), "conflict", "shiftClosed");
        Assert.Equal((1, 0, 0), (
            await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id", new { player.Id }),
            await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE user_id = @Id", new { player.Id }),
            await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM sessions WHERE user_id = @Id", new { player.Id })));

        // In a shift both go through, the refused key included: a refusal is not stored.
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/open", cashier, new { openingCash = 0 });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, topUp, key);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, open);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/extend", cashier, new { pcId = agent.PcId, minutes = 30 });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 0 });

        // After the close no more time is sold, but ending gives the unused 90 minutes back to the balance.
        Contract.AssertError(
            await ExpectAsync(Server, 409, HttpMethod.Post, "/sessions/extend", cashier, new { pcId = agent.PcId, minutes = 30 }), "conflict", "shiftClosed");
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        Assert.Equal(1_800_000, ended.GetProperty("refunded").GetProperty("amount").GetInt64());
        Assert.Equal(6_000_000, await Players.BalanceAsync(Server, player.Id));
    }

    [Fact]
    public async Task X_and_Z_split_the_top_ups_by_method_and_expected_cash_counts_cash_only()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var player = await Players.CreateAsync(Server, balance: 0);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/open", cashier, new { openingCash = 100_000 });
        foreach (var (method, amount) in new[] { ("cash", 1_000_000), ("card", 2_000_000), ("payme", 300_000), ("click", 400_000), ("uzum", 500_000) })
        {
            await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount, method });
        }

        // A top-up row with no method (the counter writes none such): it joins the open shift as "other".
        await using (var c = await Server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var clubId = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(c, "SELECT id FROM clubs", transaction: tx);
            await Ledger.PostAsync(c, tx, player.Id, allowOverdraft: false, Server.Clock.GetUtcNow(), new LedgerLine("topUp", 200_000, "test", clubId));
            await tx.CommitAsync();
        }

        const string split = """{"cash":1000000,"card":2000000,"payme":300000,"click":400000,"uzum":500000,"other":200000}""";
        var x = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier)).GetProperty("x");
        Assert.Equal(split, x.GetProperty("topUpByMethod").GetRawText());
        Assert.Equal((1_000_000L, 3_400_000L, 6), (x.GetProperty("topUpCash").GetInt64(), x.GetProperty("topUpOther").GetInt64(), x.GetProperty("count").GetInt32()));

        // 100 000 opening + 1 000 000 cash: the card and online top-ups never were in the drawer.
        var closed = await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 1_100_000 });
        Assert.Equal(1_100_000, closed.GetProperty("expectedCash").GetInt64());
        Assert.Equal(split, closed.GetProperty("shift").GetProperty("totals").GetProperty("topUpByMethod").GetRawText());
        var id = closed.GetProperty("shift").GetProperty("id").GetGuid();

        // A Z report stored before the split, closed a day earlier.
        var legacy = Guid.NewGuid();
        await Players.ExecuteAsync(Server,
            """
            INSERT INTO shifts (id, club_id, staff_name, opened_at, closed_at, opening_cash, closing_cash, expected_cash, totals)
            SELECT @legacy, id, 'Кассир Азиз', @at, @at, 0, 700000, 700000,
                   '{"topUpCash":700000,"topUpOther":300000,"sessions":0,"shop":0,"refunds":0,"bonuses":0,"count":2}'::jsonb
            FROM clubs
            """,
            new { legacy, at = Server.Clock.GetUtcNow().AddDays(-1) });
        var history = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier)).GetProperty("history").EnumerateArray()
            .ToDictionary(s => s.GetProperty("id").GetGuid(), s => s.GetProperty("totals").GetProperty("topUpByMethod").GetRawText());
        Assert.Equal(split, history[id]);
        Assert.Equal("""{"cash":700000,"card":0,"payme":0,"click":0,"uzum":0,"other":300000}""", history[legacy]);
    }

    [Fact]
    public async Task Paying_with_open_or_extend_is_one_operation_and_a_refused_session_books_nothing()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var agent = await TestAgent.CreateAsync(Server);
        var other = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 0);
        var banned = await Players.CreateAsync(Server, balance: 0);
        await Players.ProfileAsync(Server, banned, blacklisted: true);
        await OpenShiftAsync(Server, cashier);
        object Open(Guid pcId, Guid userId, object payment) => new { pcId, userId, tariffId = Players.Standard, minutes = 60, payment };

        // 60 minutes cost 1 200 000: paid by card with the open, 30 more by Click with the extend; the balance ends at zero.
        var opened = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, Open(agent.PcId, player.Id, new { amount = 1_200_000, method = "card" }));
        Assert.Equal((1_200_000L, 0L), (opened.GetProperty("charged").GetProperty("amount").GetInt64(), opened.GetProperty("balance").GetProperty("amount").GetInt64()));
        var extended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/extend", cashier,
            new { pcId = agent.PcId, minutes = 30, payment = new { amount = 600_000, method = "click" } });
        Assert.Equal(0, extended.GetProperty("balance").GetProperty("amount").GetInt64());

        // Refused after the money: the blacklist (403) and a payment short of the price (402) — and before it, a bad payment.
        Assert.Equal("blacklisted", (await ExpectAsync(Server, 403, HttpMethod.Post, "/sessions", cashier,
            Open(other.PcId, banned.Id, new { amount = 1_200_000, method = "cash" }))).GetProperty("error").GetProperty("details").GetProperty("rule").GetString());
        var poor = await Players.CreateAsync(Server, balance: 0);
        Contract.AssertError(await ExpectAsync(Server, 402, HttpMethod.Post, "/sessions", cashier, Open(other.PcId, poor.Id, new { amount = 100_000, method = "cash" })), "insufficientFunds");
        foreach (var (payment, field, reason) in new (object, string, string)[]
        {
            (new { amount = 100_000 }, "payment.method", "required"),
            (new { amount = 100_000, method = "visa" }, "payment.method", "enum"),
            (new { amount = 0, method = "cash" }, "payment.amount", "min"),
            (new { method = "cash" }, "payment.amount", "required"),
        })
        {
            var details = (await ExpectAsync(Server, 400, HttpMethod.Post, "/sessions", cashier, Open(other.PcId, poor.Id, payment))).GetProperty("error").GetProperty("details");
            Assert.Equal((field, reason), (details.GetProperty("field").GetString(), details.GetProperty("reason").GetString()));
        }

        foreach (var refused in new[] { banned.Id, poor.Id })
        {
            Assert.Equal((0, 0, 0L), (
                await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @refused", new { refused }),
                await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE user_id = @refused", new { refused }),
                await Players.BalanceAsync(Server, refused)));
        }

        var split = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier)).GetProperty("x").GetProperty("topUpByMethod");
        Assert.Equal((0L, 1_200_000L, 600_000L), (split.GetProperty("cash").GetInt64(), split.GetProperty("card").GetInt64(), split.GetProperty("click").GetInt64()));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 0 }); // the other tests start without one
    }

    [Fact]
    public async Task The_club_api_key_tops_up_without_a_shift()
    {
        var player = await Players.CreateAsync(Server, balance: 0);
        var key = await ApiKeyAsync(Server);
        try
        {
            Assert.Equal(JsonValueKind.Null, (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", key)).GetProperty("shift").ValueKind);
            await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", key, new { userId = player.Id, amount = 500_000, method = "payme" });
            Assert.Equal(500_000, await Players.BalanceAsync(Server, player.Id));
        }
        finally
        {
            await ClearApiKeyAsync(Server);
        }
    }

    private static async Task<(int Status, JsonElement Body)> SendRawAsync(
        ServerFixture server, HttpMethod method, string path, string? token, object? body = null, Guid? key = null)
    {
        using var raw = server.CreateDefaultClient();
        using var response = await raw.SendAsync(Request(method, path, token, body, key));
        return ((int)response.StatusCode, await Players.ReadAsync(response, (int)response.StatusCode));
    }
}

/// <summary>
/// The counter's client picker (<c>GET /admin/clients/lookup</c>, beyond the contract): at most 8, best match first — the
/// card or the login exactly, a word of the name (any case, Cyrillic too) or the login starting with <c>q</c>, <c>q</c>
/// inside either, then inside the phone's digits; a <c>q</c> under 2 characters lists the most recently active clients.
/// Guests, deleted clients and staff accounts are never listed; <c>playing</c> is the PC of the open session in this club.
/// </summary>
public sealed class ClientLookupTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Clients_are_found_by_name_login_phone_and_card_best_match_first()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var karim = await AddAsync(cashier, new { displayName = "Карим Валиев", username = "karim", phone = "+998 90 123 45 21", cardId = "CARD-77" });
        var abdukarim = await AddAsync(cashier, new { displayName = "Абдукарим", username = "abdukarim" });
        var farrux = await AddAsync(cashier, new { displayName = "Фаррух", username = "farrux" });

        Assert.Equal([karim, abdukarim], await IdsAsync(cashier, "karim")); // the login exactly, then inside one
        Assert.Equal([karim, abdukarim], await IdsAsync(cashier, "Кари")); // the name starts with it, then has it inside
        Assert.Equal([karim], await IdsAsync(cashier, "валиев")); // a word of the name
        Assert.Equal([karim], await IdsAsync(cashier, "card-77")); // the card, any case
        foreach (var phone in new[] { "4521", "45 21", "+998 90" })
        {
            Assert.Equal([karim], await IdsAsync(cashier, phone));
        }

        var found = (await LookupAsync(cashier, "karim")).GetProperty("items");
        Assert.Equal(
            """{"id":"%id","displayName":"Карим Валиев","username":"karim","phoneTail":"4521","balance":{"amount":0,"currency":"UZS"},"bonus":{"amount":0,"currency":"UZS"},"cardId":"CARD-77","playing":null,"blacklisted":false}"""
                .Replace("%id", karim.ToString(), StringComparison.Ordinal),
            found[0].GetRawText());
        var plain = (await LookupAsync(cashier, "farrux")).GetProperty("items")[0];
        Assert.Equal((farrux, JsonValueKind.Null, JsonValueKind.Null), (plain.GetProperty("id").GetGuid(), plain.GetProperty("phoneTail").ValueKind, plain.GetProperty("cardId").ValueKind));

        // A blacklisted client is listed, marked: the desk greys them out.
        await Players.ExecuteAsync(Server,
            "INSERT INTO client_profiles (club_id, user_id, blacklisted) SELECT id, @farrux, true FROM clubs ON CONFLICT (club_id, user_id) DO UPDATE SET blacklisted = true",
            new { farrux });
        Assert.True((await LookupAsync(cashier, "farrux")).GetProperty("items")[0].GetProperty("blacklisted").GetBoolean());

        // Never: a guest, a deleted client, a staff account — not even by the exact login.
        var guest = await Players.CreateAsync(Server, role: "guest");
        var admin = await Players.CreateAsync(Server, role: "admin");
        var deleted = await Players.CreateAsync(Server);
        await Players.ExecuteAsync(Server, "UPDATE users SET deleted_at = now() WHERE id = @Id", new { deleted.Id });
        foreach (var hidden in new[] { guest, admin, deleted })
        {
            Assert.Empty(await IdsAsync(cashier, hidden.Username));
        }

        // At most 8.
        for (var i = 0; i < 10; i++)
        {
            await AddAsync(cashier, new { displayName = $"Тест {i}", username = $"test-{i}" });
        }

        Assert.Equal(8, (await IdsAsync(cashier, "ТЕСТ")).Count);
    }

    [Fact]
    public async Task A_short_query_lists_the_most_recently_active_and_playing_names_the_pc()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await AddAsync(cashier, new { displayName = "Шахзод", username = "shahzod" });
        var other = await AddAsync(cashier, new { displayName = "Отабек", username = "otabek" });
        await OpenShiftAsync(Server, owner);

        // Wallet movements a minute apart, after every other client of the network appeared: the last one moved comes first.
        Server.Clock.Advance(TimeSpan.FromMinutes(1));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player, amount = 3_000_000, method = "cash" });
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new { pcId = agent.PcId, userId = player, tariffId = Players.Standard, minutes = 60 });

        var item = (await LookupAsync(cashier, "shahzod")).GetProperty("items")[0];
        var pcName = await Players.ScalarAsync<string>(Server, "SELECT name FROM pcs WHERE id = @PcId", new { agent.PcId });
        Assert.Equal((agent.PcId, pcName), (item.GetProperty("playing").GetProperty("pcId").GetGuid(), item.GetProperty("playing").GetProperty("pcName").GetString()));
        Assert.Equal(1_800_000, item.GetProperty("balance").GetProperty("amount").GetInt64());

        // One character is not a search yet: the most recently active.
        Server.Clock.Advance(TimeSpan.FromMinutes(1));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId = other, amount = 100_000, method = "cash" });
        foreach (var q in new[] { "", " ", "ш" })
        {
            var recent = await IdsAsync(cashier, q);
            Assert.True(recent.Count is > 2 and <= 8, string.Join(",", recent));
            Assert.Equal([other, player], recent.Take(2));
        }

        using var raw = Server.CreateDefaultClient();
        using var anonymous = await raw.SendAsync(Request(HttpMethod.Get, "/clients/lookup?q=sh", null));
        await Players.ReadAsync(anonymous, 401);
    }

    private async Task<Guid> AddAsync(string token, object body) =>
        (await ExpectAsync(Server, 200, HttpMethod.Post, "/clients", token, body)).GetProperty("client").GetProperty("id").GetGuid();

    private async Task<List<Guid>> IdsAsync(string token, string q) =>
        [.. (await LookupAsync(token, q)).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())];

    /// <summary>Beyond the contract, so a client without the response validator.</summary>
    private async Task<JsonElement> LookupAsync(string token, string q)
    {
        using var raw = Server.CreateDefaultClient();
        using var response = await raw.SendAsync(Request(HttpMethod.Get, "/clients/lookup?q=" + Uri.EscapeDataString(q), token));
        return await Players.ReadAsync(response, 200);
    }
}
