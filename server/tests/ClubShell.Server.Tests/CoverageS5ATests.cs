using ClubShell.Server.Admin;
using ClubShell.Server.Infrastructure;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>Every operation of slice S5 part A answers its success status with a body that matches the contract.</summary>
public sealed class CoverageS5ATests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task S5_part_A_operations_all_have_a_contract_valid_success_response()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var player = await Players.CreateAsync(Server);
        await ExpectAsync(Server, 200, HttpMethod.Get, "/staff", owner);
        var staffId = (await ExpectAsync(Server, 200, HttpMethod.Post, "/staff", owner, new { name = "Покрытие", role = "cashier", pin = "24680" }, Guid.NewGuid()))
            .GetProperty("id").GetString();
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/staff/{staffId}", owner, new { name = "Покрытие 2", active = true, pin = "246801" });

        await ExpectAsync(Server, 200, HttpMethod.Get, "/clients?q=", owner);
        var clientId = (await ExpectAsync(Server, 200, HttpMethod.Post, "/clients", owner,
            new { username = "cover-" + Guid.NewGuid().ToString("N")[..8], displayName = "Покрытие", password = "1234", cardId = (string?)null, birthYear = (int?)null }, Guid.NewGuid()))
            .GetProperty("client").GetProperty("id").GetGuid();
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/clients/{clientId}", owner, new { groupId = "regular", blacklisted = false });
        await ExpectAsync(Server, 200, HttpMethod.Post, $"/clients/{clientId}/card", owner, new { cardId = "COVER-" + Guid.NewGuid().ToString("N")[..6] });
        await ExpectAsync(Server, 200, HttpMethod.Post, $"/clients/{clientId}/password", owner, new { password = "abcd" });
        await ExpectAsync(Server, 200, HttpMethod.Post, "/promo/redeem", owner, new { userId = player.Id, code = "WELCOME" }, Guid.NewGuid());
        await ExpectAsync(Server, 200, HttpMethod.Get, $"/clients/{player.Id}/transactions", owner);

        await ExpectAsync(Server, 200, HttpMethod.Get, "/tariffs", owner);
        var tariff = new { name = "Покрытие", pricePerHour = 1_000_000, minMinutes = 30, maxMinutes = 600, zones = new[] { "VIP" }, isPackage = false };
        var tariffId = (await ExpectAsync(Server, 200, HttpMethod.Post, "/tariffs", owner, tariff, Guid.NewGuid())).GetProperty("tariff").GetProperty("id").GetGuid();
        await ExpectAsync(Server, 200, HttpMethod.Put, $"/tariffs/{tariffId}", owner, tariff);
        await ExpectAsync(Server, 200, HttpMethod.Delete, $"/tariffs/{tariffId}", owner);

        await ExpectAsync(Server, 200, HttpMethod.Get, "/products", owner);
        var product = DevSeed.Sid("product:water");
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/products/{product}", owner, new { stockQty = 50 });
        await ExpectAsync(Server, 200, HttpMethod.Post, $"/products/{product}/receive", owner, new { qty = 10 }, Guid.NewGuid());

        string[] operations =
        [
            .. StaffAdminEndpoints.Operations, .. ClientEndpoints.Operations, .. PromoEndpoints.Operations, .. TariffEndpoints.Operations,
            .. StockEndpoints.Operations,
        ];
        Assert.Equal(17, operations.Length);
        Assert.All(operations, op => Assert.True(Server.Covered.ContainsKey($"{op} 200"), $"{op} 200 not covered"));
    }
}
