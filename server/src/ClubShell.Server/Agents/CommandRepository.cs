using System.Collections.Concurrent;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Agents;

/// <summary>
/// Server → agent command queue (DESIGN §6.4; port of club-server <c>Data/CommandRepository.cs</c>): at-least-once, the
/// agent acks by id over WS or REST. Pending = not acked, not superseded, not expired, oldest first. Acks overwrite
/// (a repeated ack and the second <c>message</c> ack with <c>ackedAt</c> are fine) and wake <see cref="WaitForAckAsync"/>.
/// </summary>
public sealed class CommandRepository(NpgsqlDataSource db, TimeProvider clock)
{
    private const string Pending = "pc_id = @pcId AND acked_at IS NULL AND superseded_at IS NULL AND (expires_at IS NULL OR expires_at > @now)";

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<CommandAck>> _waiters = new();

    /// <summary>
    /// Inserts a command; when <paramref name="supersedes"/> names a command of the PC that was not delivered yet, it is
    /// marked superseded and never delivered (a delivered one is cancelled by the agent itself, AsyncAPI §7).
    /// </summary>
    public async Task<ServerCommandEnvelope> EnqueueAsync(
        Guid clubId, Guid pcId, ServerCommandType type, JsonElement? payload, Guid? supersedes, Guid? issuedByStaffId, TimeSpan ttl)
    {
        var now = clock.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        var name = type.ToWireName();
        await using var c = await db.OpenConnectionAsync();

        // The id comes from PostgreSQL 18 uuidv7(): sub-millisecond and monotonic within a backend, so "oldest first"
        // (created_at, id) keeps the enqueue order of commands issued within one millisecond (lock → unlock).
        var id = await c.QuerySingleAsync<Guid>(
            """
            WITH inserted AS (
                INSERT INTO agent_commands (id, club_id, pc_id, name, payload, issued_by_staff_id, supersedes, created_at, expires_at)
                VALUES (uuidv7(), @clubId, @pcId, @name, @payload::jsonb, @issuedByStaffId, @supersedes, @now, @expiresAt)
                RETURNING id),
            superseded AS (
                UPDATE agent_commands SET superseded_at = @now
                WHERE id = @supersedes AND pc_id = @pcId AND delivered_at IS NULL AND acked_at IS NULL)
            SELECT id FROM inserted
            """,
            new { clubId, pcId, name, payload = payload is { } p ? ServerJson.Jsonb(p.GetRawText()) : null, issuedByStaffId, supersedes, now, expiresAt = now + ttl });
        return new ServerCommandEnvelope(id, now, name, payload, null, supersedes, now + ttl);
    }

    public async Task<IReadOnlyList<ServerCommandEnvelope>> PendingAsync(Guid pcId)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<Row>(
            $"""
            SELECT id, name, payload::text AS payload, supersedes, created_at, expires_at FROM agent_commands
            WHERE {Pending} ORDER BY created_at, id
            """,
            new { pcId, now = clock.GetUtcNow() });
        return rows.Select(r => new ServerCommandEnvelope(
            r.Id, r.CreatedAt, r.Name, r.Payload is null ? null : JsonElement.Parse(r.Payload), null, r.Supersedes, r.ExpiresAt)).ToList();
    }

    /// <summary>
    /// Pending commands except <paramref name="liveOnWs"/> (N1: those the current live socket already carries), so it
    /// may be less than <c>GET /commands</c> returns. The vendored contract (AsyncAPI §7, <c>HeartbeatResponse</c>) counts
    /// WS-sent commands too; the wording change is DESIGN §12.2 item 8.
    /// </summary>
    public async Task<int> PendingCountAsync(Guid pcId, IReadOnlyCollection<Guid> liveOnWs)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM agent_commands WHERE {Pending} AND NOT (id = ANY(@liveOnWs))",
            new { pcId, now = clock.GetUtcNow(), liveOnWs = liveOnWs.ToArray() });
    }

    public async Task MarkDeliveredAsync(IReadOnlyCollection<Guid> ids)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE agent_commands SET delivered_at = coalesce(delivered_at, @now) WHERE id = ANY(@ids)",
            new { ids = ids.ToArray(), now = clock.GetUtcNow() });
    }

    /// <summary>Stores the ack; <c>false</c> when the command does not exist or belongs to another PC (<c>404 what=command</c>).</summary>
    public async Task<bool> AckAsync(Guid pcId, Guid commandId, CommandAck ack)
    {
        await using var c = await db.OpenConnectionAsync();
        var found = await c.ExecuteAsync(
            "UPDATE agent_commands SET acked_at = @now, ack = @ack::jsonb WHERE id = @commandId AND pc_id = @pcId",
            new { pcId, commandId, now = clock.GetUtcNow(), ack = ServerJson.Jsonb(JsonDefaults.Serialize(ack)) }) == 1;
        if (found && _waiters.TryGetValue(commandId, out var waiter))
        {
            waiter.TrySetResult(ack);
        }

        return found;
    }

    /// <summary>
    /// The command's ack, waiting up to <paramref name="timeout"/>; null on timeout (<c>adminCommand</c>, S4). The waiter
    /// is registered before the stored ack is read, so an ack arriving in between is not lost.
    /// </summary>
    public async Task<CommandAck?> WaitForAckAsync(Guid commandId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = _waiters.GetOrAdd(commandId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await using (var c = await db.OpenConnectionAsync(cancellationToken))
            {
                var stored = await c.QuerySingleOrDefaultAsync<string?>("SELECT ack::text FROM agent_commands WHERE id = @commandId AND acked_at IS NOT NULL", new { commandId });
                if (stored is not null)
                {
                    return JsonDefaults.Deserialize<CommandAck>(stored);
                }
            }

            return await waiter.Task.WaitAsync(timeout, clock, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _waiters.TryRemove(commandId, out _);
        }
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? Payload { get; init; }
        public Guid? Supersedes { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
    }
}
