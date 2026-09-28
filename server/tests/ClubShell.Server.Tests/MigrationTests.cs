using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>Every migration rolls back to an empty schema and forward again without manual steps (port of club-server).</summary>
public sealed class MigrationTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public void All_migrations_roll_back_to_empty_and_forward_again()
    {
        using var scope = server.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        runner.MigrateDown(0);
        runner.MigrateUp();
        runner.MigrateDown(0);
        runner.MigrateUp();
    }

    [Fact]
    public async Task Clock_values_are_written_truncated_to_milliseconds()
    {
        // DESIGN §4: TimeProvider.System has 100 ns ticks, PostgreSQL keeps µs; the Dapper handler cuts to ms for every slice.
        var at = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var stored = await c.QuerySingleAsync<DateTimeOffset>("SELECT @at::timestamptz", new { at });
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 10, 0, 0, 123, TimeSpan.Zero), stored);
    }
}
