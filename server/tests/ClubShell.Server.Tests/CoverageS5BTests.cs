using ClubShell.Server.Admin;
using ClubShell.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Coverage of slice S5 part B (DESIGN §10.a, §11): each of its ten operations got a contract-valid success response; and
/// the server's final coverage — all 75 operations the contract requires, plus <c>getBalance</c>, <c>reportAntiCheat</c>,
/// <c>getTransactions</c> and (beyond the contract) <c>PATCH /admin/games/{id}</c>.
/// </summary>
public sealed class CoverageS5BTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task S5_part_B_operations_all_have_a_contract_valid_success_response()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var agent = await TestAgent.CreateAsync(Server);
        await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
        {
            branding = new { clubName = "Покрытие", accent = "#112233", logoUrl = (string?)null, wallpaperUrl = (string?)null },
            control = new { earlyEndMinutes = 15 },
            automation = new[] { new { name = "Сообщение", enabled = true, trigger = new { kind = "minutesLeft", value = 5 }, action = new { kind = "message", text = "5 минут" } } },
            notifications = new { bigTopupAt = 10_000_000 },
        });
        var key = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club/api-key", owner)).GetProperty("apiKey").GetString()!;
        await ExpectAsync(Server, 200, HttpMethod.Post, "/club/api-key", key, new { });
        await ClearApiKeyAsync(Server);
        await ExpectAsync(Server, 200, HttpMethod.Get, "/games", owner);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/health/settings", owner, new { gpuHotC = 80 });

        // A ticket to move: the worker opens it from a hot live sample.
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Players.ExecuteAsync(Server, "INSERT INTO pc_metrics (pc_id, at, data) VALUES (@PcId, @at, '{\"temps\":{\"cpu\":50,\"gpu\":90},\"fps\":null}')",
            new { agent.PcId, at = Server.Clock.GetUtcNow().AddMinutes(-1) });
        await Server.Services.GetRequiredService<HealthWorker>().RunOnceAsync();
        var health = await ExpectAsync(Server, 200, HttpMethod.Get, "/health", owner);
        var ticket = health.GetProperty("tickets")[0].GetProperty("id").GetGuid();
        await ExpectAsync(Server, 200, HttpMethod.Patch, $"/health/tickets/{ticket}", owner, new { status = "inWork" });
        await ExpectAsync(Server, 200, HttpMethod.Get, "/control?days=7", owner);
        await ExpectAsync(Server, 200, HttpMethod.Get, "/reports?days=7", owner);

        string[] operations =
        [
            .. ClubSettingsEndpoints.Operations, .. CatalogAdminEndpoints.Operations, .. HealthEndpoints.Operations, .. ControlEndpoints.Operations,
            .. ReportsEndpoints.Operations,
        ];
        Assert.Equal(10, operations.Length);
        Assert.All(operations, op => Assert.True(Server.Covered.ContainsKey($"{op} 200"), $"{op} 200 not covered"));
    }

    [Fact]
    public async Task All_required_operations_and_the_three_extras_are_implemented()
    {
        var required = Contract.Operations.Where(o => o.Operation.GetProperty("x-server-status").GetString() == "required").Select(o => o.OperationId).ToList();
        Assert.Equal(75, required.Count);
        Assert.Equal(required.Append("getBalance").Append("reportAntiCheat").Append("getTransactions").Order(), ContractStatus.Implemented.Order());

        // PATCH /admin/games/{id} is not in the contract: a client without the response validator.
        var owner = await LoginAsync(Server, OwnerPin);
        var game = Guid.NewGuid();
        await Players.ExecuteAsync(Server, "INSERT INTO games (id, club_id, title, data, updated_at) SELECT @game, id, 'Coverage', '{}', now() FROM clubs", new { game });
        using var raw = Server.CreateDefaultClient();
        using var response = await raw.SendAsync(Request(HttpMethod.Patch, $"/games/{game}", owner, new { settingsPaths = new[] { "%APPDATA%\\Coverage" } }));
        Assert.Equal("{\"settingsPaths\":[\"%APPDATA%\\\\Coverage\"]}", (await Players.ReadAsync(response, 200)).GetRawText());
    }
}
