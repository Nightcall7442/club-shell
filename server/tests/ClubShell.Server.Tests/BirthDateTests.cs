using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The date of birth (D-107): required at the desk's registration (<c>400 birthDate required|format|future|min|max</c>),
/// returned with the age and the minor mark in the client list, the card and the picker; the desk sets or changes it on an
/// older account, journaled (<c>clientBirthDate</c>, in the control report's <c>birthDates</c>); the player gives it once on
/// the PC (<c>PATCH /users/{id} {birthDate}</c>), then <c>403 birthDateLocked</c>; the player's own purchase needs it
/// (<c>403 policyDenied birthDateRequired</c>). (The player's own registration on the PC: <see cref="SelfSignupTests"/>.)
/// </summary>
public sealed class BirthDateTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task The_desk_registers_only_with_a_date_of_birth_and_journals_its_changes()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var owner = await LoginAsync(Server, OwnerPin);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var twoYears = Players.BornYearsAgo(Server, 2);
        foreach (var (birthDate, reason) in new (string?, string)[]
                 {
                     (null, "required"), ("", "required"), ("31.12.2000", "format"), ("2000-02-30", "format"), ("2999-01-01", "future"),
                     (twoYears, "min"), ("1901-01-01", "max"),
                 })
        {
            Assert.Equal(("birthDate", reason), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, "/clients", cashier,
                new { displayName = "Без даты", username = $"b0-{tag}", gender = "male", birthDate })));
        }

        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM users WHERE username = @u", new { u = $"b0-{tag}" }));

        var fifteen = Players.BornYearsAgo(Server, 15);
        var made = (await ExpectAsync(Server, 200, HttpMethod.Post, "/clients", cashier,
            new { displayName = "Тимур", username = $"b1-{tag}", gender = "male", birthDate = fifteen })).GetProperty("client");
        Assert.Equal((fifteen, 15, true, int.Parse(fifteen[..4], System.Globalization.CultureInfo.InvariantCulture)),
            (made.GetProperty("birthDate").GetString(), made.GetProperty("age").GetInt32(), made.GetProperty("minor").GetBoolean(), made.GetProperty("birthYear").GetInt32()));
        var lookup = (await RawExpectAsync(Server, 200, HttpMethod.Get, $"/clients/lookup?q=b1-{tag}", cashier)).GetProperty("items")[0];
        Assert.Equal((fifteen, true), (lookup.GetProperty("birthDate").GetString(), lookup.GetProperty("minor").GetBoolean()));

        // An account made before has none: the card says so; the desk sets it (never back to none), and each change is journaled.
        var old = await Players.CreateAsync(Server, birthDate: null);
        var listed = (await ExpectAsync(Server, 200, HttpMethod.Get, $"/clients?q={old.Username}", cashier)).GetProperty("items")[0];
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (listed.GetProperty("birthDate").ValueKind, listed.GetProperty("minor").ValueKind));
        Assert.Equal(("birthDate", "required"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, $"/clients/{old.Id}", cashier, new { birthDate = (string?)null })));
        var set = (await ExpectAsync(Server, 200, HttpMethod.Patch, $"/clients/{old.Id}", cashier, new { birthDate = "2001-03-04" })).GetProperty("client");
        Assert.Equal(("2001-03-04", false), (set.GetProperty("birthDate").GetString(), set.GetProperty("minor").GetBoolean()));
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/clients/{old.Id}", owner, new { birthDate = "2001-03-05" });
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/clients/{old.Id}", cashier, new { note = "постоянный" });
        Assert.Equal("2001-03-05", await Players.ScalarAsync<string>(Server, "SELECT to_char(birth_date, 'YYYY-MM-DD') FROM users WHERE id = @Id", new { old.Id }));
        var journal = (await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(meta ->> 'before' || '>' || (meta ->> 'after'), ',' ORDER BY at, id) FROM audit_entries WHERE action = 'clientBirthDate' AND user_id = @Id", new { old.Id }));
        Assert.Equal("2001-03-04>2001-03-05", journal); // the first one has no «before»: null || … is null
        Assert.Equal(2, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM audit_entries WHERE action = 'clientBirthDate' AND user_id = @Id", new { old.Id }));
        var report = await ExpectAsync(Server, 200, HttpMethod.Get, "/control?days=1", owner);
        Assert.Equal(2, report.GetProperty("birthDates").EnumerateArray().Count(e => e.GetProperty("userId").GetString() == old.Id.ToString()));
    }

    [Fact]
    public async Task The_player_gives_the_date_once_and_cannot_change_it()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        await Players.BirthDateAsync(Server, player, null);

        // Without it the player's own purchase is refused in the forbidden hours only (unknown age, D-108).
        await Players.CurfewAsync(Server);
        try
        {
            var (status, body) = await Players.StartAsync(agent, player);
            Assert.Equal(403, status);
            Assert.Equal("birthDateRequired", PurchaseRulesTests.Details(body).GetProperty("rule").GetString());
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }

        Assert.Equal(400, (int)(await PatchAsync(agent, player, new { birthDate = "2999-01-01" })).StatusCode);
        using (var given = await PatchAsync(agent, player, new { birthDate = "2004-06-07" }))
        {
            Assert.Equal("2004-06-07", (await Players.ReadAsync(given, 200)).GetProperty("birthDate").GetString());
        }

        using (var again = await PatchAsync(agent, player, new { birthDate = "1990-01-01" }))
        {
            await Contract.ReadErrorAsync(again, 403, "forbidden", "birthDateLocked");
        }

        Assert.Equal("2004-06-07", await Players.ScalarAsync<string>(Server, "SELECT to_char(birth_date, 'YYYY-MM-DD') FROM users WHERE id = @Id", new { player.Id }));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server,
            "SELECT count(*)::int FROM audit_entries WHERE action = 'clientBirthDate' AND user_id = @Id AND staff_id IS NULL AND (meta ->> 'byPlayer')::boolean", new { player.Id }));
        using (var me = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{player.Id}"))
        {
            Assert.Equal("2004-06-07", (await Players.ReadAsync(me, 200)).GetProperty("birthDate").GetString());
        }

        Assert.Equal(201, (await Players.StartAsync(agent, player)).Status);
    }

    [Fact]
    public async Task By_day_a_player_without_a_date_starts_on_the_PC_without_questions()
    {
        // Owner's decision: sign in and play, no questions on the PC; the desk fills the date in.
        var (agent, player) = await Players.SignedInAsync(Server);
        await Players.BirthDateAsync(Server, player, null);
        Assert.Equal(201, (await Players.StartAsync(agent, player)).Status);
    }

    [Fact]
    public async Task The_desk_seats_a_client_without_a_date_outside_the_curfew_only()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, birthDate: null);

        // Outside the forbidden hours the desk answers for the client; a purchase that would run into them is refused.
        await Players.CurfewAsync(Server, fromMinutes: 90, toMinutes: 300);
        try
        {
            var refused = await ExpectAsync(Server, 400, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 120 });
            var details = PurchaseRulesTests.Details(refused);
            Assert.Equal(("minutes", "curfew", true), (details.GetProperty("field").GetString(), details.GetProperty("reason").GetString(), details.GetProperty("noBirthDate").GetBoolean()));
            Assert.InRange(details.GetProperty("maxMinutes").GetInt32(), 88, 90);
            await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 60 });
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId });

            await Players.CurfewAsync(Server);
            var denied = await ExpectAsync(Server, 403, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 30 });
            Assert.Equal("birthDateRequired", PurchaseRulesTests.Details(denied).GetProperty("rule").GetString());
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        }
    }

    private static Task<HttpResponseMessage> PatchAsync(TestAgent agent, TestPlayer player, object body) =>
        agent.SendAsync(HttpMethod.Patch, $"/api/v1/users/{player.Id}", JsonSerializer.Serialize(body));
}
