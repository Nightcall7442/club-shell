using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// SHELL_CHANGES 86: a PC whose agent reports it is still attaching its games disks takes no one from the desk; a stale
/// heartbeat (the PC switched off) or an agent that does not report readiness seats as before.
/// </summary>
public sealed class ReadinessSeatTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task A_pc_preparing_or_not_ready_refuses_a_desk_seat_and_a_ready_one_takes_it()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var agent = await TestAgent.CreateAsync(Server);
        var player = (await Players.CreateAsync(Server)).Id;
        object Seat() => new { pcId = agent.PcId, userId = player, tariffId = Players.Standard, minutes = 60 };

        foreach (var state in new[] { "preparing", "notReady" })
        {
            await HeartbeatAsync(agent.PcId, state, TimeSpan.Zero);
            var refused = await RawExpectAsync(Server, 409, HttpMethod.Post, "/sessions", cashier, Seat());
            Contract.AssertError(refused, "conflict", "pcNotReady");
            Assert.Equal(state, refused.GetProperty("error").GetProperty("details").GetProperty("state").GetString());
        }

        // A heartbeat ten minutes old is a PC that is off: seated as before.
        await HeartbeatAsync(agent.PcId, "preparing", TimeSpan.FromMinutes(10));
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, Seat());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });

        await HeartbeatAsync(agent.PcId, "ready", TimeSpan.Zero);
        await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", cashier, Seat());
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });
    }

    private Task HeartbeatAsync(Guid pcId, string state, TimeSpan age) =>
        Players.ExecuteAsync(
            Server,
            """
            UPDATE pcs SET last_heartbeat_at = now() - @age,
                           last_heartbeat = jsonb_set(coalesce(last_heartbeat, '{}'::jsonb), '{readiness}', jsonb_build_object('state', @state, 'disks', '[]'::jsonb))
            WHERE id = @pcId
            """,
            new { pcId, state, age });
}
