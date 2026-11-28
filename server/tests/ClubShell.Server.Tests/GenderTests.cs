using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The player's gender (D-104): required at the desk's registration (<c>400 gender required|enum</c>), returned in the
/// client list, the card and the picker, set in the card; an account made before has none until the desk sets it.
/// (The player's own registration on the PC: <see cref="SelfSignupTests"/>.)
/// </summary>
public sealed class GenderTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task The_desk_registers_only_with_a_gender_and_sets_it_on_an_older_account()
    {
        var cashier = await LoginAsync(Server, CashierPin);
        var tag = Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(("gender", "required"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, "/clients", cashier,
            new { displayName = "Без пола", username = $"g0-{tag}" })));
        Assert.Equal(("gender", "enum"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, "/clients", cashier,
            new { displayName = "Без пола", username = $"g0-{tag}", gender = "other" })));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM users WHERE username = @u", new { u = $"g0-{tag}" }));

        var made = (await ExpectAsync(Server, 200, HttpMethod.Post, "/clients", cashier,
            new { displayName = "Мадина", username = $"g1-{tag}", gender = "female", birthDate = Players.AdultBirthDate })).GetProperty("client");
        Assert.Equal("female", made.GetProperty("gender").GetString());
        var lookup = (await RawExpectAsync(Server, 200, HttpMethod.Get, $"/clients/lookup?q=g1-{tag}", cashier)).GetProperty("items")[0];
        Assert.Equal("female", lookup.GetProperty("gender").GetString());

        // An account made before has none; the card sets it, never back to none.
        var old = await Players.CreateAsync(Server);
        var listed = (await ExpectAsync(Server, 200, HttpMethod.Get, $"/clients?q={old.Username}", cashier)).GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("gender").ValueKind);
        Assert.Equal(("gender", "enum"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, $"/clients/{old.Id}", cashier, new { gender = "x" })));
        Assert.Equal(("gender", "required"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, $"/clients/{old.Id}", cashier, new { gender = (string?)null })));
        var set = (await ExpectAsync(Server, 200, HttpMethod.Patch, $"/clients/{old.Id}", cashier, new { gender = "male" })).GetProperty("client");
        Assert.Equal("male", set.GetProperty("gender").GetString());
        var kept = (await ExpectAsync(Server, 200, HttpMethod.Patch, $"/clients/{old.Id}", cashier, new { note = "постоянный" })).GetProperty("client");
        Assert.Equal("male", kept.GetProperty("gender").GetString());
    }
}
