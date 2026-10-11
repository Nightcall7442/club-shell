using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClubShell.Server.Infrastructure;
using static ClubShell.Server.Tests.PurchaseRulesTests;

namespace ClubShell.Server.Tests;

/// <summary>
/// Player sign-in (DESIGN §3.4): tokens and the <c>offlineHash</c> the agent verifies with its own algorithm, a failure
/// counted once per <c>X-Trace-Id</c> and the lockout, one player per PC, the 403/409 refusals, guests, logout, QR.
/// </summary>
public sealed class PlayerAuthTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task Password_login_returns_the_player_and_an_offline_hash_the_agent_verifies()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 4_500_000);
        var auth = await agent.LoginAsync(player with { Username = player.Username.ToUpperInvariant() });
        var user = auth.GetProperty("user");
        Assert.Equal((player.Id, "member", 4_500_000L), (user.GetProperty("id").GetGuid(), user.GetProperty("role").GetString(), user.GetProperty("balance").GetProperty("amount").GetInt64()));
        Assert.Equal(Server.Clock.GetUtcNow().AddHours(12), auth.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.False(auth.TryGetProperty("session", out _));

        var offlineHash = auth.GetProperty("offlineHash").GetString()!;
        Assert.True(AgentVerifyPassword(offlineHash, Players.Password));
        Assert.False(AgentVerifyPassword(offlineHash, "wrong"));
        var stored = await Players.ScalarAsync<string>(Server, "SELECT password_hash FROM users WHERE id = @Id", new { player.Id });
        Assert.NotEqual(stored, offlineHash); // derived anew, with a fresh salt: the server's own hash never leaves it
    }

    /// <summary>The agent's check is <c>OfflineSessionStore.VerifyPassword</c>; this guards that the copy below still matches it.</summary>
    [Fact]
    public void The_copied_agent_verifier_matches_the_agent_source()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ClubShell.sln")))
        {
            root = root.Parent;
        }

        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "ClubShell.Agent", "Session", "OfflineSessionStore.cs"));
        Assert.Contains("Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length)", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(parts[0], \"pbkdf2\", StringComparison.OrdinalIgnoreCase)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Card_login_ignores_case_and_gives_no_offline_hash()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var card = "CARD-" + Guid.NewGuid().ToString("N")[..8];
        var player = await Players.CreateAsync(Server, card: card);
        using var response = await agent.PostAsync("/api/v1/auth/login", new { kind = "card", cardId = card.ToLowerInvariant(), pcId = agent.PcId, hwid = agent.Hwid });
        var auth = await Players.ReadAsync(response, 200);
        Assert.Equal(player.Id, auth.GetProperty("user").GetProperty("id").GetGuid());
        Assert.False(auth.TryGetProperty("offlineHash", out _));

        using var unknown = await agent.PostAsync("/api/v1/auth/login", new { kind = "card", cardId = "no-such-card", pcId = agent.PcId, hwid = agent.Hwid });
        await Contract.ReadErrorAsync(unknown, 401, "unauthorized", "badCredentials");
    }

    /// <summary>
    /// D-75: card numbers are printed and often sequential, and the kiosk takes typed ones, so wrong cards are counted per
    /// PC like a password per name; 5 in 15 min refuse even a bound card there, while other PCs and passwords still work.
    /// </summary>
    [Fact]
    public async Task Wrong_cards_are_counted_per_pc_and_five_lock_card_login_on_that_pc_for_15_minutes()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var card = "CARD-" + Guid.NewGuid().ToString("N")[..8];
        var player = await Players.CreateAsync(Server, card: card);
        var trace = Guid.NewGuid();
        Assert.Equal(4, await CardFailAsync(agent, "0012345679", trace));
        Assert.Equal(4, await CardFailAsync(agent, "0012345679", trace)); // the agent's retry after a refresh
        for (var left = 3; left >= 0; left--)
        {
            Assert.Equal(left, await CardFailAsync(agent, $"001234568{left}", Guid.NewGuid()));
        }

        Assert.Equal(0, await CardFailAsync(agent, card, Guid.NewGuid())); // locked: even a bound card

        var other = await TestAgent.CreateAsync(Server);
        Assert.Equal(4, await CardFailAsync(other, "0012345679", Guid.NewGuid()));
        await agent.LoginAsync(player);

        Server.Clock.Advance(PlayerAuthWindow);
        using var response = await agent.PostAsync("/api/v1/auth/login", new { kind = "card", cardId = card, pcId = agent.PcId, hwid = agent.Hwid });
        Assert.Equal(player.Id, (await Players.ReadAsync(response, 200)).GetProperty("user").GetProperty("id").GetGuid());
    }

    /// <summary>A bound card does not clear the PC's count: holding a card of one's own must not buy four more guesses.</summary>
    [Fact]
    public async Task A_bound_card_does_not_reset_the_count_of_the_pc()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var card = "CARD-" + Guid.NewGuid().ToString("N")[..8];
        await Players.CreateAsync(Server, card: card);
        for (var left = 4; left >= 1; left--)
        {
            Assert.Equal(left, await CardFailAsync(agent, $"guess-{left}", Guid.NewGuid()));
        }

        using (var response = await agent.PostAsync("/api/v1/auth/login", new { kind = "card", cardId = card, pcId = agent.PcId, hwid = agent.Hwid }))
        {
            await Players.ReadAsync(response, 200);
        }

        Assert.Equal(0, await CardFailAsync(agent, "guess-0", Guid.NewGuid()));
    }

    [Fact]
    public async Task A_failure_counts_once_per_trace_id_and_five_lock_the_account_for_15_minutes()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var trace = Guid.NewGuid();
        Assert.Equal(4, await FailAsync(agent, player, "wrong", trace));
        Assert.Equal(4, await FailAsync(agent, player, "wrong", trace)); // the agent's retry after a refresh
        for (var left = 3; left >= 0; left--)
        {
            Assert.Equal(left, await FailAsync(agent, player, "wrong", Guid.NewGuid()));
        }

        Assert.Equal(0, await FailAsync(agent, player, Players.Password, Guid.NewGuid())); // locked: even the right password
        Server.Clock.Advance(PlayerAuthWindow);
        await agent.LoginAsync(player);
    }

    /// <summary>Only the agent's single repeat of a trace is free: guessing with one fixed X-Trace-Id locks the account like any other guessing.</summary>
    [Fact]
    public async Task Repeats_of_one_trace_id_beyond_the_first_count()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var trace = Guid.NewGuid();
        var left = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            left.Add(await FailAsync(agent, player, "wrong" + i, trace));
        }

        Assert.Equal([4, 4, 3, 2, 1, 0], left);
        Assert.Equal(0, await FailAsync(agent, player, Players.Password, trace));
    }

    /// <summary>Attempts on one name are serialized: 10 parallel guesses cannot all read "0 failures" and be verified at once.</summary>
    [Fact]
    public async Task Parallel_guesses_are_counted_one_after_another()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var left = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => FailAsync(agent, player, "wrong" + i, Guid.NewGuid())));
        Assert.Equal([0, 0, 0, 0, 0, 0, 1, 2, 3, 4], left.Order());

        // Only 5 passwords were checked; the other 5 were refused by the count before any check.
        Assert.Equal(5, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM login_failures WHERE username = lower(@Username)", new { player.Username }));
    }

    /// <summary>An unknown name answers like a known one (4, 3, …), so attemptsLeft does not tell which usernames exist.</summary>
    [Fact]
    public async Task An_unknown_username_counts_like_a_known_one()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var nobody = new TestPlayer(Guid.NewGuid(), "nosuch-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(4, await FailAsync(agent, nobody, "x", Guid.NewGuid()));
        Assert.Equal(3, await FailAsync(agent, nobody, "x", Guid.NewGuid()));
    }

    [Fact]
    public async Task A_new_login_on_the_pc_displaces_the_previous_player()
    {
        var (agent, first) = await Players.SignedInAsync(Server);
        var firstToken = agent.UserToken;
        var second = await Players.CreateAsync(Server);
        await agent.LoginAsync(second);

        agent.UserToken = firstToken;
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{first.Id}");
        var body = await Contract.ReadErrorAsync(response, 401, "unauthorized", "userToken");
        Assert.Equal("invalid", Details(body).GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Banned_blacklisted_minors_in_curfew_and_players_busy_elsewhere_are_refused()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var banned = await Players.CreateAsync(Server);
        await Players.ExecuteAsync(Server, "UPDATE users SET banned = true WHERE id = @Id", new { banned.Id });
        Assert.Equal(("forbidden", "banned"), await RefusedAsync(agent, banned, 403));

        var blacklisted = await Players.CreateAsync(Server);
        await Players.ProfileAsync(Server, blacklisted, blacklisted: true);
        Assert.Equal(("forbidden", "banned"), await RefusedAsync(agent, blacklisted, 403));

        var minor = await Players.CreateAsync(Server);
        await Players.ProfileAsync(Server, minor, birthYear: Server.Clock.GetUtcNow().Year - 12);
        await Players.ExecuteAsync(Server, """UPDATE clubs SET settings = settings || '{"limits":{"minorAge":18,"minorCurfew":"00:00"}}'::jsonb""");
        try
        {
            Assert.Equal(("forbidden", "ageRestricted"), await RefusedAsync(agent, minor, 403));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }

        var (elsewhere, busy) = await Players.SignedInAsync(Server);
        await Players.StartAsync(elsewhere, busy);
        using var response = await agent.PostAsync("/api/v1/auth/login", new { kind = "password", username = busy.Username, password = Players.Password, pcId = agent.PcId, hwid = agent.Hwid });
        var conflict = await Contract.ReadErrorAsync(response, 409, "conflict", "activeSessionElsewhere");
        Assert.Equal(elsewhere.PcId, Details(conflict).GetProperty("pcId").GetGuid());
        Assert.StartsWith("PC-", Details(conflict).GetProperty("pcName").GetString(), StringComparison.Ordinal);

        // On its own PC the login returns the open session.
        var again = await elsewhere.LoginAsync(busy);
        Assert.Equal("active", again.GetProperty("session").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Kinds_other_than_password_and_card_and_a_foreign_pc_are_refused()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        foreach (var kind in new[] { "qr", "guest" })
        {
            using var response = await agent.PostAsync("/api/v1/auth/login", new { kind, pcId = agent.PcId, hwid = agent.Hwid });
            Assert.Equal("kind", Details(await Contract.ReadErrorAsync(response, 400, "validation")).GetProperty("field").GetString());
        }

        using (var token = await agent.PostAsync("/api/v1/auth/login", new { kind = "token", token = "tok-alisher", pcId = agent.PcId, hwid = agent.Hwid }))
        {
            await Contract.ReadErrorAsync(token, 401, "unauthorized", "badCredentials");
        }

        using var foreign = await agent.PostAsync("/api/v1/auth/login", new { kind = "password", username = player.Username, password = Players.Password, pcId = Guid.NewGuid(), hwid = agent.Hwid });
        await Contract.ReadErrorAsync(foreign, 403, "forbidden", "pcMismatch");
    }

    [Fact]
    public async Task Guest_gets_a_temporary_account_without_an_offline_hash()
    {
        var agent = await TestAgent.CreateAsync(Server);
        using var response = await agent.PostAsync("/api/v1/auth/guest", new { pcId = agent.PcId, hwid = agent.Hwid, displayName = (string?)null, locale = "uz" });
        var auth = await Players.ReadAsync(response, 200);
        var user = auth.GetProperty("user");
        Assert.Equal(("guest", "uz", 0L), (user.GetProperty("role").GetString(), user.GetProperty("locale").GetString(), user.GetProperty("balance").GetProperty("amount").GetInt64()));
        Assert.StartsWith("guest-", user.GetProperty("username").GetString(), StringComparison.Ordinal);
        Assert.False(auth.TryGetProperty("offlineHash", out _));

        // A guest has no profile to edit.
        agent.UserToken = auth.GetProperty("accessToken").GetString();
        using var patch = await agent.SendAsync(HttpMethod.Patch, $"/api/v1/users/{user.GetProperty("id").GetGuid()}", """{"locale":"ru"}""");
        await Contract.ReadErrorAsync(patch, 403, "forbidden", "guest");
    }

    [Fact]
    public async Task Logout_ends_the_open_session_with_its_reason_and_revokes_the_token()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        using (var logout = await agent.PostAsync("/api/v1/auth/logout", new { reason = "admin" }))
        {
            await Players.ReadAsync(logout, 204);
        }

        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(("ended", "admin"), (await Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id }),
            await Players.ScalarAsync<string>(Server, "SELECT end_reason FROM sessions WHERE id = @id", new { id })));
        Assert.Equal(10_000_000, await Players.BalanceAsync(Server, player.Id)); // admin: the unused hour is refunded

        using var after = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}");
        await Contract.ReadErrorAsync(after, 401, "unauthorized", "userToken");
    }

    [Fact]
    public async Task Player_token_expires_after_12_hours()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        Server.Clock.Advance(TimeSpan.FromHours(12));
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}");
        var body = await Contract.ReadErrorAsync(response, 401, "unauthorized", "userToken");
        Assert.Equal("expired", Details(body).GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Qr_login_is_pending_until_it_expires_and_belongs_to_its_pc()
    {
        var agent = await TestAgent.CreateAsync(Server);
        using var start = await agent.PostAsync("/api/v1/auth/qr/start", new { pcId = agent.PcId });
        var qr = await Players.ReadAsync(start, 200);
        var token = qr.GetProperty("qrToken").GetString()!;
        Assert.EndsWith("/q/" + token, qr.GetProperty("qrUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal("pending", await QrStatusAsync(agent, token));

        var other = await TestAgent.CreateAsync(Server);
        using (var foreign = await other.SendAsync(HttpMethod.Get, $"/api/v1/auth/qr/{Uri.EscapeDataString(token)}"))
        {
            await Contract.ReadErrorAsync(foreign, 403, "forbidden", "pcMismatch");
        }

        using (var unknown = await agent.SendAsync(HttpMethod.Get, "/api/v1/auth/qr/nope"))
        {
            await Contract.ReadErrorAsync(unknown, 404, "notFound");
        }

        Server.Clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal("expired", await QrStatusAsync(agent, token));
    }

    /// <summary>A copy of the agent's <c>OfflineSessionStore.VerifyPassword</c> (the agent project is Windows-only).</summary>
    internal static bool AgentVerifyPassword(string encoded, string password)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], "pbkdf2", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1 || iterations > 10_000_000)
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static readonly TimeSpan PlayerAuthWindow = Auth.PlayerAuthEndpoints.FailureWindow;

    private async Task<int> FailAsync(TestAgent agent, TestPlayer player, string password, Guid trace)
    {
        using var request = agent.Request(HttpMethod.Post, "/api/v1/auth/login",
            JsonSerializer.Serialize(new { kind = "password", username = player.Username, password, pcId = agent.PcId, hwid = agent.Hwid }));
        request.Headers.Add(ApiErrorWriter.TraceHeader, trace.ToString());
        using var response = await Server.Http.SendAsync(request);
        var body = await Contract.ReadErrorAsync(response, 401, "unauthorized", "badCredentials");
        return Details(body).GetProperty("attemptsLeft").GetInt32();
    }

    private async Task<int> CardFailAsync(TestAgent agent, string cardId, Guid trace)
    {
        using var request = agent.Request(HttpMethod.Post, "/api/v1/auth/login",
            JsonSerializer.Serialize(new { kind = "card", cardId, pcId = agent.PcId, hwid = agent.Hwid }));
        request.Headers.Add(ApiErrorWriter.TraceHeader, trace.ToString());
        using var response = await Server.Http.SendAsync(request);
        var body = await Contract.ReadErrorAsync(response, 401, "unauthorized", "badCredentials");
        return Details(body).GetProperty("attemptsLeft").GetInt32();
    }

    private static async Task<(string, string)> RefusedAsync(TestAgent agent, TestPlayer player, int status)
    {
        using var response = await agent.PostAsync("/api/v1/auth/login", new { kind = "password", username = player.Username, password = Players.Password, pcId = agent.PcId, hwid = agent.Hwid });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status, (int)response.StatusCode);
        return (body.GetProperty("error").GetProperty("code").GetString()!, Details(body).GetProperty("reason").GetString()!);
    }

    private static async Task<string> QrStatusAsync(TestAgent agent, string token)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/auth/qr/{Uri.EscapeDataString(token)}");
        return (await Players.ReadAsync(response, 200)).GetProperty("status").GetString()!;
    }
}

/// <summary><c>Club:GuestLogin=false</c> → <c>403 guestDisabled</c>.</summary>
public sealed class GuestDisabledTests(GuestDisabledTests.Fixture server) : IClassFixture<GuestDisabledTests.Fixture>
{
    public sealed class Fixture : ServerFixture
    {
        public Fixture() => Settings["Club:GuestLogin"] = "false";
    }

    [Fact]
    public async Task Guest_login_is_refused_when_the_club_disables_it()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var response = await agent.PostAsync("/api/v1/auth/guest", new { pcId = agent.PcId, hwid = agent.Hwid });
        await Contract.ReadErrorAsync(response, 403, "forbidden", "guestDisabled");
    }
}
