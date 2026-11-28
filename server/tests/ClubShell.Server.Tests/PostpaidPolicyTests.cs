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
/// The warning before a postpaid cut-off (SERVER_API.md §4.5, D-109): a capped postpaid session is warned when the money left
/// — balance and bonuses — pays for at most 10, 7, 5, then 1 more minutes (the club's marks and the last minute):
/// <c>sessionUpdated</c> with the mark in <c>warningsSent</c>, which the agent shows as <c>session.warning</c> (no
/// <c>notification</c> push any more) — and ended at the minute boundary it cannot pay. Standard costs 20 000 tiyin a minute.
/// </summary>
public sealed class PostpaidWarningTests(KestrelServerFixture server) : LedgerCheckedTest(server), IClassFixture<KestrelServerFixture>
{
    /// <summary>300 000 pays 15 minutes: the cut-off at 900 s, the 10-minute warning at 300 s.</summary>
    [Fact]
    public async Task A_capped_postpaid_session_is_warned_at_10_7_5_and_1_minutes_before_its_money_runs_out()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 300_000);
        using var socket = await WsTestSocket.ConnectAsync((KestrelServerFixture)Server, agent.AccessToken);
        await Wait.UntilAsync(() => Server.Services.GetRequiredService<AgentSocketHub>().IsConnected(agent.PcId));
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        var id = created.GetProperty("id").GetGuid();
        Assert.Empty((await PushAsync(socket, "sessionUpdated")).GetProperty("warningsSent").EnumerateArray());

        // 299 s: 601 s of money left, no warning yet (the first tick only resyncs the session).
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(299));
        Assert.Equal("", Marks(await PushAsync(socket, "sessionUpdated")));
        Assert.Equal("{}", await WarningsAsync(id));

        // The next push after the mark is a sessionUpdated again, never a notification (PushAsync checks the name).
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
        Assert.Equal("10", Marks(await PushAsync(socket, "sessionUpdated")));
        Assert.Equal("{10}", await WarningsAsync(id));

        // A top-up of 40 000 (340 000 pays 17 minutes, the cut-off moves to 1 020 s) takes the mark back.
        await TopUpAsync(player.Id, 40_000);
        await PushAsync(socket, "walletUpdated");
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
        Assert.Equal("", Marks(await PushAsync(socket, "sessionUpdated")));
        Assert.Equal("{}", await WarningsAsync(id));

        // Spent down again: 10 minutes at 420 s, 7 at 600 s, 5 at 720 s, the last minute at 960 s, the end at 1 020 s.
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(119));
        Assert.Equal("10", Marks(await PushAsync(socket, "sessionUpdated")));
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(180));
        Assert.Equal("10,7", Marks(await PushAsync(socket, "sessionUpdated")));
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(120));
        Assert.Equal("10,7,5", Marks(await PushAsync(socket, "sessionUpdated")));
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(240));
        Assert.Equal("10,7,5,1", Marks(await PushAsync(socket, "sessionUpdated")));

        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(60));
        Assert.Equal("ended", await Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id }));
        Assert.Equal(0, await Players.BalanceAsync(Server, player.Id));
    }

    /// <summary>
    /// The owner's own marks (<c>limits.warningMinutes</c>): saved in the club settings, a new config version with
    /// <c>session.warningMinutes</c> for the PCs, and the tick warns at them. 140 000 pays 7 minutes: 3 at 240 s, 1 at 360 s.
    /// </summary>
    [Fact]
    public async Task The_owner_chooses_the_marks_in_the_club_settings()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var (agent, player) = await Players.SignedInAsync(Server, balance: 140_000);
        var version = await Players.ScalarAsync<int>(Server, "SELECT config_version FROM clubs");
        try
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", warningMinutes = new[] { 3 } } });
            Assert.Equal([3], (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("limits").GetProperty("warningMinutes").EnumerateArray().Select(m => m.GetInt32()));
            Assert.Equal(version + 1, await Players.ScalarAsync<int>(Server, "SELECT config_version FROM clubs"));
            var config = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("config")), 200);
            Assert.Equal([3], config.GetProperty("session").GetProperty("warningMinutes").EnumerateArray().Select(m => m.GetInt32()));

            // The same marks again are no config change.
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", warningMinutes = new[] { 3 } } });
            Assert.Equal(version + 1, await Players.ScalarAsync<int>(Server, "SELECT config_version FROM clubs"));

            var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
            var id = created.GetProperty("id").GetGuid();
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(239));
            Assert.Equal("{}", await WarningsAsync(id));
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
            Assert.Equal("{3}", await WarningsAsync(id));
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(120));
            Assert.Equal("{3,1}", await WarningsAsync(id));
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(60));
            Assert.Equal("ended", await Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id }));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
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
