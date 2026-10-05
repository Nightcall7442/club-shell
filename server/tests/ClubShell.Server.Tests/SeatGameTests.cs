using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The running game on the map (D-71): <c>overview.seats[].game</c> is the newest game the PC's agent reports in its last
/// heartbeat for the open session (the server's id or the session's <c>client_session_id</c>), from this club's catalog,
/// with an empty cover as null; nothing for another session, no session, an empty list, a foreign game or an offline PC.
/// </summary>
public sealed class SeatGameTests(CatalogServerFixture server) : IClassFixture<CatalogServerFixture>
{
    private static readonly Guid Game1 = CatalogServerFixture.GameId(1);
    private static readonly Guid Game2 = CatalogServerFixture.GameId(2);
    private static readonly Guid Game3 = CatalogServerFixture.GameId(3);

    [Fact]
    public async Task A_busy_seat_shows_the_newest_game_its_PC_reports_for_the_session()
    {
        var token = await LoginAsync(server, OwnerPin);
        await OpenShiftAsync(server, token);
        var (agent, session) = await SeatedAsync(token);
        var now = server.Clock.GetUtcNow();

        await BeatAsync(agent, session, (Game1, now.AddMinutes(-10)));
        var game = await GameAsync(token, agent);
        Assert.Equal((Game1, "Game 0001", "https://cdn.example/cover.jpg", JsonValueKind.Null),
            (game.GetProperty("id").GetGuid(), game.GetProperty("title").GetString(), game.GetProperty("coverUrl").GetString(), game.GetProperty("heroUrl").ValueKind));

        // Two at once: the one started last is what the player looks at.
        await BeatAsync(agent, session, (Game2, now.AddMinutes(-1)), (Game1, now.AddMinutes(-10)));
        Assert.Equal(Game2, (await GameAsync(token, agent)).GetProperty("id").GetGuid());

        // The game closed: the next heartbeat lists none.
        await BeatAsync(agent, session);
        Assert.Equal(JsonValueKind.Null, (await GameAsync(token, agent)).ValueKind);

        await EndAsync(token, agent);
    }

    [Fact]
    public async Task Art_is_null_when_empty_and_the_hero_passes_through()
    {
        var token = await LoginAsync(server, OwnerPin);
        await OpenShiftAsync(server, token);
        var (agent, session) = await SeatedAsync(token);
        var game = CatalogServerFixture.GameId(4);
        await Players.ExecuteAsync(server,
            """UPDATE games SET data = data || '{"coverUrl":"","heroUrl":"https://cdn.example/hero.jpg"}'::jsonb WHERE id = @game""", new { game });

        await BeatAsync(agent, session, (game, server.Clock.GetUtcNow()));
        var seen = await GameAsync(token, agent);
        Assert.Equal((JsonValueKind.Null, "https://cdn.example/hero.jpg"), (seen.GetProperty("coverUrl").ValueKind, seen.GetProperty("heroUrl").GetString()));

        await EndAsync(token, agent);
    }

