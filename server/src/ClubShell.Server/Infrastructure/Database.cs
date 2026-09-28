using FluentMigrator.Runner;
using Npgsql;

namespace ClubShell.Server.Infrastructure;

public static class Database
{
    public static IServiceCollection AddClubDatabase(this IServiceCollection services, string connectionString)
    {
        DapperSetup.Ensure();
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(Database).Assembly).For.Migrations());
        return services;
    }

    /// <summary>Migrates to the latest version under <c>pg_advisory_lock(CSMig)</c>, held on a dedicated connection.</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection))
        {
            take.Parameters.AddWithValue(AdvisoryLocks.Migrations);
            await take.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
            release.Parameters.AddWithValue(AdvisoryLocks.Migrations);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
