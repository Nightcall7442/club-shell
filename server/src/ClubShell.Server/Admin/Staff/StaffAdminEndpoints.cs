using System.Text.Json;
using System.Text.RegularExpressions;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The owner's staff editor (slice S5, DESIGN §3.5): <c>adminStaff</c> lists every member of the club, disabled ones too,
/// never a PIN; <c>adminAddStaff</c> creates an active member; <c>adminUpdateStaff</c> renames, disables or re-PINs one. A
/// PIN is 4–8 ASCII digits and unique in the network (<c>staff_network_id_pin_hmac_key</c> on its peppered HMAC) — taken is
/// <c>400 pin taken</c> on create and on change alike (§3.5; the contract's open question on PATCH). Disabling takes effect
/// at once: every token of the member is revoked with it (a later re-enable does not revive them), and the token check
/// reads <c>staff.active</c> on each request anyway. Nobody disables themselves (<c>400 active self</c>) and the club keeps
/// one active owner at least (<c>400 active lastOwner</c>, decided under a lock on the active owners — the <c>ck_</c> key
/// has no member to match "self", and two owners can disable each other at once).
/// </summary>
public static partial class StaffAdminEndpoints
{
    public static readonly string[] Operations = ["adminStaff", "adminAddStaff", "adminUpdateStaff"];

    public static void MapStaffAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/staff").WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapGet("", ListAsync);
        api.MapPost("", AddAsync);
        api.MapPatch("/{id}", UpdateAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext context, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        await using var c = await db.OpenConnectionAsync();
        var items = await c.QueryAsync<(Guid Id, string Name, string Role, bool Active)>(
            "SELECT id, name, role, active FROM staff WHERE club_id = @ClubId ORDER BY created_at, id", new { staff.ClubId });
        return AdminJson.Ok(new AdminStaffList(items.Select(s => new AdminStaffMember(s.Id.ToString(), s.Name, s.Role, s.Active)).ToList()));
    }

    private static async Task<IResult> AddAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, StaffTokens tokens, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminStaffCreateRequest>(body, "name", "role", "pin");
        var name = AdminInput.Text(r.Name, "name", 64);
        var role = r.Role is "owner" or "cashier" ? r.Role : throw ApiException.Validation("role", "enum");
        var hmac = tokens.PinHmac(Pin(r.Pin!));
        return await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7(now);
            await PinWriteAsync(c, tx, () => c.ExecuteAsync(
                """
                INSERT INTO staff (id, network_id, club_id, name, role, pin_hmac, active, created_at, updated_at)
                VALUES (@id, @NetworkId, @ClubId, @name, @role, @hmac, true, @now, @now)
                """,
                new { id, staff.NetworkId, staff.ClubId, name, role, hmac, now },
                tx));
            await Audit.WriteAsync(c, tx, staff, now, "staffAdd", detail: name, meta: new { staffId = id, role });
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminStaffCreateResponse(id.ToString())));
        });
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, StaffTokens tokens, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminStaffUpdateRequest>(body);
        var name = r.Name is null ? null : AdminInput.Text(r.Name, "name", 64);
        var hmac = r.Pin is null ? null : tokens.PinHmac(Pin(r.Pin));
        var staffId = Guid.TryParse(id, out var parsed) ? parsed : throw ApiException.NotFound("staff");
        if (r.Active == false && staffId == staff.StaffId)
        {
            throw ApiException.Validation("active", "self", "A staff member cannot disable themselves");
        }

        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var now = clock.GetUtcNow();

        // A disable locks the target together with the club's active owners, all in id order: two owners disabling each other
        // (or the ck_ key, which has no StaffId to match "self") queue up instead of both committing, and cannot deadlock.
        var rows = (await c.QueryAsync<(Guid Id, string Name, string Role, bool Active)>(
            """
            SELECT id, name, role, active FROM staff
            WHERE club_id = @ClubId AND (id = @staffId OR (@lockOwners AND role = 'owner' AND active))
            ORDER BY id FOR UPDATE
            """,
            new { staffId, staff.ClubId, lockOwners = r.Active == false }, tx)).ToList();
        if (!rows.Any(x => x.Id == staffId))
        {
            throw ApiException.NotFound("staff");
        }

        var before = rows.Single(x => x.Id == staffId);
        if (r.Active == false && before is { Role: "owner", Active: true } && rows.Count(x => x.Role == "owner" && x.Active) == 1)
        {
            throw ApiException.Validation("active", "lastOwner", "The club needs at least one active owner");
        }

        await PinWriteAsync(c, tx, () => c.ExecuteAsync(
            """
            UPDATE staff SET name = coalesce(@name, name), active = coalesce(@Active, active), pin_hmac = coalesce(@hmac, pin_hmac), updated_at = @now
            WHERE id = @staffId
            """,
            new { staffId, name, r.Active, hmac, now },
            tx));
        if (r.Active == false && before.Active)
        {
            await c.ExecuteAsync("UPDATE staff_tokens SET revoked_at = @now WHERE staff_id = @staffId AND revoked_at IS NULL", new { staffId, now }, tx);
        }

        // The PIN itself never reaches the journal: only that it changed.
        await Audit.WriteAsync(c, tx, staff, now, "staffUpdate", detail: name ?? before.Name,
            meta: new { staffId, name, active = r.Active, pinChanged = hmac is not null });
        await tx.CommitAsync();
        return AdminJson.Ok(AdminJson.OkBody);
    }

    /// <summary>4–8 ASCII digits (<c>\d</c> would take any Unicode digit), else <c>400 pin digits4to8</c>.</summary>
    private static string Pin(string pin) => PinPattern().IsMatch(pin) ? pin : throw ApiException.Validation("pin", "digits4to8");

    /// <summary>A PIN another member of the network holds is <c>400 pin taken</c>.</summary>
    private static async Task PinWriteAsync(NpgsqlConnection c, NpgsqlTransaction tx, Func<Task<int>> write)
    {
        await c.ExecuteAsync("SAVEPOINT pin", transaction: tx);
        try
        {
            await write();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "staff_network_id_pin_hmac_key")
        {
            await c.ExecuteAsync("ROLLBACK TO SAVEPOINT pin", transaction: tx);
            throw ApiException.Validation("pin", "taken");
        }
    }

    [GeneratedRegex("^[0-9]{4,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PinPattern();
}
