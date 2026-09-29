using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Server.Wallet;

namespace ClubShell.Server.Tests;

/// <summary>
/// Slice S2 through the real agent <see cref="ClubShell.Core.Http.ServerClient"/> (DESIGN §10.b): login (password, card,
/// guest) → balance → tariffs → createSession → current (<c>secondsLeft</c>) → pause/resume → extend → end (a repeated
/// end is the agent's success path); offline replay (<c>startedAt</c>, <c>clientSessionId</c>) and its events batch sent
/// twice with the same key; a wrong password counted once although the client repeats it after a refresh. Then the
/// races: two concurrent creates → one 201 and one 409; one key twice at once → one effect.
/// </summary>
public sealed class AgentHarnessS2Tests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task S2_scenario_through_the_real_server_client()
    {
        await using var agent = await AgentHarness.CreateAsync(Server);
        var client = agent.Client;
        var pcId = (await client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None)).PcId;
        var card = "CARD-" + Guid.NewGuid().ToString("N")[..8];
        var player = await Players.CreateAsync(Server, balance: 5_000_000, card: card);

        var auth = await client.LoginAsync(Password(player, Players.Password, pcId, agent.Hwid), CancellationToken.None);
        Assert.Equal(player.Id, auth.User.Id);
        Assert.Equal(player.Id, agent.Tokens.User!.UserId);
        Assert.True(PlayerAuthTests.AgentVerifyPassword(auth.OfflineHash!, Players.Password));

        Assert.Equal(5_000_000, (await client.GetBalanceAsync(player.Id, CancellationToken.None)).Amount.Amount);
        var tariffs = await client.GetTariffsAsync("", null, CancellationToken.None);
        Assert.Contains(tariffs.Value!.Items, t => t.Id == Players.Standard);

        var session = await client.CreateSessionAsync(new SessionCreateRequest(pcId, player.Id, Players.Standard, 60, true), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal((SessionState.Active, 3600), (session.State, session.SecondsLeft));

        Advance(agent, TimeSpan.FromMinutes(10));
        var current = await client.GetCurrentSessionAsync(pcId, CancellationToken.None);
        Assert.Equal((session.Id, 3000), (current!.Id, current.SecondsLeft));

        Assert.Equal(SessionState.Paused, (await client.PauseSessionAsync(session.Id, CancellationToken.None)).State);
        Advance(agent, TimeSpan.FromMinutes(5));
        var resumed = await client.ResumeSessionAsync(session.Id, CancellationToken.None);
        Assert.Equal((SessionState.Active, 3000), (resumed.State, resumed.SecondsLeft));

        var extended = await client.ExtendSessionAsync(session.Id, new SessionExtendRequest(30), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(4800, extended.SecondsLeft);

        var ended = await client.EndSessionAsync(session.Id, new SessionEndReport(SessionEndReason.User, 600, agent.Clock.GetUtcNow()), CancellationToken.None);
        Assert.Equal((SessionState.Ended, 600), (ended.Session.State, ended.Session.SecondsUsed));
        var again = await client.EndSessionAsync(session.Id, new SessionEndReport(SessionEndReason.User, 600), CancellationToken.None);
        Assert.Equal((SessionState.Ended, 0L), (again.Session.State, again.Charged.Amount)); // 409 + details.session = done
        Assert.Equal(5_000_000 - 1_800_000, await Players.BalanceAsync(Server, player.Id));

        var byCard = await client.LoginAsync(new AuthRequest(AuthKind.Card, null, null, null, card.ToLowerInvariant(), null, pcId, agent.Hwid), CancellationToken.None);
        Assert.Equal(player.Id, byCard.User.Id);
        var guest = await client.GuestLoginAsync(new GuestAuthRequest(pcId, agent.Hwid, null, Locale.Ru), CancellationToken.None);
        Assert.Equal(UserRole.Guest, guest.User.Role);
    }

    [Fact]
    public async Task Offline_session_is_replayed_and_its_events_batch_is_applied_once()
    {
        await using var agent = await AgentHarness.CreateAsync(Server);
        var client = agent.Client;
        var pcId = (await client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None)).PcId;
        var player = await Players.CreateAsync(Server, balance: 0);

        // Offline login gives no player token: the replay goes with the agent token alone (D-11).
        await agent.Tokens.SetUserAsync(null, CancellationToken.None);
        var clientId = Guid.NewGuid();
        var startedAt = agent.Clock.GetUtcNow().AddMinutes(-30);
        var replayed = await client.CreateSessionAsync(
            new SessionCreateRequest(pcId, player.Id, Players.Standard, 60, true, startedAt, clientId), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal((clientId, 1800), (replayed.Id, replayed.SecondsUsed));

        var batch = new SessionEventsBatch([
            SessionEvent.Of(clientId, SessionEventType.Started, startedAt),
            SessionEvent.Of(clientId, SessionEventType.Paused, startedAt.AddMinutes(10)),
            SessionEvent.Of(clientId, SessionEventType.Resumed, startedAt.AddMinutes(20)),
            SessionEvent.Extended(clientId, startedAt.AddMinutes(25), 30, Money(600_000)),
        ]);
        var key = Guid.NewGuid();
        await client.PostSessionEventsAsync(clientId, batch, key, CancellationToken.None);
        await client.PostSessionEventsAsync(clientId, batch, key, CancellationToken.None);

        var current = await client.GetCurrentSessionAsync(pcId, CancellationToken.None);
        Assert.Equal((1200, 5400 - 1200), (current!.SecondsUsed, current.SecondsLeft));
        Assert.Equal(-1_800_000, await Players.BalanceAsync(Server, player.Id)); // hour + 30 min, overdraft
        Assert.Equal(4, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM session_events WHERE session_id = @clientId", new { clientId }));
    }

    [Fact]
    public async Task Wrong_password_is_counted_once_although_the_client_repeats_it_after_a_refresh()
    {
        await using var agent = await AgentHarness.CreateAsync(Server);
        var client = agent.Client;
        var pcId = (await client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None)).PcId;
        var player = await Players.CreateAsync(Server);
        var refreshToken = agent.Tokens.Agent!.RefreshToken;

        var error = await Assert.ThrowsAsync<ServerApiException>(() => client.LoginAsync(Password(player, "wrong", pcId, agent.Hwid), CancellationToken.None));
        Assert.Equal((ErrorCode.Unauthorized, "badCredentials"), (error.Code, error.Reason));
        Assert.Equal(4, error.Error!.Details!.Value.GetProperty("attemptsLeft").GetInt32());
        Assert.NotEqual(refreshToken, agent.Tokens.Agent!.RefreshToken); // the client did refresh and repeat
        Assert.Equal("1/2", await Players.ScalarAsync<string>(Server, "SELECT count(*) || '/' || sum(attempts) FROM login_failures WHERE username = lower(@Username)", new { player.Username })); // one failure, the repeat free
    }

    [Fact]
    public async Task Two_concurrent_creates_give_one_201_and_one_409()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var results = await Task.WhenAll(Players.StartAsync(agent, player), Players.StartAsync(agent, player));
        Assert.Equal(new[] { 201, 409 }, results.Select(r => r.Status).Order());
        Contract.AssertError(results.Single(r => r.Status == 409).Body, "sessionAlreadyActive");

        // One player on two PCs: the open-session index of the player decides.
        var first = await TestAgent.CreateAsync(Server);
        var second = await TestAgent.CreateAsync(Server);
        var both = await Players.CreateAsync(Server);
        await first.LoginAsync(both);
        await second.LoginAsync(both);
        results = await Task.WhenAll(Players.StartAsync(first, both), Players.StartAsync(second, both));
        Assert.Equal(new[] { 201, 409 }, results.Select(r => r.Status).Order());
        Assert.Equal(10_000_000 - 1_200_000, await Players.BalanceAsync(Server, both.Id));
    }

    [Fact]
    public async Task One_key_sent_twice_at_once_has_one_effect()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var key = Guid.NewGuid();
        var results = await Task.WhenAll(Players.StartAsync(agent, player, key: key), Players.StartAsync(agent, player, key: key));
        Assert.All(results, r => Assert.Equal(201, r.Status));
        Assert.Equal(results[0].Body.GetProperty("id").GetGuid(), results[1].Body.GetProperty("id").GetGuid());
        Assert.Equal(10_000_000 - 1_200_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id AND type = 'charge'", new { player.Id }));
    }

    private void Advance(AgentHarness agent, TimeSpan by)
    {
        Server.Clock.Advance(by);
        agent.Clock.Advance(by);
    }

    private static AuthRequest Password(TestPlayer player, string password, Guid pcId, string hwid) =>
        new(AuthKind.Password, player.Username, password, null, null, null, pcId, hwid);

    private static Contracts.Wallet.Money Money(long amount) => Contracts.Wallet.Money.Uzs(amount);
}