    [Fact]
    public async Task Only_the_open_session_of_this_club_counts()
    {
        var token = await LoginAsync(server, OwnerPin);
        await OpenShiftAsync(server, token);
        var (agent, session) = await SeatedAsync(token);
        var now = server.Clock.GetUtcNow();

        // A session the agent opened offline reports its own id (`client_session_id`).
        var local = Guid.NewGuid();
        await Players.ExecuteAsync(server, "UPDATE sessions SET client_session_id = @local WHERE id = @session", new { local, session });
        await BeatAsync(agent, local, (Game3, now));
        Assert.Equal(Game3, (await GameAsync(token, agent)).GetProperty("id").GetGuid());

        // A game of another club's catalog, or of none, is never shown. (The other club goes again at once: with two clubs
        // a PIN sign-in needs the club code.)
        var foreign = Guid.NewGuid();
        var other = await Players.ScalarAsync<Guid>(server,
            """
            WITH club AS (INSERT INTO clubs (id, network_id, name) SELECT gen_random_uuid(), network_id, 'Other' FROM clubs LIMIT 1 RETURNING id),
                 game AS (INSERT INTO games (id, club_id, title, data, updated_at)
                          SELECT @foreign, id, 'Foreign', '{"coverUrl":""}'::jsonb, now() FROM club RETURNING club_id)
            SELECT club_id FROM game
            """,
            new { foreign });
        try
        {
            await BeatAsync(agent, session, (foreign, now));
            Assert.Equal(JsonValueKind.Null, (await GameAsync(token, agent)).ValueKind);
        }
        finally
        {
            await Players.ExecuteAsync(server, "DELETE FROM games WHERE id = @foreign; DELETE FROM clubs WHERE id = @other", new { foreign, other });
        }

        await BeatAsync(agent, session, (Guid.NewGuid(), now));
        Assert.Equal(JsonValueKind.Null, (await GameAsync(token, agent)).ValueKind);

        // The previous player's session id: their game never shows on the next session.
        await EndAsync(token, agent);
        await BeatAsync(agent, null, (Game1, now));
        var empty = await SeatAsync(token, agent);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (empty.GetProperty("session").ValueKind, empty.GetProperty("game").ValueKind));
        var player = await Players.CreateAsync(server);
        var next = (await ExpectAsync(server, 201, HttpMethod.Post, "/sessions", token,
            new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 })).GetProperty("session").GetProperty("id").GetGuid();
        await BeatAsync(agent, session, (Game1, now));
        Assert.Equal(JsonValueKind.Null, (await GameAsync(token, agent)).ValueKind);
        await BeatAsync(agent, next, (Game1, now));
        Assert.Equal(Game1, (await GameAsync(token, agent)).GetProperty("id").GetGuid());

        await EndAsync(token, agent);
    }

    [Fact]
    public async Task An_offline_PC_shows_no_game()
    {
        var token = await LoginAsync(server, OwnerPin);
        await OpenShiftAsync(server, token);
        var (agent, session) = await SeatedAsync(token);
        await BeatAsync(agent, session, (Game2, server.Clock.GetUtcNow()));
        Assert.Equal(Game2, (await GameAsync(token, agent)).GetProperty("id").GetGuid());

        server.Clock.Advance(TimeSpan.FromSeconds(91));
        var seat = await SeatAsync(token, agent);
        Assert.Equal(("offline", JsonValueKind.Null), (seat.GetProperty("pc").GetProperty("status").GetString(), seat.GetProperty("game").ValueKind));

        await EndAsync(token, agent);
    }

    /// <summary>An online PC with a member seated by the desk for an hour; the session id.</summary>
    private async Task<(TestAgent Agent, Guid Session)> SeatedAsync(string token)
    {
        var agent = await TestAgent.CreateAsync(server);
        await BeatAsync(agent, null);
        var player = await Players.CreateAsync(server);
        var opened = await ExpectAsync(server, 201, HttpMethod.Post, "/sessions", token,
            new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 });
        return (agent, opened.GetProperty("session").GetProperty("id").GetGuid());
    }

    private static async Task BeatAsync(TestAgent agent, Guid? session, params (Guid GameId, DateTimeOffset StartedAt)[] games) =>
        await Players.ReadAsync(await agent.HeartbeatAsync(currentSessionId: session, runningGames: games), 200);

    private async Task<JsonElement> SeatAsync(string token, TestAgent agent) =>
        (await ExpectAsync(server, 200, HttpMethod.Get, "/overview", token)).GetProperty("seats").EnumerateArray()
        .Single(s => s.GetProperty("pc").GetProperty("id").GetGuid() == agent.PcId);

    private async Task<JsonElement> GameAsync(string token, TestAgent agent)
    {
        var seat = await SeatAsync(token, agent);
        Assert.Equal("busy", seat.GetProperty("pc").GetProperty("status").GetString());
        return seat.GetProperty("game");
    }

    private Task EndAsync(string token, TestAgent agent) => ExpectAsync(server, 200, HttpMethod.Post, "/sessions/end", token, new { pcId = agent.PcId });
}
