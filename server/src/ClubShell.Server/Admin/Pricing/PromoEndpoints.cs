using System.Collections.Concurrent;
using System.Text.Json;
using ClubShell.Contracts.Errors;
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

    /// <summary>Wrong or used-up codes a player may try in <see cref="FailureWindow"/> before <c>429 rateLimited</c>.</summary>
    public const int MaxFailures = 8;

    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    /// <summary>A player's recent failed codes: a code is guessed by nobody typing them one after another.</summary>
    private static readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> Failures = new();

    public static void MapPromoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapApiGroup("/api/v1/admin/promo").WithMetadata(new AuthRequirement(AuthMode.Staff)).MapPost("/redeem", RedeemAsync);

        // D-88: the player's own redeem (the PC's shell through the agent; the same route for the club's mobile app).
        app.MapApiGroup("/api/v1/wallet/{userId:guid}").OwnedByPlayer().MapPost("/promo", PlayerRedeemAsync);
    }

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
            var name = await c.QuerySingleOrDefaultAsync<string>(
                "SELECT display_name FROM users WHERE id = @userId AND network_id = @NetworkId AND deleted_at IS NULL AND NOT transient",
                new { userId, staff.NetworkId }, tx)
                ?? throw ApiException.NotFound("user");
            var redeemed = await RedeemCoreAsync(c, tx, staff.ClubId, userId, code, staff.StaffId, now);
            await Audit.WriteAsync(c, tx, staff, now, "promoRedeem", userId, amount: redeemed.Value, detail: $"{name} · {redeemed.Code}", meta: new { code = redeemed.Code });
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminPromoRedeemResponse(Money.Uzs(redeemed.Balance))));
        });
        if (!context.Response.Headers.ContainsKey(IdempotencyStore.ReplayedHeader))
        {
            await PushAsync(pushes, userId, logs);
        }

        return result;
    }

    /// <summary>
    /// <c>POST /wallet/{userId}/promo {code}</c> (D-88, beyond the contract): the signed-in member redeems a <c>bonus</c>
    /// code of the PC's club themselves — <c>{code, bonus, balance}</c>. The same checks as the desk's; a guest or a
    /// walk-in has no account to credit (<c>403 guest</c>); after <see cref="MaxFailures"/> wrong or used-up codes in
    /// <see cref="FailureWindow"/> the player waits (<c>429 rateLimited</c>), so codes are not guessed from a PC.
    /// </summary>
    private static async Task<IResult> PlayerRedeemAsync(
        HttpContext context, Guid userId, [FromBody] JsonElement body, NpgsqlDataSource db, Pushes pushes, TimeProvider clock, ILoggerFactory logs)
    {
        var clubId = context.Features.GetRequiredFeature<AgentContext>().Pc.ClubId;
        var code = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!.Trim()
            : "";
        if (code.Length == 0)
        {
            throw ApiException.Validation("code", "required");
        }

        if (code.Length > 32)
        {
            throw ApiException.Validation("code", "max");
        }

        var now = clock.GetUtcNow();
        if (RecentFailures(userId, now) >= MaxFailures)
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, ErrorCode.RateLimited, "Too many wrong promo codes, try again later",
                new { reason = "promoAttempts", retryAfterSec = (int)FailureWindow.TotalSeconds });
        }

        (long Balance, long Value, string Code) redeemed;
        try
        {
            await using var c = await db.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            var role = await c.QuerySingleOrDefaultAsync<(string Role, bool Transient)?>(
                "SELECT role, transient FROM users WHERE id = @userId AND deleted_at IS NULL", new { userId }, tx)
                ?? throw ApiException.NotFound("user");
            if (role.Role == "guest" || role.Transient)
            {
                throw ApiException.Forbidden("guest", "A guest has no account to credit a promo code to");
            }

            redeemed = await RedeemCoreAsync(c, tx, clubId, userId, code, staffId: null, now);
            await tx.CommitAsync();
        }
        catch (ApiException e) when (e.Status is StatusCodes.Status404NotFound or StatusCodes.Status400BadRequest)
        {
            Fail(userId, now);
            throw;
        }

        await PushAsync(pushes, userId, logs);
        return Results.Json(new { code = redeemed.Code, bonus = Money.Uzs(redeemed.Value), balance = Money.Uzs(redeemed.Balance) }, ServerJson.Options);
    }

    /// <summary>
    /// The redeem itself, inside the caller's transaction: the club row <c>FOR KEY SHARE</c>, one use taken atomically,
    /// one redemption per client, the bonus through the ledger. Errors: <c>404 promo</c>, <c>400 code</c>
    /// <c>expired</c> / <c>exhausted</c> / <c>discountAtCheckout</c>.
    /// </summary>
    internal static async Task<(long Balance, long Value, string Code)> RedeemCoreAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, Guid userId, string code, Guid? staffId, DateTimeOffset now)
    {
        await c.ExecuteAsync("SELECT 1 FROM clubs WHERE id = @clubId FOR KEY SHARE", new { clubId }, tx);
        var promo = await c.QuerySingleOrDefaultAsync<PromoRow>(
            $"SELECT {PromoRow.Columns} FROM promo_codes WHERE club_id = @clubId AND upper(code) = upper(@code) AND deleted_at IS NULL",
            new { clubId, code }, tx)
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
                INSERT INTO promo_redemptions (promo_code_id, user_id, staff_id, redeemed_at) VALUES (@Id, @userId, @staffId, @now)
                ON CONFLICT DO NOTHING
                """,
                new { promo.Id, userId, staffId, now }, tx) == 0)
        {
            throw ApiException.Validation("code", "exhausted", "This client has already redeemed the code");
        }

        var lineId = Guid.CreateVersion7(now);
        var balance = await Ledger.PostAsync(c, tx, userId, allowOverdraft: false, now,
            new LedgerLine("bonus", value.Value, $"Промокод {promo.Code}", clubId, StaffId: staffId, Id: lineId));
        await c.ExecuteAsync(
            "UPDATE promo_redemptions SET op_id = (SELECT op_id FROM ledger_entries WHERE id = @lineId) WHERE promo_code_id = @Id AND user_id = @userId",
            new { lineId, promo.Id, userId }, tx);
        return (balance, value.Value, promo.Code);
    }

    private static async Task PushAsync(Pushes pushes, Guid userId, ILoggerFactory logs)
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

    private static int RecentFailures(Guid userId, DateTimeOffset now)
    {
        if (!Failures.TryGetValue(userId, out var queue))
        {
            return 0;
        }

        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > FailureWindow)
            {
                queue.Dequeue();
            }

            return queue.Count;
        }
    }

    private static void Fail(Guid userId, DateTimeOffset now)
    {
        var queue = Failures.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            queue.Enqueue(now);
        }
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
