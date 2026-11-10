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

        // M0010 (and M0011 above it): the staff PIN failures and the trusted browsers go and come back (D-76).
        string[] login = ["staff_devices", "staff_login_failures"];
        runner.MigrateDown(2026100501);
        Assert.Empty((await TablesAsync()).Intersect(login));
        runner.MigrateUp();
        Assert.Equal(login, (await TablesAsync()).Intersect(login).Order());
        Assert.Equal(2, await ScalarAsync("SELECT count(*)::int FROM pg_indexes WHERE indexname IN ('staff_login_failures_source', 'staff_login_failures_club')"));

        // M0011 alone: the re-enroll window of the re-registration proof (D-78) goes and comes back; M0010 stays.
        const string reenroll = "SELECT count(*)::int FROM information_schema.columns WHERE table_name = 'pcs' AND column_name = 'reenroll_until'";
        runner.MigrateDown(2026101201);
        Assert.Equal(0, await ScalarAsync(reenroll));
        Assert.Equal(login, (await TablesAsync()).Intersect(login).Order());
        runner.MigrateUp();
        Assert.Equal(1, await ScalarAsync(reenroll));
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

    /// <summary>
    /// M0017: the open postpaid sessions get the limit the server applied to them before — a member the club's
    /// <c>memberDebtLimit</c> or 0, a guest the club's <c>guestDebtLimit</c> or none (no cap); prepaid and ended ones none.
    /// </summary>
    [Fact]
    public async Task M0017_gives_open_postpaid_sessions_the_limit_they_had()
    {
        using var scope = server.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        await server.Services.GetRequiredService<ClubShell.Server.Infrastructure.ClubRepository>().EnsureAsync(
            server.Services.GetRequiredService<ClubShell.Server.Infrastructure.ClubOptions>());
        runner.MigrateDown(2026110501);
        var salt = Guid.NewGuid().ToString("N")[..8];
        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync(
                """
                UPDATE clubs SET settings = jsonb_set(settings, '{limits}', '{"memberDebtLimit": 70000}');
                INSERT INTO tariffs (id, club_id, name, price_per_hour) SELECT md5(@salt || 't')::uuid, id, 'M0017', 1200000 FROM clubs LIMIT 1;
                INSERT INTO pcs (id, club_id, number, name) SELECT md5(@salt || 'pc' || i)::uuid, id, 900 + i, 'm17 ' || i FROM clubs, generate_series(1, 4) i LIMIT 4;
                INSERT INTO users (id, network_id, username, display_name, role)
                SELECT md5(@salt || 'u' || i)::uuid, network_id, 'm17-' || @salt || '-' || i, 'm17', CASE WHEN i = 2 THEN 'guest' ELSE 'member' END
                FROM clubs, generate_series(1, 4) i LIMIT 4;
                INSERT INTO sessions (id, club_id, pc_id, user_id, tariff_id, state, is_prepaid, origin, started_at, ended_at, running_since,
                                      last_transition_at, price_per_hour_snapshot, day_pct, discount_pct, created_at, updated_at)
                SELECT md5(@salt || 's' || i)::uuid, cl.id, md5(@salt || 'pc' || i)::uuid, md5(@salt || 'u' || i)::uuid, md5(@salt || 't')::uuid,
                       CASE WHEN i = 4 THEN 'ended' ELSE 'active' END, i = 3, 'kiosk', now(), CASE WHEN i = 4 THEN now() END, now(), now(), 1200000, 100, 0, now(), now()
                FROM (SELECT id FROM clubs LIMIT 1) cl, generate_series(1, 4) i;
                """,
                new { salt });
        }

        runner.MigrateUp();
        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            // 1 a member, 2 a guest (no guestDebtLimit: no cap), 3 prepaid, 4 ended.
            Assert.Equal("1:70000 2:- 3:- 4:-", await c.ExecuteScalarAsync<string>(
                """
                SELECT string_agg(i || ':' || coalesce((SELECT debt_limit::text FROM sessions WHERE id = md5(@salt || 's' || i)::uuid), '-'), ' ' ORDER BY i)
                FROM generate_series(1, 4) i
                """,
                new { salt }));
            await c.ExecuteAsync(
                "UPDATE sessions SET state = 'ended', ended_at = now() WHERE id IN (SELECT md5(@salt || 's' || i)::uuid FROM generate_series(1, 3) i); UPDATE clubs SET settings = settings - 'limits'",
                new { salt });
        }
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
