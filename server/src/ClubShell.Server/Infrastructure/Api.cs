using System.Text.Json;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Auth;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace ClubShell.Server.Infrastructure;

/// <summary>Shared plumbing of the <c>/api/v1</c> endpoint groups.</summary>
public static class Api
{
    /// <summary>
    /// A route group whose endpoints accept any Content-Type: body binding then rejects a non-JSON one (415 → 400
    /// validation). With the inferred application/json only, routing would skip them and the fallback would answer 404.
    /// </summary>
    public static RouteGroupBuilder MapApiGroup(this IEndpointRouteBuilder app, string prefix)
    {
        var group = app.MapGroup(prefix);
        ((IEndpointConventionBuilder)group).Finally(endpoint => endpoint.Metadata.Add(new AcceptsMetadata(["application/json", "*/*"])));
        return group;
    }

    /// <summary>
    /// Player routes <c>/users/{userId}/…</c> and <c>/wallet/{userId}/…</c> (DESIGN §3.1): user mode, and only the player's
    /// own <c>userId</c> — another one is <c>403 notOwner</c>.
    /// </summary>
    public static RouteGroupBuilder OwnedByPlayer(this RouteGroupBuilder group) =>
        group.WithMetadata(new AuthRequirement(AuthMode.User)).AddEndpointFilter((context, next) =>
        {
            if (context.HttpContext.GetRouteValue("userId") is string raw && Guid.TryParse(raw, out var userId)
                && userId != context.HttpContext.Features.GetRequiredFeature<UserContext>().UserId)
            {
                throw ApiException.Forbidden("notOwner", "userId is not the signed-in player");
            }

            return next(context);
        });

    /// <summary>
    /// A contract DTO from a JSON body, with <c>400 validation</c> <c>field</c>/<c>required</c> for each of
    /// <paramref name="required"/> that is missing or null (the record binder would silently default it) and
    /// <c>reason=format</c> when a value does not parse.
    /// </summary>
    public static T Read<T>(JsonElement body, params string[] required)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("body", "format", "JSON object expected");
        }

        foreach (var name in required)
        {
            if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                throw ApiException.Validation(name, "required");
            }
        }

        try
        {
            return JsonDefaults.FromElement<T>(body) ?? throw ApiException.Validation("body", "format");
        }
        catch (JsonException ex)
        {
            throw ApiException.Validation(ex.Path is { Length: > 2 } path ? path[2..] : "body", "format", "Malformed JSON body");
        }
    }
}
