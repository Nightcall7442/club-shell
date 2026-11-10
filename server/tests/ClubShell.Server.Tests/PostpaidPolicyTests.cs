using System.Text.Json;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Who may run a postpaid bill and how far (SERVER_API.md §4.5): the kiosk's postpaid is a member's own pay-as-you-go
/// from the balance — debt limit 0 whatever the club allows the desk, taken when it opens (<c>sessions.debt_limit</c>) —
/// ended by the tick at the last minute the balance pays. Standard costs 20 000 tiyin a minute.
/// </summary>
public sealed class PostpaidPolicyTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private SessionTickWorker Tick => Server.Services.GetRequiredService<SessionTickWorker>();

    /// <summary>50 000 pays 2 minutes; the club's 10 000 000 member debt limit is the desk's, not the kiosk's.</summary>
    [Fact]
    public async Task A_kiosk_member_postpaid_has_no_debt_whatever_the_club_allows_the_desk()
    {
        await Players.ExecuteAsync(Server,
            """UPDATE clubs SET settings = jsonb_set(settings, '{limits}', '{"minorAge":18,"minorCurfew":"22:00","memberDebtLimit":10000000}'::jsonb)""");
        try
        {
            var (agent, player) = await Players.SignedInAsync(Server, balance: 50_000);
            var (status, created) = await Players.StartAsync(agent, player, prepaid: false);
            Assert.True(status == 201, $"expected 201, got {status}: {created}");
            var id = created.GetProperty("id").GetGuid();
            Assert.Equal(("kiosk", 0L), (await Players.ScalarAsync<string>(Server, "SELECT origin FROM sessions WHERE id = @id", new { id }),
                await Players.ScalarAsync<long>(Server, "SELECT debt_limit FROM sessions WHERE id = @id", new { id })));

            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(119));
            Assert.Equal("active", await StateAsync(id));
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
            Assert.Equal(("ended", "timeUp"), (await StateAsync(id), await Players.ScalarAsync<string>(Server, "SELECT end_reason FROM sessions WHERE id = @id", new { id })));
            Assert.Equal(10_000, await Players.BalanceAsync(Server, player.Id));

            // An empty balance does not pay the first minute: 402, nothing opened.
            var (broke, brokePlayer) = await Players.SignedInAsync(Server, balance: 0);
            var (refused, body) = await Players.StartAsync(broke, brokePlayer, prepaid: false);
            Assert.Equal(402, refused);
            Contract.AssertError(body, "insufficientFunds");
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    /// <summary>
    /// The desk caps only its own seats: with a member limit of 50 000 an empty balance is seated and stopped at 2 minutes,
    /// and the open seat keeps that cap after the owner removes it.
    /// </summary>
    [Fact]
    public async Task A_desk_cap_is_taken_when_the_seat_opens()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        await Players.ExecuteAsync(Server,
            """UPDATE clubs SET settings = jsonb_set(settings, '{limits}', '{"minorAge":18,"minorCurfew":"22:00","memberDebtLimit":50000}'::jsonb)""");
        try
        {
            var agent = await Seats.OnlineAsync(Server);
            var member = await Players.CreateAsync(Server, balance: 0);
            var id = (await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner,
                new { pcId = agent.PcId, userId = member.Id, tariffId = Players.Standard, minutes = 60, prepaid = false })).GetProperty("session").GetProperty("id").GetGuid();
            Assert.Equal(50_000, await Players.ScalarAsync<long>(Server, "SELECT debt_limit FROM sessions WHERE id = @id", new { id }));

            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(119));
            Assert.Equal("active", await StateAsync(id));
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
            Assert.Equal("ended", await StateAsync(id));
            Assert.Equal(-40_000, await Players.BalanceAsync(Server, member.Id));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    private async Task AdvanceOnlineAsync(TestAgent agent, TimeSpan by)
    {
        Server.Clock.Advance(by);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Tick.RunOnceAsync();
    }

    private Task<string> StateAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id });
}

