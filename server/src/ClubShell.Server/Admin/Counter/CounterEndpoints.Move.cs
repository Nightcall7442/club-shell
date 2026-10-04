using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using ClubShell.Server.Sessions.Billing;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace ClubShell.Server.Admin;

public static partial class CounterEndpoints
{
    /// <summary>
    /// <c>POST /admin/sessions/move</c> (beyond the contract, D-59..D-61): «Пересадить» — the open session of <c>fromPcId</c>
    /// (or <c>sessionId</c>, which must be it) goes to <c>toPcId</c> with its time and money; no ledger row is written and the
    /// clock keeps running during the walk. Staff only, <c>Idempotency-Key</c> required with the strict body check (D-70).
    /// <c>fromPcId</c> is required: a retry after a lost answer, or a stale map, finds no session there (<c>409 sessionMoved</c>)
    /// instead of moving it again. Both PCs' advisory locks are taken in ascending id order before any row lock (§4.4,
    /// D-69); then the session row, then the target: live in the club (<c>404 pc</c>), not in maintenance (<c>403
    /// policyDenied pcMaintenance</c>), online (<c>409 targetOffline</c>), free (<c>409 pcBusy</c>) and without a session of its
    /// own agent the server cannot see (<c>409 targetHasLocalSession</c>: a non-empty offline queue, or a reported current
    /// session the server does not know — agent 1.0.16 keeps only an unsent offline session over the server's). Prepaid keeps
    /// its tariff where its zones allow the target zone, else the desk names an hourly tariff valid there (<c>409 tariffZone
    /// {zone}</c> when missing); postpaid keeps its frozen price (<c>400 tariffId postpaid</c>). The target's other players are
    /// signed out (<c>seatTaken</c>), the player's token on the old PC is deleted; after the commit the session goes to the
    /// target, its «ended view» to the old PC, then <c>userRevoked seatMoved</c>. The player signs in on the target.
    /// </summary>
    private static async Task<IResult> MoveAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, PcRepository pcs, AgentSocketHub hub,
        AgentOptions agents)
    {
        var staff = StaffOnly(context);
        var r = Api.Read<AdminMoveRequest>(body, "fromPcId", "toPcId");
        if (r.FromPcId == r.ToPcId)
        {
            throw ApiException.Validation("toPcId", "same");
        }

        var from = await LivePcAsync(pcs, staff, r.FromPcId!.Value);
        var to = await LivePcAsync(pcs, staff, r.ToPcId!.Value);
        var effects = new SessionEffects();
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: true, body, async (c, tx) =>
        {
            var now = sessions.Clock.GetUtcNow();
            foreach (var pcId in new[] { from.Id, to.Id }.Order())
            {
                await AdvisoryLocks.PcAsync(c, tx, pcId);
            }

            var id = r.SessionId ?? await c.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM sessions WHERE pc_id = @Id AND state <> 'ended'", new { from.Id }, tx) ?? throw SessionService.Conflict("sessionMoved");
            var s = await SessionService.LockAsync(c, tx, id);
            if (s is null || s.ClubId != staff.ClubId)
            {
                throw ApiException.NotFound("session");
            }

            if (s.Ended || s.PcId != from.Id)
            {
                throw SessionService.Conflict("sessionMoved");
            }

            if (s.State == "ending")
            {
                throw SessionService.Conflict("sessionEnding");
            }

            var target = await c.QuerySingleOrDefaultAsync<MoveTarget>(
                """
                SELECT zone, maintenance, last_heartbeat_at, coalesce((last_heartbeat ->> 'offlineQueue')::int, 0) AS offline_queue,
                       last_heartbeat ->> 'currentSessionId' AS current_session_id
                FROM pcs WHERE id = @Id AND club_id = @ClubId AND deleted_at IS NULL
                FOR KEY SHARE
                """,
                new { to.Id, staff.ClubId }, tx)
                ?? throw ApiException.NotFound("pc");
            if (target.Maintenance)
            {
                throw SessionService.PolicyDenied("pcMaintenance");
            }

            if (!hub.IsConnected(to.Id) && (target.LastHeartbeatAt is not { } beat || now - beat >= TimeSpan.FromSeconds(agents.OfflineAfterSec)))
            {
                throw SessionService.Conflict("targetOffline");
            }

            if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM sessions WHERE pc_id = @Id AND state <> 'ended')", new { to.Id }, tx))
            {
                throw SessionService.PcBusy(to.Id);
            }

            if (target.OfflineQueue > 0 || (Guid.TryParse(target.CurrentSessionId, out var reported) && !await c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM sessions WHERE id = @reported OR client_session_id = @reported)", new { reported }, tx)))
            {
                throw SessionService.Conflict("targetHasLocalSession");
            }

            // D-60: postpaid keeps its frozen price; prepaid keeps its tariff where it is valid, else an hourly one of the zone.
            var tariffNow = (await SessionService.TariffAsync(c, tx, s.ClubId, s.TariffId, withDeleted: true))!;
            TariffRow? tariff = null;
            if (!s.IsPrepaid)
            {
                if (r.TariffId is not null)
                {
                    throw ApiException.Validation("tariffId", "postpaid");
                }
            }
            else if (r.TariffId is { } wanted)
            {
                tariff = await SessionService.TariffAsync(c, tx, s.ClubId, wanted) ?? throw ApiException.NotFound("tariff");
                if (tariff.IsPackage)
                {
                    throw ApiException.Validation("tariffId", "package");
                }

                if (SessionService.TariffRule(target.Zone, tariff, await SessionService.ClubAsync(c, tx, s.ClubId), now) is { } rule)
                {
                    throw SessionService.PolicyDenied(rule);
                }
            }
            else if (tariffNow.Zones.Length > 0 && !tariffNow.Zones.Contains(target.Zone, StringComparer.OrdinalIgnoreCase))
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: tariffZone", new { reason = "tariffZone", zone = target.Zone });
            }

            var tariffChanged = tariff is not null && tariff.Id != s.TariffId;
            var departed = SessionService.EndedView(s, from.Id, now, s.Used(now));
            var moved = await sessions.MoveAsync(c, tx, s, to, tariffChanged ? tariff : null, now, staff);
            foreach (var other in await c.QueryAsync<Guid>(
                "DELETE FROM user_tokens WHERE pc_id = @pcId AND user_id <> @UserId RETURNING user_id", new { pcId = to.Id, moved.UserId }, tx))
            {
                effects.RevokedBefore.Add((other, to.Id, "seatTaken"));
            }

            await c.ExecuteAsync("DELETE FROM user_tokens WHERE pc_id = @pcId AND user_id = @UserId", new { pcId = from.Id, moved.UserId }, tx);
            var session = moved.ToWire(now);
            effects.Sessions.Add(session);
            effects.Departed.Add(departed);
            effects.RevokedAfter.Add((moved.UserId, from.Id, "seatMoved"));

            var who = await c.QuerySingleAsync<(string DisplayName, string Role)>("SELECT display_name, role FROM users WHERE id = @UserId", new { moved.UserId }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "sessionMove", moved.UserId, to.Id, 0, $"{from.Name} → {to.Name}",
                new
                {
                    sessionId = moved.Id, fromPcId = from.Id, fromPc = from.Name, toPcId = to.Id, toPc = to.Name, tariffFrom = tariffNow.Name,
                    tariffTo = tariffChanged ? tariff!.Name : tariffNow.Name, prepaid = moved.IsPrepaid, secondsLeft = session.SecondsLeft,
                });
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminMoveResponse(
                session, new AdminMovePc(from.Id, from.Name), new AdminMovePc(to.Id, to.Name), tariffChanged,
                new AdminSessionUser(moved.UserId, who.DisplayName, who.Role), SignedIn: false)));
        }, strictBody: true);
        await sessions.PublishAsync(effects);
        return result;
    }

    /// <summary>What a move checks of its target PC (D-59).</summary>
    private sealed class MoveTarget
    {
        public string Zone { get; init; } = "";
        public bool Maintenance { get; init; }
        public DateTimeOffset? LastHeartbeatAt { get; init; }
        public int OfflineQueue { get; init; }
        public string? CurrentSessionId { get; init; }
    }
}
