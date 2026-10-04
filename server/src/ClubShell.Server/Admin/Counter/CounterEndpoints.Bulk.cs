using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Users;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

public static partial class CounterEndpoints
{
    private static readonly string[] BulkKinds = ["message", "lock", "unlock", "reboot", "shutdown"];

    /// <summary>
    /// <c>POST /admin/pcs/commands</c> (beyond the contract, D-65): one command — <c>message | lock | unlock | reboot |
    /// shutdown</c> — to up to 100 PCs, each answered on its own (<c>{batchId, results[]}</c>). <c>Idempotency-Key</c> required
    /// with the strict body check (D-70); a replay answers the stored results as they were before the acks (sent commands read
    /// <c>queued</c>). One transaction, one savepoint per PC: a refusal on one PC rolls back only that PC (<c>failed</c>, the
    /// refusal in <c>ack.error</c>). PCs not in the club are <c>skipped notFound</c>; an offline PC is skipped for lock, reboot
    /// and shutdown (a queued one would hit the next player within the command's 10 minutes) — a message or an unlock stays
    /// queued. A busy PC is locked as it is (the clock runs, D-12), but reboot and shutdown skip it (<c>sessionOpen</c>) unless
    /// <c>includeBusy</c>: then its session is ended first exactly as the desk's «Завершить» ends it (the journal entry, the
    /// refund, the guest signed out, <c>endSession</c> queued before the power command), so the agent's own end before
    /// powering off gets <c>409 sessionNotActive</c> and nothing is refunded twice. With <c>sessionIds</c> (what the desk's
    /// confirm listed) only those sessions are ended: a player who sat down on a selected PC after the confirm is
    /// <c>skipped sessionOpen</c>, never ended unseen. The busy PCs' advisory locks are taken in
    /// ascending id order before any row lock (§4.4). Each PC sent to gets a <c>pcCommand</c> journal entry (<c>meta {kind,
    /// batchId}</c>). After the commit the commands go out and the acks of the connected PCs are awaited together, up to
    /// <see cref="AgentOptions.AckWaitSec"/>: <c>done</c> (ok), <c>failed</c> (refused), <c>noAnswer</c>, <c>queued</c> (offline).
    /// </summary>
    private static async Task<IResult> CommandsAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, PcRepository pcs, CommandDispatcher dispatcher,
        CommandRepository commands, AgentSocketHub hub, AgentOptions agents, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminBulkCommandRequest>(body, "pcIds", "kind");
        var ids = r.PcIds!;
        if (ids.Count == 0 || ids.Count > 100)
        {
            throw ApiException.Validation("pcIds", ids.Count == 0 ? "required" : "max");
        }

        if (ids.Distinct().Count() != ids.Count)
        {
            throw ApiException.Validation("pcIds", "duplicate");
        }

        var kind = BulkKinds.Contains(r.Kind, StringComparer.Ordinal) ? r.Kind! : throw ApiException.Validation("kind", "unknown");
        if (r.Text is { Length: > 500 })
        {
            throw ApiException.Validation("text", "max");
        }

        var text = string.IsNullOrWhiteSpace(r.Text) ? null : r.Text;
        if (kind == "message" && text is null)
        {
            throw ApiException.Validation("text", "required");
        }

        var level = r.Level switch
        {
            null or "info" => NotificationLevel.Info,
            "warning" => NotificationLevel.Warning,
            _ => throw ApiException.Validation("level", "enum"),
        };
        var power = kind is "reboot" or "shutdown";
        if (r.IncludeBusy == true && !power)
        {
            throw ApiException.Validation("includeBusy", "kind");
        }

        var includeBusy = r.IncludeBusy ?? false;
        if (r.SessionIds is { } listed && (!includeBusy || listed.Count > 100))
        {
            throw ApiException.Validation("sessionIds", includeBusy ? "max" : "includeBusy");
        }