/// <summary>
/// The warning before a postpaid cut-off (SERVER_API.md §4.5): a capped postpaid session is warned when the money left pays
/// for at most 5, then 1 more minutes — <c>sessionUpdated</c> with the mark in <c>warningsSent</c>, then a
/// <c>notification</c> push — and ended at the minute boundary it cannot pay. Standard costs 20 000 tiyin a minute: 140 000
/// pays 7 minutes, so the cut-off is at 420 s, the 5-minute warning at 120 s and the 1-minute one at 360 s.
/// </summary>
public sealed class PostpaidWarningTests(KestrelServerFixture server) : LedgerCheckedTest(server), IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task A_capped_postpaid_session_is_warned_5_and_1_minutes_before_its_money_runs_out()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 140_000);
        using var socket = await WsTestSocket.ConnectAsync((KestrelServerFixture)Server, agent.AccessToken);
        await Wait.UntilAsync(() => Server.Services.GetRequiredService<AgentSocketHub>().IsConnected(agent.PcId));
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        var id = created.GetProperty("id").GetGuid();
        Assert.Empty((await PushAsync(socket, "sessionUpdated")).GetProperty("warningsSent").EnumerateArray());

        // 119 s: 301 s of money left, no warning yet (the first tick only resyncs the session).
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(119));
        Assert.Equal("", Marks(await PushAsync(socket, "sessionUpdated")));
        Assert.Equal("{}", await WarningsAsync(id));

        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
        Assert.Equal("5", Marks(await PushAsync(socket, "sessionUpdated")));
        var five = await PushAsync(socket, "notification");
        Assert.Equal("warning", five.GetProperty("level").GetString());
        Assert.Contains("5 мин", five.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Equal("{5}", await WarningsAsync(id));

        // A top-up of 40 000 (180 000 pays 9 minutes, the cut-off moves to 540 s) takes the mark back.
        await TopUpAsync(player.Id, 40_000);
        await PushAsync(socket, "walletUpdated");
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
        Assert.Equal("", Marks(await PushAsync(socket, "sessionUpdated")));
        Assert.Equal("{}", await WarningsAsync(id));

        // Spent down again: the 5-minute warning at 240 s, the 1-minute one at 480 s, the end at 540 s.
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(119));
        Assert.Equal("5", Marks(await PushAsync(socket, "sessionUpdated")));
        await PushAsync(socket, "notification");

        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(240));
        Assert.Equal("5,1", Marks(await PushAsync(socket, "sessionUpdated")));
        Assert.Contains("1 мин", (await PushAsync(socket, "notification")).GetProperty("body").GetString(), StringComparison.Ordinal);

        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(60));
        Assert.Equal("ended", await Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id }));
        Assert.Equal(0, await Players.BalanceAsync(Server, player.Id));
    }

    /// <summary>A desk seat without a cap is never warned: it is not cut off.</summary>
    [Fact]
    public async Task A_postpaid_seat_without_a_cap_is_not_warned()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var agent = await Seats.OnlineAsync(Server);
        var member = await Players.CreateAsync(Server, balance: 0);
        var id = (await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner,
            new { pcId = agent.PcId, userId = member.Id, tariffId = Players.Standard, minutes = 60, prepaid = false })).GetProperty("session").GetProperty("id").GetGuid();
        await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(10));
        Assert.Equal(("active", "{}"), (await Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id }), await WarningsAsync(id)));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { sessionId = id });
    }

    private async Task TopUpAsync(Guid userId, long amount)
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", owner, new { userId, amount, method = "cash" });
    }

    private async Task AdvanceOnlineAsync(TestAgent agent, TimeSpan by)
    {
        Server.Clock.Advance(by);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Server.Services.GetRequiredService<SessionTickWorker>().RunOnceAsync();
    }

    private Task<string> WarningsAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT warnings_sent::text FROM sessions WHERE id = @id", new { id });

    private static string Marks(JsonElement session) => string.Join(",", session.GetProperty("warningsSent").EnumerateArray().Select(m => m.GetInt32()));

    private static async Task<JsonElement> PushAsync(WsTestSocket socket, string name)
    {
        var frame = await socket.ReceiveAsync();
        Assert.Equal(("push", name), (frame.GetProperty("type").GetString(), frame.GetProperty("name").GetString()));
        Contract.AssertMessage("push" + char.ToUpperInvariant(name[0]) + name[1..], frame);
        return frame.GetProperty("payload");
    }
}
