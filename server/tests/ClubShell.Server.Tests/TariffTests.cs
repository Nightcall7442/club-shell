using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Tariffs of the console (slice S5, D-8): owner-only edits, integer tiyin in and Money out, soft delete (a running session
/// still settles, a new purchase no longer finds it), the agent's <c>GET /tariffs</c> ETag following every change and
/// <c>refreshConfig {tariffs:true}</c> queued for the club's PCs.
/// </summary>
public sealed class TariffTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Owner_edits_move_the_agents_etag_and_delete_is_soft()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var cashier = await LoginAsync(Server, CashierPin);
        var (agent, player) = await Players.SignedInAsync(Server);
        var etag0 = await ETagAsync(agent);

        var input = new
        {
            name = "Дневной", pricePerHour = 900_000, minMinutes = 60, maxMinutes = (int?)null, zones = Array.Empty<string>(),
            timeWindows = new[] { new { days = new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" }, from = "00:00", to = "00:00" } }, isPackage = false,
            packageMinutes = (int?)null, packagePrice = (long?)null,
        };
        Contract.AssertError(await ExpectAsync(Server, 403, HttpMethod.Post, "/tariffs", cashier, input), "forbidden", "ownerOnly");
        var tariff = (await ReplayedAsync(Server, 200, HttpMethod.Post, "/tariffs", owner, input)).GetProperty("tariff");
        var id = tariff.GetProperty("id").GetGuid();
        Assert.Equal((900_000L, "UZS", 60, JsonValueKind.Null, "tue"), (tariff.GetProperty("pricePerHour").GetProperty("amount").GetInt64(),
            tariff.GetProperty("pricePerHour").GetProperty("currency").GetString(), tariff.GetProperty("minMinutes").GetInt32(),
            tariff.GetProperty("packagePrice").ValueKind, tariff.GetProperty("timeWindows")[0].GetProperty("days")[1].GetString()));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM tariffs WHERE name = 'Дневной'"));
        var etag1 = await ETagAsync(agent);
        Assert.NotEqual(etag0, etag1);
        Assert.Contains((await ExpectAsync(Server, 200, HttpMethod.Get, "/tariffs", cashier)).GetProperty("items").EnumerateArray(), t => t.GetProperty("id").GetGuid() == id);

        // Every change queues refreshConfig {tariffs:true} for the PC.
        var commands = (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("commands")), 200)).GetProperty("items");
        var refresh = commands.EnumerateArray().Last(c => c.GetProperty("name").GetString() == "refreshConfig").GetProperty("payload");
        Assert.Equal(["tariffs"], refresh.EnumerateObject().Select(p => p.Name));
        Assert.True(refresh.GetProperty("tariffs").GetBoolean());

        var saved = (await ExpectAsync(Server, 200, HttpMethod.Put, $"/tariffs/{id}", owner, input)).GetProperty("tariff");
        Assert.Equal(id, saved.GetProperty("id").GetGuid());
        var package = (await ExpectAsync(Server, 200, HttpMethod.Put, $"/tariffs/{id}", owner,
            new { name = "Пакет 3ч", pricePerHour = 0, zones = new[] { "VIP" }, isPackage = true, packageMinutes = 180, packagePrice = 2_500_000 })).GetProperty("tariff");
        Assert.Equal((30, 180, 2_500_000L, "VIP"), (package.GetProperty("minMinutes").GetInt32(), package.GetProperty("packageMinutes").GetInt32(),
            package.GetProperty("packagePrice").GetProperty("amount").GetInt64(), package.GetProperty("zones")[0].GetString()));
        var etag2 = await ETagAsync(agent);
        Assert.NotEqual(etag1, etag2);

        // Back to an hourly tariff everywhere, a session on it, then delete: the session still settles.
        await ExpectAsync(Server, 200, HttpMethod.Put, $"/tariffs/{id}", owner, input);
        var (status, _) = await Players.StartAsync(agent, player, tariff: id, minutes: 60);
        Assert.Equal(201, status);
        Assert.True((await ExpectAsync(Server, 200, HttpMethod.Delete, $"/tariffs/{id}", owner)).GetProperty("ok").GetBoolean());
        Assert.DoesNotContain((await ExpectAsync(Server, 200, HttpMethod.Get, "/tariffs", cashier)).GetProperty("items").EnumerateArray(), t => t.GetProperty("id").GetGuid() == id);
        Assert.True(await Players.ScalarAsync<bool>(Server, "SELECT deleted_at IS NOT NULL FROM tariffs WHERE id = @id", new { id }));
        Assert.NotEqual(etag2, await ETagAsync(agent));
        await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", cashier, new { pcId = agent.PcId });

        var (again, body) = await Players.StartAsync(agent, player, tariff: id, minutes: 60);
        Assert.Equal((404, "tariff"), (again, body.GetProperty("error").GetProperty("details").GetProperty("what").GetString()));
        foreach (var gone in new[] { id.ToString(), Guid.NewGuid().ToString(), "nope" })
        {
            await ExpectAsync(Server, 200, HttpMethod.Delete, $"/tariffs/{gone}", owner);
        }

        Assert.Equal("tariff", (await ExpectAsync(Server, 404, HttpMethod.Put, $"/tariffs/{id}", owner, input)).GetProperty("error").GetProperty("details").GetProperty("what").GetString());
        Assert.Equal("tariffAdd tariffDelete tariffSave tariffSave tariffSave", await Players.ScalarAsync<string>(Server,
            "SELECT string_agg(action, ' ' ORDER BY action) FROM audit_entries WHERE meta ->> 'tariffId' = @id", new { id = id.ToString() }));
    }

    [Fact]
    public async Task Tariff_input_is_checked_against_the_contract()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var windows = new[] { new { days = new[] { "mon" }, from = "10:00", to = "18:00" } };
        foreach (var (body, field, reason) in new (object, string, string)[]
                 {
                     (new { name = "T", pricePerHour = 1, zones = Array.Empty<string>() }, "isPackage", "required"),
                     (new { name = "T", pricePerHour = 1, isPackage = false }, "zones", "required"),
                     (new { name = "", pricePerHour = 1, zones = Array.Empty<string>(), isPackage = false }, "name", "required"),
                     (new { name = "T", pricePerHour = -1, zones = Array.Empty<string>(), isPackage = false }, "pricePerHour", "min"),
                     (new { name = "T", pricePerHour = 1, minMinutes = 4, zones = Array.Empty<string>(), isPackage = false }, "minMinutes", "min"),
                     (new { name = "T", pricePerHour = 1, minMinutes = 60, maxMinutes = 30, zones = Array.Empty<string>(), isPackage = false }, "maxMinutes", "range"),
                     (new { name = "T", pricePerHour = 1, zones = Enumerable.Repeat("Z", 21), isPackage = false }, "zones", "max"),
                     (new { name = "T", pricePerHour = 1, zones = Array.Empty<string>(), isPackage = true, packagePrice = 5 }, "packageMinutes", "required"),
                     (new { name = "T", pricePerHour = 1, zones = Array.Empty<string>(), isPackage = true, packageMinutes = 60 }, "packagePrice", "required"),
                     (new { name = "T", pricePerHour = 1, zones = Array.Empty<string>(), isPackage = false, timeWindows = new[] { new { days = new[] { "xyz" }, from = "10:00", to = "18:00" } } }, "timeWindows[0].days", "enum"),
                     (new { name = "T", pricePerHour = 1, zones = Array.Empty<string>(), isPackage = false, timeWindows = new[] { new { days = new[] { "mon" }, from = "25:00", to = "18:00" } } }, "timeWindows[0].from", "format"),
                     (new { name = "T", pricePerHour = 1, zones = Array.Empty<string>(), isPackage = false, timeWindows = new object[] { windows[0], new { days = new[] { "tue" }, from = "10:00" } } }, "timeWindows[1].to", "required"),
                     (new { name = "T", pricePerHour = "1", zones = Array.Empty<string>(), isPackage = false }, "pricePerHour", "format"),
                 })
        {
            Assert.Equal((field, reason), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, "/tariffs", owner, body)));
        }

        // Package fields of an hourly tariff are ignored.
        var hourly = (await ExpectAsync(Server, 200, HttpMethod.Post, "/tariffs", owner,
            new { name = "T", pricePerHour = 1, zones = Array.Empty<string>(), isPackage = false, packageMinutes = 60, packagePrice = 5, timeWindows = windows })).GetProperty("tariff");
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, "18:00"), (hourly.GetProperty("packageMinutes").ValueKind, hourly.GetProperty("packagePrice").ValueKind,
            hourly.GetProperty("timeWindows")[0].GetProperty("to").GetString()));
    }

    private static async Task<string> ETagAsync(TestAgent agent)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, "/api/v1/tariffs");
        await Players.ReadAsync(response, 200);
        return response.Headers.ETag!.ToString();
    }
}
