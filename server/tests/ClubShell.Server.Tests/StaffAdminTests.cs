using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The owner's staff editor (slice S5, DESIGN §3.5): owner only (<c>ck_</c> too), PIN 4–8 digits and unique on create and
/// change, nobody disables themselves, disabling revokes the member's tokens for good.
/// </summary>
public sealed class StaffAdminTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Owner_lists_adds_and_edits_staff_and_a_cashier_is_refused_first()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, "/staff", null), (HttpMethod.Post, "/staff", new { }), (HttpMethod.Patch, $"/staff/{Guid.NewGuid()}", new { pin = "x" }),
                 })
        {
            // ownerOnly comes before any check of the body or the id.
            Contract.AssertError(await ExpectAsync(server, 403, method, path, cashier, body), "forbidden", "ownerOnly");
        }

        var list = (await ExpectAsync(server, 200, HttpMethod.Get, "/staff", owner)).GetProperty("items");
        Assert.Contains(list.EnumerateArray(), s => s.GetProperty("name").GetString() == "Кассир Азиз" && s.GetProperty("role").GetString() == "cashier");
        Assert.All(list.EnumerateArray(), s => Assert.False(s.TryGetProperty("pin", out _)));

        foreach (var (body, field, reason) in new (object, string, string)[]
                 {
                     (new { name = "Бобур", role = "cashier", pin = "123" }, "pin", "digits4to8"),
                     (new { name = "Бобур", role = "cashier", pin = "１２３４" }, "pin", "digits4to8"),
                     (new { name = "Бобур", role = "cashier", pin = OwnerPin }, "pin", "taken"),
                     (new { name = "", role = "cashier", pin = "4321" }, "name", "required"),
                     (new { name = "Бобур", role = "boss", pin = "4321" }, "role", "enum"),
                 })
        {
            Assert.Equal((field, reason), Details(await ExpectAsync(server, 400, HttpMethod.Post, "/staff", owner, body)));
        }

        var id = (await ReplayedAsync(server, 200, HttpMethod.Post, "/staff", owner, new { name = "Бобур", role = "cashier", pin = "4321" }))
            .GetProperty("id").GetString()!;
        Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM staff WHERE name = 'Бобур'"));
        var token = await LoginAsync(server, "4321");
        Assert.Equal((id, "cashier"), Member(await ExpectAsync(server, 200, HttpMethod.Get, "/me", token)));

        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{id}", owner, new { name = "Бобур Р." });
        Assert.Equal(("pin", "taken"), Details(await ExpectAsync(server, 400, HttpMethod.Patch, $"/staff/{id}", owner, new { pin = CashierPin })));
        Assert.Equal(("pin", "digits4to8"), Details(await ExpectAsync(server, 400, HttpMethod.Patch, $"/staff/{id}", owner, new { pin = "12a4" })));
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{id}", owner, new { pin = "98765" });
        Assert.Equal("Бобур Р.", (await ExpectAsync(server, 200, HttpMethod.Get, "/me", await LoginAsync(server, "98765"))).GetProperty("staff").GetProperty("name").GetString());

        foreach (var unknown in new[] { Guid.NewGuid().ToString(), "owner" })
        {
            var error = await ExpectAsync(server, 404, HttpMethod.Patch, $"/staff/{unknown}", owner, new { name = "X" });
            Assert.Equal("staff", error.GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        }

        var self = (await ExpectAsync(server, 200, HttpMethod.Get, "/me", owner)).GetProperty("staff").GetProperty("id").GetString();
        Assert.Equal(("active", "self"), Details(await ExpectAsync(server, 400, HttpMethod.Patch, $"/staff/{self}", owner, new { active = false })));
        Assert.Equal(2, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM audit_entries WHERE action = 'staffUpdate' AND meta ->> 'staffId' = @id", new { id }));
        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM audit_entries WHERE meta::text LIKE '%98765%' OR detail LIKE '%98765%'"));
    }

    [Fact]
    public async Task Disabling_a_member_kills_their_tokens_for_good()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var id = (await ExpectAsync(server, 200, HttpMethod.Post, "/staff", owner, new { name = "Temp", role = "owner", pin = "55667788" })).GetProperty("id").GetString();
        var token = await LoginAsync(server, "55667788");
        await ExpectAsync(server, 200, HttpMethod.Get, "/staff", token);

        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{id}", owner, new { active = false });
        using (var response = await server.Http.SendAsync(Request(HttpMethod.Get, "/me", token)))
        {
            await Contract.ReadErrorAsync(response, 401, "unauthorized", "invalid");
        }

        using (var login = await server.Http.PostAsync("/api/v1/admin/login", JsonBody(new { pin = "55667788" })))
        {
            await Contract.ReadErrorAsync(login, 401, "unauthorized", "invalidPin");
        }

        var listed = (await ExpectAsync(server, 200, HttpMethod.Get, "/staff", owner)).GetProperty("items").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        Assert.False(listed.GetProperty("active").GetBoolean());

        // Enabled again: the old token stays dead, a new sign-in works.
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{id}", owner, new { active = true });
        using (var response = await server.Http.SendAsync(Request(HttpMethod.Get, "/me", token)))
        {
            await Contract.ReadErrorAsync(response, 401, "unauthorized", "invalid");
        }

        await ExpectAsync(server, 200, HttpMethod.Get, "/me", await LoginAsync(server, "55667788"));
    }

    [Fact]
    public async Task Club_api_key_is_the_owner_of_the_staff_editor()
    {
        var key = await ApiKeyAsync(server);
        try
        {
            await ExpectAsync(server, 200, HttpMethod.Get, "/staff", key);
            var id = (await ExpectAsync(server, 200, HttpMethod.Post, "/staff", key, new { name = "Через ключ", role = "cashier", pin = "13579" })).GetProperty("id").GetString();
            Assert.Equal(StaffTokensName, await Players.ScalarAsync<string>(server, "SELECT staff_name FROM audit_entries WHERE action = 'staffAdd' AND meta ->> 'staffId' = @id", new { id }));
        }
        finally
        {
            await ClearApiKeyAsync(server);
        }
    }

    [Fact]
    public async Task The_last_active_owner_cannot_be_disabled_not_even_through_the_api_key()
    {
        var (ownerId, restore) = await SoleOwnerAsync();
        try
        {
            var key = await ApiKeyAsync(server); // no StaffId: the "self" check never matches it

            Assert.Equal(("active", "lastOwner"), Details(await ExpectAsync(server, 400, HttpMethod.Patch, $"/staff/{ownerId}", key, new { active = false })));
            Assert.True(await ActiveAsync(ownerId));
            await ExpectAsync(server, 200, HttpMethod.Get, "/me", await LoginAsync(server, OwnerPin));

            // With a second owner one of them may go; the last one may not.
            var second = (await ExpectAsync(server, 200, HttpMethod.Post, "/staff", key, new { name = "Второй владелец", role = "owner", pin = "24681357" })).GetProperty("id").GetGuid();
            await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{ownerId}", key, new { active = false });
            Assert.False(await ActiveAsync(ownerId));
            using (var login = await server.Http.PostAsync("/api/v1/admin/login", JsonBody(new { pin = OwnerPin })))
            {
                await Contract.ReadErrorAsync(login, 401, "unauthorized", "invalidPin");
            }

            Assert.Equal(("active", "lastOwner"), Details(await ExpectAsync(server, 400, HttpMethod.Patch, $"/staff/{second}", key, new { active = false })));
            Assert.True(await ActiveAsync(second));
            await ExpectAsync(server, 200, HttpMethod.Get, "/me", await LoginAsync(server, "24681357"));

            // A disabled owner is not "the last": disabling again is a no-op; a cashier goes whenever.
            await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{ownerId}", key, new { active = false });
            var cashier = (await ExpectAsync(server, 200, HttpMethod.Post, "/staff", key, new { name = "Временный кассир", role = "cashier", pin = "86420" })).GetProperty("id").GetGuid();
            await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{cashier}", key, new { active = false });
            Assert.False(await ActiveAsync(cashier));
        }
        finally
        {
            await restore();
        }
    }

    [Fact]
    public async Task Two_owners_disabling_each_other_at_once_leave_one()
    {
        var (ownerId, restore) = await SoleOwnerAsync();
        try
        {
            var key = await ApiKeyAsync(server);
            var second = (await ExpectAsync(server, 200, HttpMethod.Post, "/staff", key, new { name = "Соперник", role = "owner", pin = "97531864" })).GetProperty("id").GetGuid();

            // Through the key both requests always pass authentication, then only one may win the lock.
            var results = await Task.WhenAll(
                SendAsync(server, HttpMethod.Patch, $"/staff/{ownerId}", key, new { active = false }),
                SendAsync(server, HttpMethod.Patch, $"/staff/{second}", key, new { active = false }));

            Assert.Equal([200, 400], results.Select(r => r.Status).Order());
            Assert.Equal(("active", "lastOwner"), Details(results.Single(r => r.Status == 400).Body));
            Assert.Equal(1, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM staff WHERE role = 'owner' AND active"));
        }
        finally
        {
            await restore();
        }
    }

    /// <summary>
    /// The seed owner is the only active owner (the other tests of the class add owners of their own): its id and the way back,
    /// which also removes the club API key.
    /// </summary>
    private async Task<(Guid OwnerId, Func<Task> Restore)> SoleOwnerAsync()
    {
        var ownerId = Guid.Parse((await ExpectAsync(server, 200, HttpMethod.Get, "/me", await LoginAsync(server, OwnerPin))).GetProperty("staff").GetProperty("id").GetString()!);
        var others = await Players.ScalarAsync<string>(server,
            "SELECT coalesce(string_agg(id::text, ','), '') FROM staff WHERE role = 'owner' AND active AND id <> @ownerId", new { ownerId });
        await Players.ExecuteAsync(server, "UPDATE staff SET active = false WHERE role = 'owner' AND active AND id <> @ownerId", new { ownerId });
        return (ownerId, async () =>
        {
            await Players.ExecuteAsync(server, "UPDATE staff SET active = true WHERE id = @ownerId OR id = ANY(string_to_array(@others, ',')::uuid[])", new { ownerId, others });
            await ClearApiKeyAsync(server);
        });
    }

    private Task<bool> ActiveAsync(Guid id) => Players.ScalarAsync<bool>(server, "SELECT active FROM staff WHERE id = @id", new { id });

    private const string StaffTokensName = Auth.StaffTokens.ApiKeyName;

    private static (string?, string?) Member(JsonElement me) =>
        (me.GetProperty("staff").GetProperty("id").GetString(), me.GetProperty("staff").GetProperty("role").GetString());

    internal static (string?, string?) Details(JsonElement error) =>
        (error.GetProperty("error").GetProperty("details").GetProperty("field").GetString(), error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
}
