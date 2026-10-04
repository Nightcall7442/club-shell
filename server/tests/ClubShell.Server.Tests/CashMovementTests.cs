using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The drawer (cash desk part 2, D-40..D-42): cash put in and taken out (<c>POST /admin/shift/cash</c>, append-only
/// <c>cash_movements</c>) with a reason, in an open shift, never more out than the drawer is expected to hold; only the
/// server computes <c>expectedCash</c> = opening + staff cash top-ups + in − out − payouts (the club API key's cash top-ups
/// are reported as <c>apiCash</c> and left out); the Z report keeps the movements, the expected drawer and who closed it,
/// and an old Z reads the new fields as 0.
/// </summary>
public sealed class CashMovementTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Cash_in_and_out_change_expected_cash_and_land_in_the_z_report()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier, openingCash: 100_000);
        var player = await Players.CreateAsync(Server, balance: 0);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 1_000_000, method = "cash" });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 2_000_000, method = "card" });

        var key = Guid.NewGuid();
        var moveIn = new { kind = "in", amount = 5_000_000, reasonCode = "change" };
        var added = await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, moveIn, key);
        Assert.Equal(("in", 5_000_000L, "change", JsonValueKind.Null, "Кассир Азиз", 6_100_000L), (
            added.GetProperty("movement").GetProperty("kind").GetString(), added.GetProperty("movement").GetProperty("amount").GetInt64(),
            added.GetProperty("movement").GetProperty("reasonCode").GetString(), added.GetProperty("movement").GetProperty("note").ValueKind,
            added.GetProperty("movement").GetProperty("staffName").GetString(), added.GetProperty("expectedCash").GetInt64()));
        var (_, replayed, wasReplayed) = await RawAsync(Server, HttpMethod.Post, "/shift/cash", cashier, moveIn, key);
        Assert.True(wasReplayed);
        Assert.True(JsonElement.DeepEquals(added, replayed));
        var taken = await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier,
            new { kind = "out", amount = 3_000_000, reasonCode = "collection", note = "  в сейф  " }, Guid.NewGuid());
        Assert.Equal(("в сейф", 3_100_000L), (taken.GetProperty("movement").GetProperty("note").GetString(), taken.GetProperty("expectedCash").GetInt64()));

        // A cash top-up by the club API key is reported apart and never counted in the drawer.
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", await ApiKeyAsync(Server), new { userId = player.Id, amount = 700_000, method = "cash" });
        await ClearApiKeyAsync(Server);
        var state = await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier);
        var x = state.GetProperty("x");
        Assert.Equal((5_000_000L, 3_000_000L, 0L, 700_000L, 1_700_000L), (x.GetProperty("cashIn").GetInt64(), x.GetProperty("cashOut").GetInt64(),
            x.GetProperty("payouts").GetInt64(), x.GetProperty("apiCash").GetInt64(), x.GetProperty("topUpCash").GetInt64()));
        Assert.Equal(3_100_000, state.GetProperty("expectedCash").GetInt64());
        Assert.Equal(2, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM cash_movements WHERE shift_id = @id", new { id = state.GetProperty("shift").GetProperty("id").GetGuid() }));

        // Closed with exactly the expected drawer: no shortfall; the Z keeps the movements, the expected cash and the closer.
        var closed = await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 3_100_000 });
        Assert.Equal(3_100_000, closed.GetProperty("expectedCash").GetInt64());
        var shiftId = closed.GetProperty("shift").GetProperty("id").GetGuid();
        Assert.Equal("0 false", await Players.ScalarAsync<string>(Server,
            "SELECT (meta ->> 'diff') || ' ' || (meta ->> 'shortfall') FROM audit_entries WHERE action = 'shiftClose' AND shift_id = @shiftId", new { shiftId }));
        var z = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier)).GetProperty("history").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == shiftId);
        Assert.Equal((shiftId, 3_100_000L, "Кассир Азиз", 5_000_000L, 3_000_000L), (z.GetProperty("id").GetGuid(), z.GetProperty("expectedCash").GetInt64(),
            z.GetProperty("closedBy").GetString(), z.GetProperty("totals").GetProperty("cashIn").GetInt64(), z.GetProperty("totals").GetProperty("cashOut").GetInt64()));
        Assert.Equal("cashIn cashOut", await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(action, ' ' ORDER BY at, action) FROM audit_entries WHERE shift_id = @shiftId AND action LIKE 'cash%'", new { shiftId }));
    }

    [Fact]
    public async Task Cash_moves_need_a_key_a_reason_a_shift_and_money_in_the_drawer()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 0 });
        foreach (var kind in new[] { "in", "out" })
        {
            Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/shift/cash", cashier, new { kind, amount = 100, reasonCode = "change" }, Guid.NewGuid()),
                "conflict", "shiftClosed");
        }

        await ExpectAsync(Server, 200, HttpMethod.Post, "/shift/open", cashier, new { openingCash = 0 });
        foreach (var (body, field, reason, key) in new (object, string, string, bool)[]
        {
            (new { kind = "in", amount = 100, reasonCode = "change" }, "Idempotency-Key", "required", false),
            (new { kind = "sideways", amount = 100, reasonCode = "change" }, "kind", "enum", true),
            (new { kind = "in", amount = 0, reasonCode = "change" }, "amount", "min", true),
            (new { kind = "in", amount = 10_000_000_001, reasonCode = "change" }, "amount", "max", true),
            (new { kind = "in", amount = 100 }, "reasonCode", "required", true),
            (new { kind = "in", amount = 100, reasonCode = "gift" }, "reasonCode", "enum", true),
            (new { kind = "in", amount = 100, reasonCode = "other" }, "note", "required", true),
            (new { kind = "in", amount = 100, reasonCode = "other", note = " ab " }, "note", "min", true),
            (new { kind = "in", amount = 100, reasonCode = "change", note = new string('n', 201) }, "note", "max", true),
        })
        {
            var details = (await RawExpectAsync(Server, 400, HttpMethod.Post, "/shift/cash", cashier, body, key ? Guid.NewGuid() : null)).GetProperty("error").GetProperty("details");
            Assert.Equal((field, reason), (details.GetProperty("field").GetString(), details.GetProperty("reason").GetString()));
        }

        // Never more out than the drawer holds; "other" with its note is fine.
        var empty = await RawExpectAsync(Server, 409, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = 1, reasonCode = "expenses" }, Guid.NewGuid());
        Contract.AssertError(empty, "conflict", "cashShort");
        Assert.Equal(0, Amount(empty.GetProperty("error").GetProperty("details").GetProperty("available")));
        await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "in", amount = 50_000, reasonCode = "other", note = "Сдача от владельца" }, Guid.NewGuid());
        var shortBody = await RawExpectAsync(Server, 409, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = 60_000, reasonCode = "expenses" }, Guid.NewGuid());
        Assert.Equal(50_000, Amount(shortBody.GetProperty("error").GetProperty("details").GetProperty("available")));
        await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = 50_000, reasonCode = "expenses" }, Guid.NewGuid());

        Contract.AssertError(await RawExpectAsync(Server, 403, HttpMethod.Post, "/shift/cash", await ApiKeyAsync(Server), new { kind = "in", amount = 100, reasonCode = "change" }, Guid.NewGuid()),
            "forbidden", "staffOnly");
        await ClearApiKeyAsync(Server);
    }

    [Fact]
    public async Task The_owner_keeps_cash_out_to_himself()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier, openingCash: 1_000_000);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", cashOutOwnerOnly = true } });
        try
        {
            var refused = await RawExpectAsync(Server, 403, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = 100_000, reasonCode = "collection" }, Guid.NewGuid());
            Contract.AssertError(refused, "forbidden", "ownerOnly");
            await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", owner, new { kind = "out", amount = 100_000, reasonCode = "collection" }, Guid.NewGuid());
            await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "in", amount = 100_000, reasonCode = "change" }, Guid.NewGuid());
            Assert.Equal(1_000_000, await ExpectedCashAsync(Server, cashier));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task Parallel_cash_outs_cannot_both_empty_the_drawer()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var owner = await LoginAsync(Server, OwnerPin);
        for (var i = 0; i < 4; i++)
        {
            await FreshShiftAsync(Server, cashier, openingCash: 1_000_000);
            var results = await Task.WhenAll(
                RawAsync(Server, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = 1_000_000, reasonCode = "collection" }, Guid.NewGuid()),
                RawAsync(Server, HttpMethod.Post, "/shift/cash", owner, new { kind = "out", amount = 1_000_000, reasonCode = "collection" }, Guid.NewGuid()));
            Assert.Equal([200, 409], results.Select(r => r.Status).Order());
            Contract.AssertError(results.Single(r => r.Status == 409).Body, "conflict", "cashShort");
            Assert.Equal(0, await ExpectedCashAsync(Server, cashier));
        }
    }

    /// <summary>The close holds the shift FOR UPDATE: a racing cash-out waits, then finds no open shift — or lands before the Z sums it.</summary>
    [Fact]
    public async Task A_cash_out_racing_the_close_is_refused()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        for (var i = 0; i < 4; i++)
        {
            var shiftId = await FreshShiftAsync(Server, cashier, openingCash: 1_000_000);
            var move = RawAsync(Server, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = 400_000, reasonCode = "collection" }, Guid.NewGuid());
            var close = ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 1_000_000 });
            await Task.WhenAll(move, close);
            var (status, body, _) = await move;
            if (status == 409)
            {
                Contract.AssertError(body, "conflict", "shiftClosed");
            }
            else
            {
                Assert.Equal(200, status);
            }

            var z = (await close).GetProperty("shift").GetProperty("totals");
            Assert.Equal(await Players.ScalarAsync<long>(Server, "SELECT coalesce(sum(amount), 0)::bigint FROM cash_movements WHERE shift_id = @shiftId AND kind = 'out'", new { shiftId }),
                z.GetProperty("cashOut").GetInt64());
        }
    }

    [Fact]
    public async Task Movements_are_append_only_and_a_big_cash_out_raises_suspicious()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await FreshShiftAsync(Server, cashier);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            webhooks = new[] { new { id = "watch", url = "https://hooks.example.com/watch", events = new[] { "suspicious" }, enabled = true, lastStatus = (int?)null, lastAt = (string?)null } },
        });
        try
        {
            await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "in", amount = 30_000_000, reasonCode = "change" }, Guid.NewGuid());
            await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = 1_000_000, reasonCode = "expenses" }, Guid.NewGuid());
            Assert.Equal(0, await SuspiciousAsync());
            await RawExpectAsync(Server, 200, HttpMethod.Post, "/shift/cash", cashier, new { kind = "out", amount = 20_000_000, reasonCode = "collection" }, Guid.NewGuid());
            Assert.Equal(1, await SuspiciousAsync());
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { webhooks = Array.Empty<object>() });
        }

        await using var c = await Server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        foreach (var sql in new[] { "UPDATE cash_movements SET amount = 1", "DELETE FROM cash_movements", "TRUNCATE cash_movements" })
        {
            await using var command = new NpgsqlCommand(sql, c);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_old_z_report_reads_the_new_fields_as_zero()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var legacy = Guid.NewGuid();
        await Players.ExecuteAsync(Server,
            """
            INSERT INTO shifts (id, club_id, staff_name, opened_at, closed_at, opening_cash, closing_cash, expected_cash, totals)
            SELECT @legacy, id, 'Кассир Азиз', @at, @at, 0, 700000, 700000,
                   '{"topUpCash":700000,"topUpOther":0,"sessions":0,"shop":0,"refunds":0,"bonuses":0,"count":1,"topUpByMethod":{"cash":700000,"card":0,"payme":0,"click":0,"uzum":0,"other":0}}'::jsonb
            FROM clubs
            """,
            new { legacy, at = Server.Clock.GetUtcNow().AddDays(-1) });
        var z = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier)).GetProperty("history").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == legacy);
        var totals = z.GetProperty("totals");
        Assert.Equal((0L, 0L, 0L, 0L), (totals.GetProperty("cashIn").GetInt64(), totals.GetProperty("cashOut").GetInt64(), totals.GetProperty("payouts").GetInt64(),
            totals.GetProperty("apiCash").GetInt64()));
        Assert.Equal((700_000L, JsonValueKind.Null), (z.GetProperty("expectedCash").GetInt64(), z.GetProperty("closedBy").ValueKind));
    }

    private Task<int> SuspiciousAsync() =>
        Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM webhook_outbox WHERE event = 'suspicious' AND payload ->> 'text' LIKE '%изъятие%'");
}
