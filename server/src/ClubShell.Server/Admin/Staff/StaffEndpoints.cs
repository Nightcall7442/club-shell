using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// Staff sign-in (slice S4, DESIGN §3.5): <c>adminLogin</c> by PIN (<c>401 invalidPin</c>; past a wrong-PIN limit of
/// <see cref="StaffLoginGuard"/> — per source, and club-wide for new and for trusted browsers, D-76 — <c>429 rateLimited</c>
/// with <c>retryAfterSec</c>, <c>scope</c> and <c>Retry-After</c>, for any PIN — the contract does not declare that 429 yet,
/// OQ-4 / §12.2 item 4), <c>adminLogout</c> (always
/// <c>200 {ok:true}</c>, <c>ck_</c> is not revoked) and <c>adminMe</c>. <c>active</c> is always true: an inactive member
/// cannot sign in.
/// </summary>
public static class StaffEndpoints
{
    public static readonly string[] Operations = ["adminLogin", "adminLogout", "adminMe"];

    public static void MapStaffEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin");
        api.MapPost("/login", LoginAsync).WithMetadata(new AuthRequirement(AuthMode.None));
        api.MapPost("/logout", async (HttpContext context, StaffTokens staff) =>
        {
            await staff.RevokeAsync(AgentAuthMiddleware.BearerToken(context));
            return AdminJson.Ok(AdminJson.OkBody);
        }).WithMetadata(new AuthRequirement(AuthMode.None));
        api.MapGet("/me", async (HttpContext context, NpgsqlDataSource db) =>
        {
            var staff = context.Features.GetRequiredFeature<StaffContext>();
            await using var c = await db.OpenConnectionAsync();
            return AdminJson.Ok(new AdminMeResponse(await MemberAsync(c, staff), await ShiftEndpoints.OpenShiftAsync(c, staff.ClubId)));
        }).WithMetadata(new AuthRequirement(AuthMode.Staff));
    }

    /// <summary>The signed-in member as <c>AdminStaffMember</c>: active by definition, <c>pcService</c> read from the row.</summary>
    public static async Task<AdminStaffMember> MemberAsync(NpgsqlConnection c, StaffContext staff)
    {
        var marked = staff.StaffId is { } id && await c.ExecuteScalarAsync<bool>("SELECT pc_service FROM staff WHERE id = @id", new { id });
        return new(staff.WireId, staff.Name, staff.Role, Active: true, StaffAdminEndpoints.PcService(staff.Role, marked));
    }

    private static async Task<IResult> LoginAsync(HttpContext context, [FromBody] JsonElement body, StaffTokens staff, NpgsqlDataSource db)
    {
        var request = Api.Read<AdminLoginRequest>(body, "pin");
        var pin = request.Pin!;
        if (pin.Length is 0 or > 12)
        {
            throw ApiException.Validation("pin", pin.Length == 0 ? "min" : "max");
        }

        var deviceId = request.DeviceId is { ValueKind: JsonValueKind.String } device ? device.GetString() : null;
        var (token, member) = await staff.LoginAsync(pin, request.ClubCode, context.Connection.RemoteIpAddress, deviceId)
            ?? throw ApiException.Unauthorized("invalidPin", "No active staff member with this PIN");
        await using var c = await db.OpenConnectionAsync();
        return AdminJson.Ok(new AdminLoginResponse(token, await MemberAsync(c, member), await ShiftEndpoints.OpenShiftAsync(c, member.ClubId)));
    }
}
