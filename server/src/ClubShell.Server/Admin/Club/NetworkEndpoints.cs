using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Admin;

/// <summary>
/// The console's network page beyond the contract (<c>GET /admin/network</c>, <c>POST /admin/network/clubs</c>): one network,
/// one club per deployment in v1 (D-19), so both answer <c>501 notImplemented</c> — for the owner; a cashier gets
/// <c>403 ownerOnly</c> first, as the mock's owner-only routes.
/// </summary>
public static class NetworkEndpoints
{
    public static void MapNetworkEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/network").WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapGet("", (RequestDelegate)(_ => throw ApiException.NotImplemented("adminNetwork")));
        api.MapPost("/clubs", (RequestDelegate)(_ => throw ApiException.NotImplemented("adminAddNetworkClub")));
    }
}
