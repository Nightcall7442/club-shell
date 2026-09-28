using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>
/// The server in memory over its own temporary database (DESIGN §10.0): <c>CREATE DATABASE clubshell_test_&lt;guid&gt;</c>
/// on the PostgreSQL from <c>CLUBSHELL_TEST_PG</c>, JWT key in a temp directory, workers off, <see cref="FakeClock"/> in DI.
/// Use <see cref="Http"/>: every response it returns is validated against the contract.
/// </summary>
public class ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ClubKey = "test-club-key";

    private readonly string _database = "clubshell_test_" + Guid.NewGuid().ToString("N");
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "clubshell-test-" + Guid.NewGuid().ToString("N"));
    private HttpClient? _http;

    /// <summary>Overrides on top of the defaults below, applied last.</summary>
    public Dictionary<string, string> Settings { get; } = new();

    public FakeClock Clock { get; } = new();

    /// <summary><c>operationId status</c> pairs that got a contract-valid response through <see cref="Http"/>.</summary>
    public ConcurrentDictionary<string, bool> Covered { get; } = new();

    /// <summary>Client whose responses pass through <see cref="ContractValidatingHandler"/>.</summary>
    public HttpClient Http => _http ??= CreateDefaultClient(new ContractValidatingHandler(this));

    public virtual async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(TestDatabases.AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {_database}", connection);
        await command.ExecuteNonQueryAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        _http?.Dispose();
        await DisposeAsync();
        await TestDatabases.DropAsync(_database);
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Club", TestDatabases.ConnectionTo(_database));
        builder.UseSetting("Auth:SigningKeyPath", Path.Combine(_dataDir, "jwt-signing-key.pem"));
        builder.UseSetting("Club:EnrollmentKey", ClubKey);
        builder.UseSetting("Club:AutoApprovePcs", "true");
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("Seed:Dev", "true");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }
}

public static class TestDatabases
{
    /// <summary>Admin connection string: <c>CLUBSHELL_TEST_PG</c>, else a local PostgreSQL as <c>postgres</c>.</summary>
    public static string AdminConnection { get; } =
        Environment.GetEnvironmentVariable("CLUBSHELL_TEST_PG") is { Length: > 0 } value ? value : "Host=localhost;Username=postgres";

    public static string ConnectionTo(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnection) { Database = database }.ConnectionString;

    /// <summary>
    /// Drops a temporary database. FORCE cannot terminate autovacuum without superuser rights (42501); it ends soon on
    /// its own, so retry (port of club-server <c>TestDatabases</c>).
    /// </summary>
    public static async Task DropAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(AdminConnection);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (PostgresException ex) when (attempt < 20 && ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ObjectInUse)
            {
                await Task.Delay(250);
            }
        }
    }
}

/// <summary>Settable clock (DESIGN §10.0): expiry, windows and ticks are tested without waiting. Starts at the real now.</summary>
public sealed class FakeClock : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
        }
    }
}
