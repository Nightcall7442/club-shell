using ClubShell.Server.Infrastructure;
using Npgsql;

namespace ClubShell.Server.Realtime;

/// <summary>
/// Guard against an accidental second instance (DESIGN §6.8, D-20): the connection registry and ack waits live in
/// memory, so two instances would split the PCs between them. At start <c>pg_try_advisory_lock(CSHub)</c> is taken on a
/// dedicated connection held for the process life; when another instance holds it the start fails (logged Critical).
/// Registered with the background workers (<c>Workers:Enabled</c>): test hosts share nothing and run without it.
/// </summary>
public sealed class HubLock(NpgsqlDataSource db, ILogger<HubLock> logger) : IHostedService, IAsyncDisposable
{
    private NpgsqlConnection? _connection;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
        command.Parameters.AddWithValue(AdvisoryLocks.Hub);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            await connection.DisposeAsync();
            logger.LogCritical("Another server instance holds the CSHub lock on this database; this one does not start");
            throw new InvalidOperationException("Another server instance is running against this database (advisory lock CSHub)");
        }

        _connection = connection;
    }

    public Task StopAsync(CancellationToken cancellationToken) => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            // Explicitly: a pooled connection keeps its session locks until the pool reuses it.
            await using (var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", _connection))
            {
                unlock.Parameters.AddWithValue(AdvisoryLocks.Hub);
                await unlock.ExecuteNonQueryAsync();
            }

            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
