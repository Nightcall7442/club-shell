using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>Idempotency-Key store (DESIGN §7.1): one effect per key, the concurrent twin waits and replays, errors are not stored.</summary>
public sealed class IdempotencyTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static readonly byte[] Hash = [1, 2, 3];

    private IdempotencyStore Store => server.Services.GetRequiredService<IdempotencyStore>();

    [Fact]
    public async Task Concurrent_twin_waits_for_the_first_commit_and_replays_its_answer()
    {
        var key = Guid.NewGuid();
        var calls = 0;
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        async Task<IdempotentResult> Handler(NpgsqlConnection c, NpgsqlTransaction tx)
        {
            Interlocked.Increment(ref calls);
            inside.TrySetResult();
            await release.Task;
            return new IdempotentResult(201, JsonElement.Parse("""{"sessionId":"s-1","charged":1008000}"""));
        }

        var first = Store.ExecuteAsync("pc:1", "POST", "/api/v1/sessions", key, Hash, Handler);
        await inside.Task;
        var second = Store.ExecuteAsync("pc:1", "POST", "/api/v1/sessions", key, [9], Handler);

        // The twin blocks on the unique index of the uncommitted key row, not on anything in the test.
        await WaitForLockWaitAsync();
        Assert.False(second.IsCompleted);

        release.SetResult();
        var a = await first;
        var b = await second;
        Assert.Equal(1, calls);
        Assert.False(a.Replayed);
        Assert.True(b.Replayed);
        Assert.Equal(201, b.Status);
        Assert.Equal(1008000, b.Body!.Value.GetProperty("charged").GetInt64());
    }

    [Fact]
    public async Task Errors_are_not_stored_and_a_repeat_runs_again()
    {
        var key = Guid.NewGuid();
        var calls = 0;
        Task<IdempotentResult> Fails(NpgsqlConnection c, NpgsqlTransaction tx)
        {
            calls++;
            throw new ApiException(404, ErrorCode.NotFound, "Session not found");
        }

        Task<IdempotentResult> Conflicts(NpgsqlConnection c, NpgsqlTransaction tx)
        {
            calls++;
            return Task.FromResult(new IdempotentResult(409, null));
        }

        Task<IdempotentResult> Succeeds(NpgsqlConnection c, NpgsqlTransaction tx)
        {
            calls++;
            return Task.FromResult(new IdempotentResult(204, null));
        }

        await Assert.ThrowsAsync<ApiException>(() => Store.ExecuteAsync("pc:2", "POST", "/api/v1/sessions/x/events", key, Hash, Fails));
        Assert.Equal(409, (await Store.ExecuteAsync("pc:2", "POST", "/api/v1/sessions/x/events", key, Hash, Conflicts)).Status);
        var done = await Store.ExecuteAsync("pc:2", "POST", "/api/v1/sessions/x/events", key, Hash, Succeeds);
        var replay = await Store.ExecuteAsync("pc:2", "POST", "/api/v1/sessions/x/events", key, Hash, Succeeds);

        Assert.Equal(3, calls);
        Assert.False(done.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(204, replay.Status);
        Assert.Null(replay.Body);
    }

    [Fact]
    public async Task Handler_writes_roll_back_with_the_error()
    {
        var key = Guid.NewGuid();
        await Assert.ThrowsAsync<ApiException>(() => Store.ExecuteAsync("club:1", "POST", "/api/v1/admin/wallet/topup", key, Hash, async (c, tx) =>
        {
            await c.ExecuteAsync("INSERT INTO networks (id, name) VALUES (@id, 'rolled back')", new { id = key }, tx);
            throw new ApiException(402, ErrorCode.InsufficientFunds, "Balance too low");
        }));

        await using var check = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT count(*) FROM networks WHERE id = @key", new { key }));
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT count(*) FROM idempotency_keys WHERE key = @key", new { key }));
    }

    [Fact]
    public async Task Keys_are_scoped_by_principal()
    {
        var key = Guid.NewGuid();
        var calls = 0;
        Task<IdempotentResult> Handler(NpgsqlConnection c, NpgsqlTransaction tx)
        {
            calls++;
            return Task.FromResult(new IdempotentResult(200, JsonElement.Parse("{}")));
        }

        Assert.False((await Store.ExecuteAsync("pc:a", "POST", "/api/v1/sessions", key, Hash, Handler)).Replayed);
        Assert.False((await Store.ExecuteAsync("pc:b", "POST", "/api/v1/sessions", key, Hash, Handler)).Replayed);
        Assert.Equal(2, calls);
    }

    private async Task WaitForLockWaitAsync()
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        for (var i = 0; i < 100; i++)
        {
            if (await c.ExecuteScalarAsync<int>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("the concurrent twin never waited on a lock");
    }
}
