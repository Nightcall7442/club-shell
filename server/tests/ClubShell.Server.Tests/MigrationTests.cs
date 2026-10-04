using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>Every migration rolls back to an empty schema and forward again without manual steps (port of club-server).</summary>
public sealed class MigrationTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task All_migrations_roll_back_to_empty_and_forward_again()
    {
        using var scope = server.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        runner.MigrateDown(0);
        Assert.Equal(["VersionInfo"], await TablesAsync());
        runner.MigrateUp();
        runner.MigrateDown(0);
        Assert.Equal(["VersionInfo"], await TablesAsync());
        runner.MigrateUp();
        Assert.Contains("agent_commands", await TablesAsync());
        Assert.Contains("cash_movements", await TablesAsync());

        // M0008 alone, down and up again: its columns and index go and come back.
        runner.MigrateDown(2026100202);
        Assert.DoesNotContain("cash_movements", await TablesAsync());
        Assert.Equal(0, await ScalarAsync("SELECT count(*)::int FROM information_schema.columns WHERE table_name = 'shifts' AND column_name LIKE 'closed_by%'"));
        runner.MigrateUp();
        Assert.Equal(2, await ScalarAsync("SELECT count(*)::int FROM information_schema.columns WHERE table_name = 'shifts' AND column_name LIKE 'closed_by%'"));
        Assert.Equal(1, await ScalarAsync("SELECT count(*)::int FROM pg_indexes WHERE indexname = 'audit_entries_shift'"));
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>(sql);
    }

    /// <summary>Tables, views and sequences of the public schema: a Down must leave nothing but FluentMigrator's own table.</summary>
    private async Task<List<string>> TablesAsync()
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        return (await c.QueryAsync<string>(
            """
            SELECT relname FROM pg_class JOIN pg_namespace n ON n.oid = relnamespace
            WHERE nspname = 'public' AND relkind IN ('r', 'v', 'm', 'S', 'p') ORDER BY relname
            """)).ToList();
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