        // The sessions the desk's confirm listed: a player who sat down on a selected PC after it is not ended.
        var endable = r.SessionIds?.ToHashSet();
        var hall = (await pcs.ListAsync(staff.ClubId)).ToDictionary(p => p.Id);
        var effects = new SessionEffects();
        var sent = new List<(int Index, Guid PcId, ServerCommandEnvelope? EndSession, ServerCommandEnvelope Command)>();
        var results = new List<AdminBulkCommandResult>();
        var stored = await store.ExecuteHttpResultAsync(context, ShiftEndpoints.Principal(staff), keyRequired: true, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var batchId = Guid.CreateVersion7(now);
            bool Offline(PcRow pc) => !hub.IsConnected(pc.Id) && (pc.LastHeartbeatAt is not { } at || now - at >= TimeSpan.FromSeconds(agents.OfflineAfterSec));

            // The PCs whose sessions may be ended, locked before any row (a move or a desk end takes the same locks); a session
            // opened after this read is left alone (sessionOpen), so no PC lock is ever taken after a row lock.
            var locked = new HashSet<Guid>();
            if (power && includeBusy)
            {
                var busy = await c.QueryAsync<Guid>(
                    "SELECT pc_id FROM sessions WHERE pc_id = ANY(@ids) AND state <> 'ended' AND (@all OR id = ANY(@listed))",
                    new { ids = ids.Where(hall.ContainsKey).ToArray(), all = endable is null, listed = endable?.ToArray() ?? [] }, tx);
                foreach (var pcId in busy.Where(pcId => !Offline(hall[pcId])).Order())
                {
                    await AdvisoryLocks.PcAsync(c, tx, pcId);
                    locked.Add(pcId);
                }
            }

            foreach (var id in ids)
            {
                if (!hall.TryGetValue(id, out var pc))
                {
                    results.Add(new AdminBulkCommandResult(id, null, "skipped", "notFound", null, null));
                    continue;
                }

                if (kind is "lock" or "reboot" or "shutdown" && Offline(pc))
                {
                    results.Add(new AdminBulkCommandResult(id, pc.Name, "skipped", "offline", null, null));
                    continue;
                }

                var open = power ? await SessionService.OpenOfPcAsync(c, id, tx) : null;
                if (open is not null && (!locked.Contains(id) || endable?.Contains(open.Id) == false))
                {
                    results.Add(new AdminBulkCommandResult(id, pc.Name, "skipped", "sessionOpen", null, null));
                    continue;
                }

                var own = new SessionEffects();
                await c.ExecuteAsync("SAVEPOINT bulk_pc", transaction: tx);
                try
                {
                    AdminBulkEnded? ended = null;
                    ServerCommandEnvelope? endSession = null;
                    if (open is not null && await SessionService.LockAsync(c, tx, open.Id) is { Ended: false } s && s.PcId == id)
                    {
                        var end = await DeskEndCoreAsync(c, tx, staff, sessions, s, now, own);
                        // endSession goes before the power command: queued here, in the PC's order, not after the commit.
                        foreach (var (clubId, pcId, command) in own.Commands)
                        {
                            endSession = await dispatcher.QueueAsync(tx, clubId, pcId, command, issuedByStaffId: staff.StaffId);
                        }

                        own.Commands.Clear();
                        ended = new AdminBulkEnded(s.Id, end.User!, end.Charged, end.Refunded!.Value);
                    }

                    var supersedes = kind != "unlock" ? null : await c.QuerySingleOrDefaultAsync<Guid?>(
                        "SELECT id FROM agent_commands WHERE pc_id = @id AND name = 'lock' AND acked_at IS NULL AND superseded_at IS NULL ORDER BY created_at DESC, id DESC LIMIT 1",
                        new { id }, tx);
                    var queued = await dispatcher.QueueAsync(tx, pc.ClubId, id, BuildCommand(kind, text, level), supersedes, staff.StaffId);
                    await Audit.WriteAsync(c, tx, staff, now, "pcCommand", pcId: id, detail: $"{pc.Name} · {kind}", meta: new { kind, batchId });
                    await c.ExecuteAsync("RELEASE SAVEPOINT bulk_pc", transaction: tx);
                    Merge(effects, own);
                    sent.Add((results.Count, id, endSession, queued));
                    results.Add(new AdminBulkCommandResult(id, pc.Name, "queued", null, null, ended));
                }
                catch (Exception ex) when (ex is ApiException or PostgresException)
                {
                    await c.ExecuteAsync("ROLLBACK TO SAVEPOINT bulk_pc", transaction: tx);
                    var error = ex is ApiException api ? IpcError.Of(api.Code, api.Message) : IpcError.Of(ErrorCode.Internal, "The command could not be queued");
                    results.Add(new AdminBulkCommandResult(id, pc.Name, "failed", null, new CommandAck(false, error), null));
                }
            }

            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminBulkCommandResponse(batchId, results)));
        }, strictBody: true);
        if (stored.Replayed)
        {
            return IdempotencyStore.ToHttp(stored);
        }

        // The session ends first (endSession, then the player's sign-out), then the commands; the connected PCs are waited for together.
        foreach (var (_, pcId, endSession, _) in sent)
        {
            if (endSession is not null)
            {
                await dispatcher.SendAsync(pcId, endSession);
            }
        }

        await sessions.PublishAsync(effects);
        var waits = new List<Task>();
        foreach (var (index, pcId, _, command) in sent)
        {
            var online = hub.IsConnected(pcId);
            await dispatcher.SendAsync(pcId, command);
            if (online)
            {
                waits.Add(WaitAsync(index, command.Id));
            }
        }

        await Task.WhenAll(waits);
        var batch = stored.Body!.Value.GetProperty("batchId").GetGuid();
        return AdminJson.Ok(new AdminBulkCommandResponse(batch, results));

        async Task WaitAsync(int index, Guid commandId)
        {
            var ack = await commands.WaitForAckAsync(commandId, TimeSpan.FromSeconds(agents.AckWaitSec), context.RequestAborted);
            lock (results)
            {
                results[index] = results[index] with
                {
                    Outcome = ack is null ? "noAnswer" : ack.Ok ? "done" : "failed",
                    Ack = ack ?? new CommandAck(false, IpcError.Of(ErrorCode.Timeout, "No acknowledgement from the PC in time")),
                };
            }
        }
    }

    /// <summary>What one PC's savepoint produced, kept once the savepoint is released.</summary>
    private static void Merge(SessionEffects into, SessionEffects from)
    {
        into.Sessions.AddRange(from.Sessions);
        into.Wallets.UnionWith(from.Wallets);
        into.Commands.AddRange(from.Commands);
        into.Opened.AddRange(from.Opened);
        into.RevokedBefore.AddRange(from.RevokedBefore);
        into.RevokedAfter.AddRange(from.RevokedAfter);
        into.Departed.AddRange(from.Departed);
    }
}
