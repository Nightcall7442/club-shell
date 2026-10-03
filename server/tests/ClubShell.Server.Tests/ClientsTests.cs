using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Clients at the counter (slice S5): login name and card unique case-insensitively, the card bound and unbound, the
/// profile patched key by key, <c>blacklisted</c> for the owner only, a new password revoking the player's tokens, the
/// wallet history newest first; every journaled change in <c>audit_entries</c>.
/// </summary>
public sealed class ClientsTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Login_and_card_are_unique_case_insensitively_and_the_card_signs_in()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var username = $"Case-{tag}";
        var card = $"CARD-{tag}";
        var client = (await ReplayedAsync(Server, 200, HttpMethod.Post, "/clients", cashier,
            new { username, displayName = "Регистрация", password = Players.Password, cardId = $"  {card}  ", phone = "+998901112233", groupId = "student", telegram = "@nope" }))
            .GetProperty("client");
        Assert.Equal((username.ToLowerInvariant(), card, "member", 0L, "student", "+998901112233"), (client.GetProperty("username").GetString(), client.GetProperty("cardId").GetString(),
            client.GetProperty("role").GetString(), client.GetProperty("balance").GetProperty("amount").GetInt64(), client.GetProperty("groupId").GetString(),
            client.GetProperty("phone").GetString()));
        Assert.False(client.TryGetProperty("telegram", out _));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM users WHERE username = @u", new { u = username.ToLowerInvariant() }));

        foreach (var (body, field, reason) in new (object, string, string)[]
                 {
                     (new { username = username.ToUpperInvariant(), displayName = "X" }, "username", "taken"),
                     (new { username = $"other-{tag}", displayName = "X", cardId = card.ToLowerInvariant() }, "cardId", "taken"),
                     (new { username = $"other-{tag}", displayName = "X", password = "abc" }, "password", "min"),
                     (new { username = $"other-{tag}", displayName = "" }, "displayName", "required"),
                     (new { username = $"other-{tag}", displayName = "X", birthYear = 1800 }, "birthYear", "min"),
                 })
        {
            Assert.Equal((field, reason), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, "/clients", cashier, body)));
        }

        // The registered client signs in on a PC with the password and with the card in any case.
        var agent = await TestAgent.CreateAsync(Server);
        await agent.LoginAsync(new TestPlayer(client.GetProperty("id").GetGuid(), username));
        using (var byCard = await agent.PostAsync("/api/v1/auth/login", new { kind = "card", cardId = card.ToLowerInvariant(), pcId = agent.PcId, hwid = agent.Hwid }))
        {
            await Players.ReadAsync(byCard, 200);
        }

        // Without a password nobody knows one; a short name is fine (1–32).
        var other = (await ExpectAsync(Server, 200, HttpMethod.Post, "/clients", cashier, new { username = tag[..2], displayName = "Без пароля" })).GetProperty("client");
        Assert.StartsWith("pbkdf2$", await Players.ScalarAsync<string>(Server, "SELECT password_hash FROM users WHERE id = @id", new { id = other.GetProperty("id").GetGuid() }));

        var otherId = other.GetProperty("id").GetGuid();
        Assert.Equal(("cardId", "taken"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, $"/clients/{otherId}/card", cashier, new { cardId = card.ToLowerInvariant() })));
        Assert.Equal(("cardId", "required"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, $"/clients/{otherId}/card", cashier, new { })));
        var unbound = (await ExpectAsync(Server, 200, HttpMethod.Post, $"/clients/{client.GetProperty("id").GetGuid()}/card", cashier, new { cardId = (string?)null })).GetProperty("client");
        Assert.Equal(JsonValueKind.Null, unbound.GetProperty("cardId").ValueKind);
        var bound = (await ExpectAsync(Server, 200, HttpMethod.Post, $"/clients/{otherId}/card", cashier, new { cardId = card.ToLowerInvariant() })).GetProperty("client");
        Assert.Equal(card.ToLowerInvariant(), bound.GetProperty("cardId").GetString());
        Assert.Equal(card.ToLowerInvariant(), await Players.ScalarAsync<string>(Server,
            "SELECT meta ->> 'cardId' FROM audit_entries WHERE action = 'clientCard' AND user_id = @otherId", new { otherId }));
        foreach (var unknown in new[] { Guid.NewGuid().ToString(), "nobody" })
        {
            var error = await ExpectAsync(Server, 404, HttpMethod.Post, $"/clients/{unknown}/card", cashier, new { cardId = "X" });
            Assert.Equal("user", error.GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        }

        // Search: name or login case-insensitively, or the phone.
        foreach (var q in new[] { username.ToUpperInvariant(), "регистрац", "901112233" })
        {
            var found = (await ExpectAsync(Server, 200, HttpMethod.Get, $"/clients?q={Uri.EscapeDataString(q)}", cashier)).GetProperty("items");
            Assert.Contains(found.EnumerateArray(), c => c.GetProperty("id").GetGuid() == client.GetProperty("id").GetGuid());
        }

        var all = (await ExpectAsync(Server, 200, HttpMethod.Get, "/clients?q=", cashier)).GetProperty("items");
        Assert.DoesNotContain(all.EnumerateArray(), c => c.GetProperty("role").GetString() is "guest" or "admin");
    }

    [Fact]
    public async Task Blacklist_is_the_owners_and_the_profile_is_patched_key_by_key()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var owner = await LoginAsync(Server, OwnerPin);
        var (agent, player) = await Players.SignedInAsync(Server);
        var path = $"/clients/{player.Id}";

        Contract.AssertError(await ExpectAsync(Server, 403, HttpMethod.Patch, path, cashier, new { blacklisted = "yes", birthYear = 1 }), "forbidden", "ownerOnly");
        var patched = (await ExpectAsync(Server, 200, HttpMethod.Patch, path, cashier,
            new { groupId = "staff", note = "постоянный", phone = "+99890", birthYear = 2001, displayName = "Новое имя", telegram = "@x" })).GetProperty("client");
        Assert.Equal(("staff", "постоянный", "+99890", 2001, "Новое имя"), (patched.GetProperty("groupId").GetString(), patched.GetProperty("note").GetString(),
            patched.GetProperty("phone").GetString(), patched.GetProperty("birthYear").GetInt32(), patched.GetProperty("displayName").GetString()));
        var group = await Players.ScalarAsync<string>(Server,
            "SELECT meta ->> 'discountPct' || ' ' || detail FROM audit_entries WHERE action = 'clientGroup' AND user_id = @Id", new { player.Id });
        Assert.Equal("50 Новое имя → Сотрудник", group);

        // Absent keys stay, null clears.
        patched = (await ExpectAsync(Server, 200, HttpMethod.Patch, path, cashier, new { groupId = (string?)null, note = (string?)null })).GetProperty("client");
        Assert.Equal((JsonValueKind.Null, "", "+99890", 2001), (patched.GetProperty("groupId").ValueKind, patched.GetProperty("note").GetString(),
            patched.GetProperty("phone").GetString(), patched.GetProperty("birthYear").GetInt32()));
        Assert.Equal(2, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'clientGroup' AND user_id = @Id", new { player.Id }));
        Assert.Equal(("birthYear", "max"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, path, cashier, new { birthYear = 2101 })));
        Assert.Equal("user", (await ExpectAsync(Server, 404, HttpMethod.Patch, $"/clients/{Guid.NewGuid()}", cashier, new { note = "x" }))
            .GetProperty("error").GetProperty("details").GetProperty("what").GetString());

        using (var before = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"))
        {
            await Players.ReadAsync(before, 200);
        }

        Assert.True((await ExpectAsync(Server, 200, HttpMethod.Patch, path, owner, new { blacklisted = true })).GetProperty("client").GetProperty("blacklisted").GetBoolean());
        Assert.Equal("true", await Players.ScalarAsync<string>(Server,
            "SELECT meta ->> 'blacklisted' FROM audit_entries WHERE action = 'blacklist' AND user_id = @Id", new { player.Id }));
        using (var after = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"))
        {
            await Contract.ReadErrorAsync(after, 401, "unauthorized", "userToken");
        }

        // The same value again changes nothing and journals nothing.
        await ExpectAsync(Server, 200, HttpMethod.Patch, path, owner, new { blacklisted = true });
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'blacklist' AND user_id = @Id", new { player.Id }));
    }

    [Fact]
    public async Task The_blacklist_guard_ignores_the_case_of_the_key_as_the_binder_does()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var owner = await LoginAsync(Server, OwnerPin);
        var player = await Players.CreateAsync(Server);
        var path = $"/clients/{player.Id}";
        Task<bool> Blacklisted() => Players.ScalarAsync<bool>(Server,
            "SELECT coalesce((SELECT blacklisted FROM client_profiles WHERE user_id = @Id), false)", new { player.Id });

        // A cashier cannot lift the ban ...
        await Players.ProfileAsync(Server, player, blacklisted: true);
        foreach (var body in new[] { """{"Blacklisted":false}""", """{"BLACKLISTED":false}""", """{"bLaCkLiStEd":null}""", """{"note":"x","Blacklisted":false}""" })
        {
            Contract.AssertError(await ExpectAsync(Server, 403, HttpMethod.Patch, path, cashier, body), "forbidden", "ownerOnly");
            Assert.True(await Blacklisted(), body);
        }

        // ... nor put one on.
        await Players.ProfileAsync(Server, player, blacklisted: false);
        foreach (var body in new[] { """{"BLACKLISTED":true}""", """{"Blacklisted":true}""" })
        {
            Contract.AssertError(await ExpectAsync(Server, 403, HttpMethod.Patch, path, cashier, body), "forbidden", "ownerOnly");
            Assert.False(await Blacklisted(), body);
        }

        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'blacklist' AND user_id = @Id", new { player.Id }));

        // The owner may, in any spelling.
        Assert.True((await ExpectAsync(Server, 200, HttpMethod.Patch, path, owner, """{"BLACKLISTED":true}""")).GetProperty("client").GetProperty("blacklisted").GetBoolean());
        Assert.True(await Blacklisted());
        Assert.False((await ExpectAsync(Server, 200, HttpMethod.Patch, path, owner, """{"Blacklisted":false}""")).GetProperty("client").GetProperty("blacklisted").GetBoolean());
        Assert.False(await Blacklisted());
        Assert.Equal(("blacklisted", "format"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, path, owner, """{"BLACKLISTED":null}""")));
    }

    [Fact]
    public async Task A_client_added_into_a_discount_group_is_flagged_like_one_moved_into_it()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var owner = await LoginAsync(Server, OwnerPin);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var big = (await ExpectAsync(Server, 200, HttpMethod.Post, "/clients", cashier, new { username = $"big-{tag}", displayName = $"Сотрудник {tag}", groupId = "staff" })).GetProperty("client");
        var small = (await ExpectAsync(Server, 200, HttpMethod.Post, "/clients", cashier, new { username = $"small-{tag}", displayName = $"Школьник {tag}", groupId = "student" })).GetProperty("client");
        var none = (await ExpectAsync(Server, 200, HttpMethod.Post, "/clients", cashier, new { username = $"none-{tag}", displayName = $"Без группы {tag}" })).GetProperty("client");

        var bigId = big.GetProperty("id").GetGuid();
        Assert.Equal($"50 Сотрудник {tag} → Сотрудник", await Players.ScalarAsync<string>(Server,
            "SELECT meta ->> 'discountPct' || ' ' || detail FROM audit_entries WHERE action = 'clientGroup' AND user_id = @bigId", new { bigId }));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'clientGroup' AND user_id = @id", new { id = small.GetProperty("id").GetGuid() }));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'clientGroup' AND user_id = @id", new { id = none.GetProperty("id").GetGuid() }));

        // 50 % meets the default 30 % of settings.control.discountPct; 15 % does not.
        var flags = (await ExpectAsync(Server, 200, HttpMethod.Get, "/control", owner)).GetProperty("flags").EnumerateArray()
            .Where(f => f.GetProperty("kind").GetString() == "discount").ToList();
        var flag = Assert.Single(flags, f => f.GetProperty("userId").GetGuid() == bigId);
        Assert.Equal((50, "Сотрудник", $"Сотрудник {tag}", "medium"), (flag.GetProperty("params").GetProperty("pct").GetInt32(), flag.GetProperty("params").GetProperty("group").GetString(),
            flag.GetProperty("params").GetProperty("detail").GetString(), flag.GetProperty("severity").GetString()));
        Assert.DoesNotContain(flags, f => f.GetProperty("userId").GetGuid() == small.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_staff_account_is_no_client_for_the_writes_either()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var account = await Players.CreateAsync(Server, balance: 0, role: "admin", card: $"ADM-{tag}");
        var before = await Players.ScalarAsync<string>(Server, "SELECT password_hash || '|' || card_id || '|' || display_name FROM users WHERE id = @Id", new { account.Id });

        foreach (var (method, path, body) in new (HttpMethod, string, object)[]
                 {
                     (HttpMethod.Post, $"/clients/{account.Id}/password", new { password = "temp-4821" }),
                     (HttpMethod.Patch, $"/clients/{account.Id}", new { displayName = "Захвачен", note = "x" }),
                     (HttpMethod.Post, $"/clients/{account.Id}/card", new { cardId = $"NEW-{tag}" }),
                 })
        {
            var error = await ExpectAsync(Server, 404, method, path, cashier, body);
            Assert.Equal("user", error.GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        }

        Assert.Equal(before, await Players.ScalarAsync<string>(Server, "SELECT password_hash || '|' || card_id || '|' || display_name FROM users WHERE id = @Id", new { account.Id }));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE user_id = @Id", new { account.Id }));
    }

    [Fact]
    public async Task A_new_password_revokes_the_players_tokens_and_replaces_the_old_one()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var (agent, player) = await Players.SignedInAsync(Server);
        var path = $"/clients/{player.Id}/password";
        Assert.Equal(("password", "min"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, path, cashier, new { password = "abc" })));
        Assert.Equal(("password", "required"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, path, cashier, new { })));
        Assert.Equal(("password", "max"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, path, cashier, new { password = new string('x', 65) })));

        Assert.True((await ExpectAsync(Server, 200, HttpMethod.Post, path, cashier, new { password = "temp-4821" })).GetProperty("ok").GetBoolean());
        using (var revoked = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"))
        {
            await Contract.ReadErrorAsync(revoked, 401, "unauthorized", "userToken");
        }

        using (var old = await agent.PostAsync("/api/v1/auth/login", new { kind = "password", username = player.Username, password = Players.Password, pcId = agent.PcId, hwid = agent.Hwid }))
        {
            await Contract.ReadErrorAsync(old, 401, "unauthorized", "badCredentials");
        }

        using (var fresh = await agent.PostAsync("/api/v1/auth/login", new { kind = "password", username = player.Username, password = "temp-4821", pcId = agent.PcId, hwid = agent.Hwid }))
        {
            await Players.ReadAsync(fresh, 200);
        }

        var entry = await Players.ScalarAsync<string>(Server, "SELECT detail || meta::text FROM audit_entries WHERE action = 'clientPassword' AND user_id = @Id", new { player.Id });
        Assert.DoesNotContain("temp-4821", entry);
        Assert.Equal("user", (await ExpectAsync(Server, 404, HttpMethod.Post, $"/clients/{Guid.NewGuid()}/password", cashier, new { password = "12345" }))
            .GetProperty("error").GetProperty("details").GetProperty("what").GetString());
    }

    [Fact]
    public async Task Transactions_are_the_newest_hundred_and_an_unknown_client_has_none()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        await OpenShiftAsync(Server, cashier);
        var player = await Players.CreateAsync(Server, balance: 1_000_000);
        Server.Clock.Advance(TimeSpan.FromMilliseconds(5));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 500_000 });
        Server.Clock.Advance(TimeSpan.FromMilliseconds(5));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/wallet/topup", cashier, new { userId = player.Id, amount = 700_000 });

        var items = (await ExpectAsync(Server, 200, HttpMethod.Get, $"/clients/{player.Id}/transactions", cashier)).GetProperty("items");
        Assert.Equal([700_000L, 500_000L, 1_000_000L], items.EnumerateArray().Select(t => t.GetProperty("amount").GetProperty("amount").GetInt64()));
        Assert.Equal([2_200_000L, 1_500_000L, 1_000_000L], items.EnumerateArray().Select(t => t.GetProperty("balanceAfter").GetProperty("amount").GetInt64()));
        Assert.Equal("topUp", items[0].GetProperty("type").GetString());
        Assert.Equal(0, (await ExpectAsync(Server, 200, HttpMethod.Get, $"/clients/{Guid.NewGuid()}/transactions", cashier)).GetProperty("items").GetArrayLength());
    }
}