/// <summary>
/// Coverage of slice S2 (DESIGN §10.a, §11): each of its 18 operations and <c>getBalance</c> got a contract-valid success
/// response through the real agent client in this fixture.
/// </summary>
public sealed class CoverageS2Tests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task S2_operations_all_have_a_contract_valid_success_response()
    {
        await using var agent = await AgentHarness.CreateAsync(Server);
        var client = agent.Client;
        var ct = CancellationToken.None;
        var pcId = (await client.RegisterAsync(agent.RegisterRequest(), ct)).PcId;
        var player = await Players.CreateAsync(Server);

        var qr = await client.StartQrLoginAsync(new QrStartRequest(pcId), ct);
        await client.GetQrLoginStatusAsync(qr.QrToken, ct);
        await client.GuestLoginAsync(new GuestAuthRequest(pcId, agent.Hwid), ct);
        await client.LoginAsync(new AuthRequest(AuthKind.Password, player.Username, Players.Password, null, null, null, pcId, agent.Hwid), ct);
        await client.GetUserAsync(player.Id, ct);
        await client.UpdateUserAsync(player.Id, new ProfileUpdateRequest(Locale: Locale.Uz), ct);
        await client.GetUserStatsAsync(player.Id, ct);
        await client.GetUserAchievementsAsync(player.Id, ct);
        await client.GetUserLoyaltyAsync(player.Id, ct);
        await client.GetBalanceAsync(player.Id, ct);
        await client.GetTariffsAsync(null, null, ct);
        var session = await client.CreateSessionAsync(new SessionCreateRequest(pcId, player.Id, Players.Standard, 60, true), Guid.NewGuid(), ct);
        await client.GetCurrentSessionAsync(pcId, ct);
        await client.PauseSessionAsync(session.Id, ct);
        await client.ResumeSessionAsync(session.Id, ct);
        await client.ExtendSessionAsync(session.Id, new SessionExtendRequest(30), Guid.NewGuid(), ct);
        await client.PostSessionEventsAsync(session.Id, new SessionEventsBatch([SessionEvent.Warning(session.Id, agent.Clock.GetUtcNow(), 5)]), Guid.NewGuid(), ct);
        await client.EndSessionAsync(session.Id, new SessionEndReport(SessionEndReason.User, 1), ct);
        await client.LogoutAsync(new LogoutRequest(SessionEndReason.User), ct);

        var success = new Dictionary<string, int>
        {
            ["login"] = 200, ["startQrLogin"] = 200, ["getQrLoginStatus"] = 200, ["guestLogin"] = 200, ["logout"] = 204,
            ["getUser"] = 200, ["updateUser"] = 200, ["getUserStats"] = 200, ["getUserAchievements"] = 200, ["getUserLoyalty"] = 200,
            ["getCurrentSession"] = 200, ["createSession"] = 201, ["pauseSession"] = 200, ["resumeSession"] = 200, ["endSession"] = 200,
            ["extendSession"] = 200, ["postSessionEvents"] = 204, ["getTariffs"] = 200, ["getBalance"] = 200,
        };
        Assert.Equal(
            Auth.PlayerAuthEndpoints.Operations.Concat(Users.UserEndpoints.Operations).Concat(Sessions.SessionEndpoints.Operations).Concat(WalletEndpoints.Operations).Order(),
            success.Keys.Order());
        Assert.All(success, op => Assert.True(Server.Covered.ContainsKey($"{op.Key} {op.Value}"), $"{op.Key} {op.Value} not covered"));
    }
}
