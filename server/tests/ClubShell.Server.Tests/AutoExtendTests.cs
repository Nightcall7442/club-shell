using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Auto-extension, the club's choice (<c>settings.limits.autoExtendMinutes</c>, beyond the contract): a prepaid session that
/// runs out is extended from the balance by the tick, like the cashier's extend (charged, <c>extendSession {charge:false}</c>
/// to the agent); once the balance cannot pay, it ends as before. Standard costs 20 000 tiyin a minute.
/// </summary>
public sealed class AutoExtendTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private SessionTickWorker Tick => Server.Services.GetRequiredService<SessionTickWorker>();

    [Fact]
    public async Task A_session_that_runs_out_is_extended_while_the_balance_pays_then_ends()
    {
        await LimitsAsync("""{"minorAge":18,"minorCurfew":"22:00","autoExtendMinutes":30}""");
        try
        {
            var (agent, player) = await Players.SignedInAsync(Server, balance: 1_200_000);
            var (_, created) = await Players.StartAsync(agent, player, minutes: 30);
            var id = created.GetProperty("id").GetGuid();
            Assert.Equal(600_000, await Players.BalanceAsync(Server, player.Id));

            await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(30));
            Assert.Equal(("active", 3600), (await StateAsync(id), await Players.ScalarAsync<int>(Server, "SELECT purchased_sec FROM sessions WHERE id = @id", new { id })));
            Assert.Equal(0, await Players.BalanceAsync(Server, player.Id));
            using (var commands = await agent.SendAsync(HttpMethod.Get, agent.Path("commands")))
            {
                var command = Assert.Single((await Players.ReadAsync(commands, 200)).GetProperty("items").EnumerateArray());
                Assert.Equal("extendSession", command.GetProperty("name").GetString());
                Assert.Equal((30, false), (command.GetProperty("payload").GetProperty("minutes").GetInt32(), command.GetProperty("payload").GetProperty("charge").GetBoolean()));
            }

            // The next 30 minutes are not covered: ending, then timeUp after the free grace, nothing charged.
            await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(30));
            Assert.Equal("ending", await StateAsync(id));
            await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(60));
            Assert.Equal("ended", await StateAsync(id));
            Assert.Equal(0, await Players.BalanceAsync(Server, player.Id));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task Off_by_default_the_session_ends_with_money_left()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 1_200_000);
        var (_, created) = await Players.StartAsync(agent, player, minutes: 30);
        var id = created.GetProperty("id").GetGuid();

        await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(30));
        Assert.Equal("ending", await StateAsync(id));
        Assert.Equal(600_000, await Players.BalanceAsync(Server, player.Id));
    }

    [Fact]
    public async Task The_owner_sets_it_in_the_club_settings()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        try
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", autoExtendMinutes = 60 } });
            Assert.Equal(60, (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("limits").GetProperty("autoExtendMinutes").GetInt32());
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    private Task LimitsAsync(string json) =>
        Players.ExecuteAsync(Server, "UPDATE clubs SET settings = jsonb_set(settings, '{limits}', @json::jsonb)", new { json });

    private async Task AdvanceOnlineAsync(TestAgent agent, TimeSpan by)
    {
        Server.Clock.Advance(by);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Tick.RunOnceAsync();
    }

    private Task<string> StateAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id });
}
