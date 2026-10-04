using System.Text.Json;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Tests;

/// <summary>The purchase rules of <c>POST /sessions</c> (DESIGN §5.2), one per test, plus the authorization of the online call.</summary>
public sealed class PurchaseRulesTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Unknown_tariff_is_404_tariff()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (status, body) = await Players.StartAsync(agent, player, tariff: Guid.NewGuid());
        AssertError(404, "notFound", status, body);
        Assert.Equal("tariff", Details(body).GetProperty("what").GetString());
    }

    [Fact]
    public async Task Pc_in_maintenance_is_denied()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        await Players.ExecuteAsync(Server, "UPDATE pcs SET maintenance = true WHERE id = @PcId", new { agent.PcId });
        var (status, body) = await Players.StartAsync(agent, player);
        AssertRule("pcMaintenance", status, body);
    }

    /// <summary>
    /// A sign-in onto a PC that holds another player's open session is refused (<c>403 pcOccupied</c>, D-26), so two players
    /// meet on one PC only when the PC's offline log replays a session after the second one signed in: buying time is then
    /// 409 with the open session.
    /// </summary>
    [Fact]
    public async Task Open_session_of_the_pc_is_409_with_its_id()
    {
        var (agent, second) = await Players.SignedInAsync(Server);
        var token = agent.UserToken;
        var first = await Players.CreateAsync(Server);
        agent.UserToken = null;
        var session = await Players.ReadAsync(await agent.PostAsync(
            "/api/v1/sessions", OfflineReplayTests.Replay(agent, first, Guid.NewGuid(), Server.Clock.GetUtcNow().AddMinutes(-5)), Guid.NewGuid()), 201);

        agent.UserToken = token;
        var (status, body) = await Players.StartAsync(agent, second);
        AssertError(409, "sessionAlreadyActive", status, body);
        Assert.Equal(session.GetProperty("id").GetGuid(), Details(body).GetProperty("sessionId").GetGuid());
        Assert.Equal(agent.PcId, Details(body).GetProperty("pcId").GetGuid());

        using var login = await agent.PostAsync("/api/v1/auth/login", new { kind = "password", username = second.Username, password = Players.Password, pcId = agent.PcId, hwid = agent.Hwid });
        await Contract.ReadErrorAsync(login, 403, "forbidden", "pcOccupied");
    }

    [Fact]
    public async Task Blacklisted_player_is_denied()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        await Players.ProfileAsync(Server, player, blacklisted: true);
        var (status, body) = await Players.StartAsync(agent, player);
        AssertRule("blacklisted", status, body);
    }

    [Fact]
    public async Task Minor_in_the_curfew_is_denied()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        await Players.ProfileAsync(Server, player, birthYear: Server.Clock.GetUtcNow().Year - 10);
        // Curfew from 00:00 to 06:00 always contains "now" in this rule's model ([curfew, 24:00) ∪ [00:00, 06:00)).
        await Players.ExecuteAsync(Server, """UPDATE clubs SET settings = settings || '{"limits":{"minorAge":18,"minorCurfew":"00:00"}}'::jsonb""");
        try
        {
            var (status, body) = await Players.StartAsync(agent, player);
            AssertRule("minorCurfew", status, body);
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    [Fact]
    public async Task Tariff_of_another_zone_is_denied()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (status, body) = await Players.StartAsync(agent, player, tariff: Players.Vip);
        AssertRule("tariffZone", status, body);
    }

    [Fact]
    public async Task Tariff_outside_its_time_window_is_denied()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var local = ClubTime.Local(Server.Clock.GetUtcNow(), "Asia/Tashkent");
        var window = JsonSerializer.Serialize(new[]
        {
            new { days = new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" }, from = local.AddHours(2).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), to = local.AddHours(3).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) },
        });
        var tariff = Guid.NewGuid();
        await Players.ExecuteAsync(Server,
            "INSERT INTO tariffs (id, club_id, name, price_per_hour, min_minutes, time_windows) SELECT @tariff, id, 'Later', 100000, 1, @window::jsonb FROM clubs",
            new { tariff, window });
        var (status, body) = await Players.StartAsync(agent, player, tariff: tariff);
        AssertRule("tariffTime", status, body);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData(10, "min")]
    [InlineData(721, "max")]
    public async Task Minutes_of_an_hourly_tariff_are_required_and_within_its_range(int? minutes, string reason)
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (status, body) = await Players.StartAsync(agent, player, minutes: minutes);
        AssertError(400, "validation", status, body);
        Assert.Equal(("minutes", reason), (Details(body).GetProperty("field").GetString(), Details(body).GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task Guest_may_not_play_postpaid()
    {
        var agent = await TestAgent.CreateAsync(Server);
        using var login = await agent.PostAsync("/api/v1/auth/guest", new { pcId = agent.PcId, hwid = agent.Hwid });
        var guest = await Players.ReadAsync(login, 200);
        agent.UserToken = guest.GetProperty("accessToken").GetString();
        var (status, body) = await Players.StartAsync(agent, new TestPlayer(guest.GetProperty("user").GetProperty("id").GetGuid(), ""), prepaid: false);
        AssertRule("postpaidNotAllowed", status, body);
    }

    [Fact]
    public async Task Prepaid_beyond_the_balance_is_402_with_required_and_available()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 500_000);
        var (status, body) = await Players.StartAsync(agent, player);
        AssertError(402, "insufficientFunds", status, body);
        Assert.Equal(1_200_000, Details(body).GetProperty("required").GetProperty("amount").GetInt64());
        Assert.Equal(500_000, Details(body).GetProperty("available").GetProperty("amount").GetInt64());
        Assert.Equal(500_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM sessions WHERE user_id = @Id", new { player.Id }));
    }

    [Fact]
    public async Task Online_create_needs_the_players_own_token_on_this_pc()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var other = await Players.CreateAsync(Server);
        var (status, body) = await Players.StartAsync(agent, other);
        AssertError(403, "forbidden", status, body);
        Assert.Equal("notOwner", Details(body).GetProperty("reason").GetString());

        using (var response = await agent.PostAsync("/api/v1/sessions", new { pcId = Guid.NewGuid(), userId = player.Id, tariffId = Players.Standard, minutes = 60, prepaid = true }, Guid.NewGuid()))
        {
            await Contract.ReadErrorAsync(response, 403, "forbidden", "pcMismatch");
        }

        agent.UserToken = null;
        (status, body) = await Players.StartAsync(agent, player);
        AssertError(401, "unauthorized", status, body);
        Assert.Equal("userToken", Details(body).GetProperty("reason").GetString());

        // The key is mandatory.
        agent.UserToken = (await agent.LoginAsync(player)).GetProperty("accessToken").GetString();
        using var keyless = await agent.PostAsync("/api/v1/sessions", new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60, prepaid = true });
        var error = await Contract.ReadErrorAsync(keyless, 400, "validation");
        Assert.Equal("Idempotency-Key", error.GetProperty("error").GetProperty("details").GetProperty("field").GetString());
    }

    internal static JsonElement Details(JsonElement body) => body.GetProperty("error").GetProperty("details");

    internal static void AssertError(int expected, string code, int status, JsonElement body)
    {
        Assert.True(expected == status, $"expected {expected}, got {status}: {body}");
        Contract.AssertError(body, code);
    }

    private static void AssertRule(string rule, int status, JsonElement body)
    {
        AssertError(403, "policyDenied", status, body);
        Assert.Equal(rule, Details(body).GetProperty("rule").GetString());
    }
}
