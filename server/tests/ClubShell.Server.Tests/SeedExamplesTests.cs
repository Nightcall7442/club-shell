namespace ClubShell.Server.Tests;

/// <summary>A fixture started on the example catalogs shipped in <c>config/</c> (DESIGN D-14; server/README.md, "Каталог игр и товаров").</summary>
public sealed class ExampleCatalogFixture : ServerFixture
{
    public ExampleCatalogFixture()
    {
        Settings["Catalog:GamesSeedPath"] = ConfigFile("games.example.json");
        Settings["Catalog:ProductsSeedPath"] = ConfigFile("products.example.json");
    }

    private static string ConfigFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "config", name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"config/{name} not found above {AppContext.BaseDirectory}");
    }
}

/// <summary>The example seeds load at startup and their data passes the contract in both directions the console and the agent read it.</summary>
public sealed class SeedExamplesTests(ExampleCatalogFixture server) : IClassFixture<ExampleCatalogFixture>
{
    [Fact]
    public async Task The_example_games_load_and_the_agent_reads_them()
    {
        var agent = await TestAgent.CreateAsync(server);
        var body = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/games"), 200);

        Assert.Equal(15, body.GetProperty("total").GetInt32());
        var games = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(games, g => g.GetProperty("title").GetString() == "Counter-Strike 2" && g.GetProperty("coverUrl").GetString()!.StartsWith("https://", StringComparison.Ordinal));
        Assert.Contains(games, g => g.GetProperty("title").GetString() == "Minecraft" && g.GetProperty("launcher").GetString() == "exe");
    }

    [Fact]
    public async Task The_example_products_load_and_the_console_lists_them()
    {
        var owner = await Staff.LoginAsync(server, "0000");
        var body = await Staff.ExpectAsync(server, 200, HttpMethod.Get, "/products", owner);

        var products = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(12, products.Count);
        Assert.Contains(products, p => p.GetProperty("title").GetString() == "Coca-Cola 0.5L" && p.GetProperty("stockQty").GetInt32() == 48);
    }
}
