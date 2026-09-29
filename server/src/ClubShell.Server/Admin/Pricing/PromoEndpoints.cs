using System.Text.Json;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Wallet;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// <c>adminRedeemPromo</c> (slice S5, DESIGN §4.4, D-15): a <c>bonus</c> code credits its <c>value</c> to the client's main
/// balance through the <see cref="Ledger"/> (row type <c>bonus</c>, D-9). One atomic
/// <c>UPDATE promo_codes … WHERE (uses_left IS NULL OR uses_left &gt; 0) AND (expires_at IS NULL OR expires_at &gt; now)</c>
/// takes a use — concurrent redeems queue on the row and re-check, so no more than <c>usesLeft</c> succeed — and the
/// <c>promo_redemptions</c> key (code, client) refuses a second redeem by the same client as <c>exhausted</c>; either
/// failure rolls the whole operation back. Errors in the mock's order: <c>404 user</c>, <c>404 promo</c>, <c>expired</c>,
/// <c>exhausted</c>, <c>discountAtCheckout</c> (no discount at the counter in v1, §5.1). The club row is held
/// <c>FOR KEY SHARE</c> first, so a redeem and <c>PATCH /admin/club</c> (which rewrites the codes under <c>FOR UPDATE</c>
/// of that row) serialize (§4.4). Lock order: idempotency key → club → promo code → wallet → shift. <c>walletUpdated</c>
/// goes out after the commit.
/// </summary>
public static class PromoEndpoints
{
    public static readonly string[] Operations = ["adminRedeemPromo"];

    public static void MapPromoEndpoints(this IEndpointRouteBuilder app) =>
        app.MapApiGroup("/api/v1/admin/promo").WithMetadata(new AuthRequirement(AuthMode.Staff)).MapPost("/redeem", RedeemAsync);

    private static async Task<IResult> RedeemAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, Pushes pushes, TimeProvider clock, ILoggerFactory logs)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminPromoRedeemRequest>(body, "userId", "code");
        var code = AdminInput.Text(r.Code, "code", 32);
        var userId = r.UserId!.Value;
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            await c.ExecuteAsync("SELECT 1 FROM clubs WHERE id = @ClubId FOR KEY SHARE", new { staff.ClubId }, tx);
            var name = await c.QuerySingleOrDefaultAsync<string>(
                "SELECT display_name FROM users WHERE id = @userId AND network_id = @NetworkId AND deleted_at IS NULL AND NOT transient",
                new { userId, staff.NetworkId }, tx)
                ?? throw ApiException.NotFound("user");
            var promo = await c.QuerySingleOrDefaultAsync<PromoRow>(
                $"SELECT {PromoRow.Columns} FROM promo_codes WHERE club_id = @ClubId AND upper(code) = upper(@code) AND deleted_at IS NULL",
                new { staff.ClubId, code }, tx)
                ?? throw ApiException.NotFound("promo");
            var value = await c.QuerySingleOrDefaultAsync<long?>(
                """
                UPDATE promo_codes SET uses_left = uses_left - 1, used = used + 1
                WHERE id = @Id AND kind = 'bonus' AND (uses_left IS NULL OR uses_left > 0) AND (expires_at IS NULL OR expires_at > @now)
                RETURNING value
                """,
                new { promo.Id, now }, tx);
            if (value is null)
            {
                // The row as it is now (a concurrent redeem may have taken the last use while this one waited).
                var current = await c.QuerySingleAsync<PromoRow>($"SELECT {PromoRow.Columns} FROM promo_codes WHERE id = @Id", new { promo.Id }, tx);
                throw ApiException.Validation("code",
                    current.ExpiresAt <= now ? "expired" : current.UsesLeft <= 0 ? "exhausted" : "discountAtCheckout");
            }

            if (await c.ExecuteAsync(
                    """
                    INSERT INTO promo_redemptions (promo_code_id, user_id, staff_id, redeemed_at) VALUES (@Id, @userId, @StaffId, @now)
                    ON CONFLICT DO NOTHING
                    """,
                    new { promo.Id, userId, staff.StaffId, now }, tx) == 0)
            {
                throw ApiException.Validation("code", "exhausted", "This client has already redeemed the code");
            }

            var lineId = Guid.CreateVersion7(now);
            var balance = await Ledger.PostAsync(c, tx, userId, allowOverdraft: false, now,
                new LedgerLine("bonus", value.Value, $"Промокод {promo.Code}", staff.ClubId, StaffId: staff.StaffId, Id: lineId));
            await c.ExecuteAsync(
                "UPDATE promo_redemptions SET op_id = (SELECT op_id FROM ledger_entries WHERE id = @lineId) WHERE promo_code_id = @Id AND user_id = @userId",
                new { lineId, promo.Id, userId }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "promoRedeem", userId, amount: value.Value, detail: $"{name} · {promo.Code}", meta: new { code = promo.Code });
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminPromoRedeemResponse(Money.Uzs(balance))));
        });
        if (!context.Response.Headers.ContainsKey(IdempotencyStore.ReplayedHeader))
        {
            try
            {
                await pushes.WalletAsync(userId);
            }
            catch (NpgsqlException ex)
            {
                // The credit is committed; a lost push only delays the kiosk's balance until its next read.
                logs.CreateLogger(typeof(PromoEndpoints).FullName!).LogWarning(ex, "walletUpdated after a promo redeem failed");
            }
        }

        return result;
    }

    private sealed class PromoRow
    {
        public const string Columns = "id, code, kind, value, uses_left, expires_at";

        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Kind { get; init; } = "";
        public long Value { get; init; }
        public int? UsesLeft { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
    }
}
