using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// Staff sign-in (slice S4, DESIGN §3.5): <c>adminLogin</c> by PIN (<c>401 invalidPin</c>; after
/// <see cref="AuthOptions.PinAttempts"/> wrong PINs from one IP (IPv6: one /64) within the window <c>429 rateLimited</c> +
/// <c>Retry-After</c>, for any PIN — the contract does not declare that 429 yet, OQ-4 / §12.2 item 4), <c>adminLogout</c> (always
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
            return AdminJson.Ok(new AdminMeResponse(Member(staff), await ShiftEndpoints.OpenShiftAsync(c, staff.ClubId)));
        }).WithMetadata(new AuthRequirement(AuthMode.Staff));
    }

    public static AdminStaffMember Member(StaffContext staff) => new(staff.WireId, staff.Name, staff.Role, Active: true);

    private static async Task<IResult> LoginAsync(HttpContext context, [FromBody] JsonElement body, StaffTokens staff, NpgsqlDataSource db)
    {
        var pin = Api.Read<AdminLoginRequest>(body, "pin").Pin!;
        if (pin.Length is 0 or > 12)
        {
            throw ApiException.Validation("pin", pin.Length == 0 ? "min" : "max");
        }

        var (token, member) = await staff.LoginAsync(pin, context.Connection.RemoteIpAddress)
            ?? throw ApiException.Unauthorized("invalidPin", "No active staff member with this PIN");
        await using var c = await db.OpenConnectionAsync();
        return AdminJson.Ok(new AdminLoginResponse(token, Member(member), await ShiftEndpoints.OpenShiftAsync(c, member.ClubId)));
    }
}
