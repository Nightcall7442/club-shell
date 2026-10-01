using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Admin;
using ClubShell.Server.Games;
using Npgsql;

namespace ClubShell.Server.Infrastructure;

/// <summary>
/// The seed files every club gets (D-14): the PC policy, the games catalog and the shop products. Applied to all clubs at
/// startup and again when the platform creates a club; each seed is idempotent, so the clubs that have them see no change.
/// </summary>
public sealed record ClubSeeds(string PolicyPath, string GamesPath, string ProductsPath)
{
    public async Task ApplyAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<NpgsqlDataSource>();
        var clock = services.GetRequiredService<TimeProvider>();
        await services.GetRequiredService<ClubRepository>().SeedPolicyAsync(JsonDefaults.Deserialize<Policy>(File.ReadAllText(PolicyPath))
            ?? throw new InvalidOperationException($"Policy seed {PolicyPath} is empty"));
        await CatalogSeed.ApplyAsync(db, GamesPath, clock);
        await ProductSeed.ApplyAsync(db, ProductsPath, clock);
    }
}
