using System.Text.Json;
using ClubShell.Server.Sessions.Billing;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Time «по сумме» at the desk (D-90): with <c>amount</c> the quote, a member's or a guest's seat and an extension sell the
/// most whole minutes the amount buys at the price the server charges now — the day percent and the single best discount
/// — so the charge is never above the amount; less than the shortest purchase is <c>409 amountTooLow {minutes, total}</c>.
/// Standard costs 1 200 000 tiyin an hour (20 000 a minute), 30..720 minutes; the seed group <c>student</c> has 15 % off.
/// </summary>
public sealed class DeskByAmountTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    // Every day 110 %: a walk-in pays 22 000 a minute, a student 18 700 (20 000 × 1.10 × 0.85).
    private const string Week = """{"pricing":{"weekdayPct":[110,110,110,110,110,110,110],"holidays":[],"holidayPct":110}}""";

    [Fact]
    public void Most_for_is_the_most_whole_minutes_whose_price_fits_within_the_range()
    {
        var tariff = new TariffRow { PricePerHour = 1_200_000, MinMinutes = 30, MaxMinutes = 720 };
        var club = ClubPricing.Parse("Asia/Tashkent", Week);
        var at = DateTimeOffset.Parse("2026-11-23T12:00:00Z");
        Assert.Equal((45, 990_000L), Pick(Pricing.MostFor(tariff, 1_000_000, 30, 720, null, 0, "", at, club)!));
        Assert.Equal((45, 990_000L), Pick(Pricing.MostFor(tariff, 990_000, 30, 720, null, 0, "", at, club)!));
        Assert.Equal((44, 968_000L), Pick(Pricing.MostFor(tariff, 989_999, 30, 720, null, 0, "", at, club)!));
        Assert.Equal((720, 15_840_000L), Pick(Pricing.MostFor(tariff, 100_000_000, 30, 720, null, 0, "", at, club)!));
        Assert.Null(Pricing.MostFor(tariff, 659_900, 30, 720, null, 0, "", at, club));

        static (int, long) Pick(Quote q) => (q.Minutes, q.Total);
    }

    [Fact]
    public async Task A_member_is_seated_and_extended_for_an_amount_with_the_day_rate_and_the_discount()
    {
        await Players.ExecuteAsync(Server, $"UPDATE clubs SET settings = settings || '{Week}'::jsonb");
        try
        {
            var cashier = await LoginAsync(Server, CashierPin);
            await OpenShiftAsync(Server, cashier);
            var agent = await TestAgent.CreateAsync(Server);
            var player = await Players.CreateAsync(Server, balance: 500_000);
            await Players.ProfileAsync(Server, player, groupId: "student");

            // 2 000 000 buys 106 minutes (1 982 200); the 107th would cost 2 000 900.
            var quote = await ExpectAsync(Server, 200, HttpMethod.Post, "/quote", cashier,
                new { tariffId = Players.Standard, pcId = agent.PcId, minutes = 60, userId = player.Id, amount = 2_000_000 });
            Assert.Equal((106, 1_982_200L, 110, 15, 2_120_000L), (quote.GetProperty("minutes").GetInt32(), Amount(quote.GetProperty("total")),
                quote.GetProperty("dayPct").GetInt32(), quote.GetProperty("discountPct").GetInt32(), Amount(quote.GetProperty("base"))));

            // The balance pays 500 000, the desk takes the rest: the server sells the minutes of the amount, not `minutes`.
            var opened = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, new
            {
                pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60, amount = 2_000_000,
                payment = new { amount = 1_482_200, method = "cash" },
            });
            Assert.Equal((1_982_200L, 0L, 106 * 60), (Amount(opened.GetProperty("charged")), Amount(opened.GetProperty("balance")),
                opened.GetProperty("session").GetProperty("secondsLeft").GetInt32()));
            var meta = JsonElement.Parse(await Players.ScalarAsync<string>(Server,
                "SELECT meta::text FROM audit_entries WHERE action = 'sessionOpen' AND user_id = @Id", new { player.Id }));
            Assert.Equal(106, meta.GetProperty("minutes").GetInt32());

            // Extend for 300 000: 16 minutes (299 200), paid in full at the desk.
            var extendQuote = await RawExpectAsync(Server, 200, HttpMethod.Post, "/quote", cashier,
                new { tariffId = Players.Standard, pcId = agent.PcId, minutes = 60, userId = player.Id, amount = 300_000, sessionId = opened.GetProperty("session").GetProperty("id").GetGuid() });
            Assert.Equal((16, 299_200L), (extendQuote.GetProperty("minutes").GetInt32(), Amount(extendQuote.GetProperty("total"))));
            var extended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/extend", cashier,
                new { pcId = agent.PcId, minutes = 60, amount = 300_000, payment = new { amount = 299_200, method = "card" } });
            Assert.Equal((299_200L, 0L), (Amount(extended.GetProperty("charged")), Amount(extended.GetProperty("balance"))));
            Assert.Equal(16, await Players.ScalarAsync<int>(Server,
                "SELECT (meta ->> 'minutes')::int FROM audit_entries WHERE action = 'sessionExtend' AND user_id = @Id", new { player.Id }));
            Assert.Equal(122 * 60, await Players.ScalarAsync<int>(Server, "SELECT purchased_sec FROM sessions WHERE user_id = @Id", new { player.Id }));

            // Too little for the shortest extension (5 minutes = 93 500).
            var low = await RawExpectAsync(Server, 409, HttpMethod.Post, "/sessions/extend", cashier, new { pcId = agent.PcId, minutes = 60, amount = 90_000 });
            Contract.AssertError(low, "conflict", "amountTooLow");
            var details = low.GetProperty("error").GetProperty("details");
            Assert.Equal((5, 93_500L), (details.GetProperty("minutes").GetInt32(), Amount(details.GetProperty("total"))));
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'pricing'");
        }
    }

    [Fact]
    public async Task A_guest_pays_exactly_the_price_of_the_minutes_an_amount_buys()
    {
        await Players.ExecuteAsync(Server, $"UPDATE clubs SET settings = settings || '{Week}'::jsonb");
        try
        {
            var cashier = await LoginAsync(Server, CashierPin);
            await OpenShiftAsync(Server, cashier);
            var agent = await TestAgent.CreateAsync(Server);
            object Seat(long paid) => new { pcId = agent.PcId, tariffId = Players.Standard, minutes = 60, amount = 1_000_000, payment = new { amount = paid, method = "cash" } };

            // A walk-in: 45 minutes for 990 000; the amount itself is not the price (D-48: nothing extra on a throwaway account).
            var quote = await ExpectAsync(Server, 200, HttpMethod.Post, "/quote", cashier, new { tariffId = Players.Standard, pcId = agent.PcId, minutes = 60, amount = 1_000_000 });
            Assert.Equal((45, 990_000L), (quote.GetProperty("minutes").GetInt32(), Amount(quote.GetProperty("total"))));
            var wrong = await RawExpectAsync(Server, 409, HttpMethod.Post, "/sessions/guest", cashier, Seat(1_000_000), Guid.NewGuid());
            Contract.AssertError(wrong, "conflict", "priceChanged");
            Assert.Equal(990_000, Amount(wrong.GetProperty("error").GetProperty("details").GetProperty("total")));

            var opened = await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions/guest", cashier, Seat(990_000), Guid.NewGuid());
            Assert.Equal((990_000L, 0L, 45 * 60), (Amount(opened.GetProperty("charged")), Amount(opened.GetProperty("balance")),
                opened.GetProperty("session").GetProperty("secondsLeft").GetInt32()));
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'pricing'");
        }
    }

    [Fact]
    public async Task An_amount_below_the_shortest_purchase_a_package_or_postpaid_is_refused()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 5_000_000);

        // Standard sells 30 minutes at least: 600 000.
        var low = await RawExpectAsync(Server, 409, HttpMethod.Post, "/quote", cashier, new { tariffId = Players.Standard, pcId = agent.PcId, minutes = 60, amount = 599_900 });
        Contract.AssertError(low, "conflict", "amountTooLow");
        Assert.Equal((30, 600_000L), (low.GetProperty("error").GetProperty("details").GetProperty("minutes").GetInt32(),
            Amount(low.GetProperty("error").GetProperty("details").GetProperty("total"))));
        Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/sessions", cashier,
            new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60, amount = 599_900 }), "conflict", "amountTooLow");
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM sessions WHERE user_id = @Id", new { player.Id }));

        foreach (var (body, reason) in new (object, string)[]
        {
            (new { pcId = agent.PcId, userId = player.Id, tariffId = Players.NightPack, minutes = 60, amount = 5_000_000 }, "package"),
            (new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60, amount = 5_000_000, prepaid = false }, "postpaid"),
            (new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60, amount = 0 }, "min"),
        })
        {
            var refused = await RawExpectAsync(Server, 400, HttpMethod.Post, "/sessions", cashier, body);
            Assert.Equal(("amount", reason), (refused.GetProperty("error").GetProperty("details").GetProperty("field").GetString(),
                refused.GetProperty("error").GetProperty("details").GetProperty("reason").GetString()));
        }

        Assert.Equal(5_000_000, await Players.BalanceAsync(Server, player.Id));
    }
}
