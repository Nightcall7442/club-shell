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

        // M0009 alone: the bar's and the inbox's tables, the products' columns and the index go and come back.
        string[] tables = ["shop_sales", "shop_sale_lines", "admin_calls"];
        runner.MigrateDown(2026100401);
        Assert.Empty((await TablesAsync()).Intersect(tables));
        Assert.Equal(0, await ScalarAsync("SELECT count(*)::int FROM information_schema.columns WHERE table_name = 'products' AND column_name IN ('source', 'deleted_by')"));
        runner.MigrateUp();
        Assert.Equal(tables.Order(), (await TablesAsync()).Intersect(tables).Order());
        Assert.Equal(2, await ScalarAsync("SELECT count(*)::int FROM information_schema.columns WHERE table_name = 'products' AND column_name IN ('source', 'deleted_by')"));
        Assert.Equal(1, await ScalarAsync("SELECT count(*)::int FROM pg_indexes WHERE indexname = 'session_events_server'"));
    }

    /// <summary>
    /// D-62: a stored <c>features.callAdmin = false</c> never had any effect (the server always sent false) and is usually the
    /// default the console copied, so M0009 turns it on once and bumps the versions; Down leaves the owner's switch alone.
    /// </summary>
    [Fact]
    public async Task M0009_turns_a_stored_callAdmin_false_on_once()
    {
        using var scope = server.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        // The other test of the class may have emptied the database: the club comes back as at a start.
        await server.Services.GetRequiredService<ClubShell.Server.Infrastructure.ClubRepository>().EnsureAsync(
            server.Services.GetRequiredService<ClubShell.Server.Infrastructure.ClubOptions>());
        runner.MigrateDown(2026100401);
        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE clubs SET settings = jsonb_set(settings, '{features}', '{\"callAdmin\": false, \"gpuPanel\": true}')");
        }

        var (settings, config) = (await ScalarAsync("SELECT settings_version FROM clubs"), await ScalarAsync("SELECT config_version FROM clubs"));
        runner.MigrateUp();
        Assert.Equal((settings + 1, config + 1), (await ScalarAsync("SELECT settings_version FROM clubs"), await ScalarAsync("SELECT config_version FROM clubs")));
        Assert.Equal(1, await ScalarAsync("SELECT count(*)::int FROM clubs WHERE settings #>> '{features,callAdmin}' = 'true' AND settings #>> '{features,gpuPanel}' = 'true'"));

        // Switched off after the deploy, it stays off: the next start's migration run does not flip it again.
        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE clubs SET settings = jsonb_set(settings, '{features,callAdmin}', 'false')");
        }

        runner.MigrateUp();
        Assert.Equal(1, await ScalarAsync("SELECT count(*)::int FROM clubs WHERE settings #>> '{features,callAdmin}' = 'false'"));
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
