using System.Text.Json;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Postpaid from the desk (cash desk part 2, D-30..D-35, D-38): <c>prepaid:false</c> seats a member on the balance (and
/// <c>limits.memberDebtLimit</c> below zero, off by default) or a guest under <c>limits.guestPostpaid</c>; the desk end
/// charges the frozen price; a debt is taken with <c>adminTopUp {settleDebt}</c> of exactly the debt in tiyin, with no bonus;
/// an ordinary top-up's tier bonus counts only the money above a debt. A package or a payment never goes with postpaid, and
/// a postpaid open racing the close is refused rather than journaled without a shift. Standard costs 20 000 tiyin a minute.
/// </summary>
public sealed class DeskPostpaidTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private SessionTickWorker Tick => Server.Services.GetRequiredService<SessionTickWorker>();

    [Fact]
    public async Task A_member_plays_postpaid_from_the_balance_and_the_desk_end_charges_the_frozen_price()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var shiftId = (await ExpectAsync(Server, 200, HttpMethod.Get, "/shift", cashier)).GetProperty("shift").GetProperty("id").GetGuid();
        var agent = await TestAgent.CreateAsync(Server);
        var member = await Players.CreateAsync(Server, balance: 5_000_000);

        var opened = await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, Postpaid(agent.PcId, member.Id));
        Assert.Equal((-1, false, 0L, 5_000_000L), (opened.GetProperty("session").GetProperty("secondsLeft").GetInt32(), opened.GetProperty("session").GetProperty("isPrepaid").GetBoolean(),
            Amount(opened.GetProperty("charged")), Amount(opened.GetProperty("balance"))));
        Assert.False(opened.TryGetProperty("payment", out _));
        var id = opened.GetProperty("session").GetProperty("id").GetGuid();
        var journal = await Players.ScalarAsync<string>(Server,
            "SELECT shift_id::text || ' ' || (meta ->> 'prepaid') || ' ' || (meta ->> 'minutes') FROM audit_entries WHERE action = 'sessionOpen' AND meta ->> 'sessionId' = @id",
            new { id = id.ToString() });
        Assert.Equal($"{shiftId} false 0", journal);

        Server.Clock.Advance(TimeSpan.FromMinutes(10));
        var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { sessionId = id });
        Assert.Equal((200_000L, 0L, 4_800_000L), (Amount(ended.GetProperty("charged")), Amount(ended.GetProperty("refunded")), Amount(ended.GetProperty("balance"))));
        Assert.False(ended.TryGetProperty("payable", out _));
    }

    [Fact]
    public async Task Member_debt_follows_limits_memberDebtLimit()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var member = await Players.CreateAsync(Server, balance: 0);
        var agent = await TestAgent.CreateAsync(Server);

        // Off by default: an empty balance does not pay the first minute.
        Contract.AssertError(await ExpectAsync(Server, 402, HttpMethod.Post, "/sessions", cashier, Postpaid(agent.PcId, member.Id)), "insufficientFunds");
        try
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", memberDebtLimit = 50_000 } });
            Assert.Equal(50_000, (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", cashier)).GetProperty("limits").GetProperty("memberDebtLimit").GetInt64());
            Assert.Equal(("limits.memberDebtLimit", "min"), Field(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner,
                new { limits = new { minorAge = 18, minorCurfew = "22:00", memberDebtLimit = -1 } })));

            // 50 000 below zero pays two minutes (40 000); the tick does not start the third.
            var id = (await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, Postpaid(agent.PcId, member.Id))).GetProperty("session").GetProperty("id").GetGuid();
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(119));
            Assert.Equal("active", await StateAsync(id));
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
            Assert.Equal("ended", await StateAsync(id));
            Assert.Equal(-40_000, await Players.BalanceAsync(Server, member.Id));
            var debt = Assert.Single(await DebtsAsync(cashier), d => d.GetProperty("userId").GetGuid() == member.Id);
            Assert.Equal((40_000L, "member"), (Amount(debt.GetProperty("debt")), debt.GetProperty("role").GetString()));

            // The debt in tiyin: a club API key top-up leaves 38 766.
            await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", await ApiKeyAsync(Server), new { userId = member.Id, amount = 1_234, method = "payme" });
            await ClearApiKeyAsync(Server);
            var changed = await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/topup", cashier, Settle(member.Id, 40_000));
            Contract.AssertError(changed, "conflict", "debtChanged");
            Assert.Equal(38_766, Amount(changed.GetProperty("error").GetProperty("details").GetProperty("debt")));
            var settled = await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, Settle(member.Id, 38_766));
            Assert.Equal((0L, 0L), (Amount(settled.GetProperty("balance")), Amount(settled.GetProperty("bonus"))));
            Assert.DoesNotContain(await DebtsAsync(cashier), d => d.GetProperty("userId").GetGuid() == member.Id);
            Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/topup", cashier, Settle(member.Id, 1)), "conflict", "noDebt");
            Assert.True(await Players.ScalarAsync<bool>(Server,
                "SELECT (meta ->> 'debt')::boolean FROM audit_entries WHERE action = 'topUp' AND user_id = @Id AND amount = 38766", new { member.Id }));

            // A debt big enough for a tier (5 % from 5 000 000): the exact payment still gets no bonus, no bigTopup event and
            // no topupAtLeast firing; an ordinary top-up's bonus counts only the money above the debt.
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
            {
                limits = new { minorAge = 18, minorCurfew = "22:00", memberDebtLimit = 10_000_000 },
                automation = new[] { new { id = "big", name = "big", enabled = true, trigger = new { kind = "topupAtLeast", value = 1 }, action = new { kind = "message", text = "Спасибо" } } },
            });
            var payer = await DebtorAsync(cashier, 6_000_000);
            var exact = await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, Settle(payer.Id, 6_000_000));
            Assert.Equal(0, Amount(exact.GetProperty("bonus")));
            var settledId = exact.GetProperty("transaction").GetProperty("id").GetGuid();
            Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM rule_firings WHERE target_key = @key", new { key = $"topup:{settledId}" }));

            // 20 000 000 against a debt of 6 000 000: the bonus of 14 000 000 (10 %), not of 20 000 000 (15 %).
            var topper = await DebtorAsync(cashier, 6_000_000);
            var topUp = await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = topper.Id, amount = 20_000_000, method = "cash" });
            Assert.Equal((1_400_000L, 15_400_000L), (Amount(topUp.GetProperty("bonus")), Amount(topUp.GetProperty("balance"))));
            var ordinaryId = topUp.GetProperty("transaction").GetProperty("id").GetGuid();
            Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM rule_firings WHERE target_key = @key", new { key = $"topup:{ordinaryId}" }));
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { automation = Array.Empty<object>() });
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task A_desk_guest_postpaid_follows_guestPostpaid()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        object Seat() => new { pcId = agent.PcId, tariffId = Players.Standard, minutes = 60, prepaid = false };
        Assert.Equal("postpaidNotAllowed", (await RawExpectAsync(Server, 403, HttpMethod.Post, "/sessions/guest", cashier, Seat(), Guid.NewGuid()))
            .GetProperty("error").GetProperty("details").GetProperty("rule").GetString());

        await Players.ExecuteAsync(Server, """UPDATE clubs SET settings = jsonb_set(settings, '{limits}', '{"minorAge":18,"minorCurfew":"22:00","guestPostpaid":true}'::jsonb)""");
        try
        {
            var opened = await RawExpectAsync(Server, 201, HttpMethod.Post, "/sessions/guest", cashier, Seat(), Guid.NewGuid());
            Assert.Equal((-1, 0L), (opened.GetProperty("session").GetProperty("secondsLeft").GetInt32(), Amount(opened.GetProperty("charged"))));
            var guestId = opened.GetProperty("user").GetProperty("id").GetGuid();
            Server.Clock.Advance(TimeSpan.FromMinutes(10));
            var ended = await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
            Assert.Equal((200_000L, -200_000L, 0L), (Amount(ended.GetProperty("charged")), Amount(ended.GetProperty("balance")), Amount(ended.GetProperty("payable"))));
            Assert.Equal("guest", Assert.Single(await DebtsAsync(cashier), d => d.GetProperty("userId").GetGuid() == guestId).GetProperty("role").GetString());

            await RawExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, Settle(guestId, 200_000));
            Contract.AssertError(await RawExpectAsync(Server, 409, HttpMethod.Post, "/wallet/topup", cashier, Settle(guestId, 200_000)), "conflict", "noDebt");
            Assert.Equal(0, await Players.BalanceAsync(Server, guestId));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task Postpaid_with_a_package_or_a_payment_is_refused()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        var member = await Players.CreateAsync(Server);
        Assert.Equal(("prepaid", "package"), Field(await ExpectAsync(Server, 400, HttpMethod.Post, "/sessions", cashier,
            new { pcId = agent.PcId, userId = member.Id, tariffId = Players.NightPack, minutes = 60, prepaid = false })));
        Assert.Equal(("payment", "postpaid"), Field(await ExpectAsync(Server, 400, HttpMethod.Post, "/sessions", cashier,
            new { pcId = agent.PcId, userId = member.Id, tariffId = Players.Standard, minutes = 60, prepaid = false, payment = new { amount = 100_000, method = "cash" } })));

        // The kiosk is refused the same; the offline replay of a postpaid package is recorded as the agent let it happen.
        await agent.LoginAsync(member);
        var (status, body) = await Players.StartAsync(agent, member, tariff: Players.NightPack, prepaid: false);
        Assert.Equal((400, ("prepaid", "package")), (status, Field(body)));
        agent.UserToken = null;
        await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions",
            OfflineReplayTests.Replay(agent, member, Guid.NewGuid(), Server.Clock.GetUtcNow().AddMinutes(-10), prepaid: false, tariff: Players.NightPack), Guid.NewGuid()), 201);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
    }

    /// <summary>
    /// The postpaid open locks the shift: it either commits inside it (its journal entry carries the shift) or, after the
    /// close, is refused with 409 shiftClosed — never a sessionOpen with no shift (the noShift flag of 0 money).
    /// </summary>
    [Fact]
    public async Task A_postpaid_open_racing_the_close_is_refused_and_never_flags_noShift()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        var member = await Players.CreateAsync(Server, balance: 5_000_000);
        for (var i = 0; i < 6; i++)
        {
            await OpenShiftAsync(Server, cashier);
            var agent = await TestAgent.CreateAsync(Server);
            var open = RawAsync(Server, HttpMethod.Post, "/sessions", cashier, Postpaid(agent.PcId, member.Id));
            var close = ExpectAsync(Server, 200, HttpMethod.Post, "/shift/close", cashier, new { closingCash = 0 });
            await Task.WhenAll(open, close);
            var (status, body, _) = await open;
            if (status == 409)
            {
                Contract.AssertError(body, "conflict", "shiftClosed");
            }
            else
            {
                Assert.Equal(201, status);
                await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
            }
        }

        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'sessionOpen' AND user_id = @Id AND shift_id IS NULL", new { member.Id }));
        var flags = (await ExpectAsync(Server, 200, HttpMethod.Get, "/control?days=1", owner)).GetProperty("flags").EnumerateArray();
        Assert.DoesNotContain(flags, f => f.GetProperty("kind").GetString() == "noShift" && f.GetProperty("userId").ValueKind == JsonValueKind.String
            && f.GetProperty("userId").GetGuid() == member.Id);
    }

    private static object Postpaid(Guid pcId, Guid userId) => new { pcId, userId, tariffId = Players.Standard, minutes = 60, prepaid = false };

    private static object Settle(Guid userId, long amount) => new { userId, amount, method = "cash", settleDebt = true };

    /// <summary>A member with a postpaid debt of <paramref name="debt"/> (a desk session of that many tiyin, ended at the desk).</summary>
    private async Task<TestPlayer> DebtorAsync(string cashier, long debt)
    {
        var member = await Players.CreateAsync(Server, balance: 0);
        var agent = await TestAgent.CreateAsync(Server);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, Postpaid(agent.PcId, member.Id));
        Server.Clock.Advance(TimeSpan.FromMinutes(debt / 20_000));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
        Assert.Equal(-debt, await Players.BalanceAsync(Server, member.Id));
        return member;
    }

    private async Task<List<JsonElement>> DebtsAsync(string token) =>
        [.. (await ExpectAsync(Server, 200, HttpMethod.Get, "/overview", token)).GetProperty("guestDebts").EnumerateArray()];

    private async Task AdvanceOnlineAsync(TestAgent agent, TimeSpan by)
    {
        Server.Clock.Advance(by);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Tick.RunOnceAsync();
    }

    private Task<string> StateAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id });

    private static (string?, string?) Field(JsonElement error) =>
        (error.GetProperty("error").GetProperty("details").GetProperty("field").GetString(), error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
}
