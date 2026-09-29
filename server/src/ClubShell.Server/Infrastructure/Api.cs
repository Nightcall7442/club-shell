using System.Text.Json;
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
    /// A contract DTO (or a hand-written admin request, which resolves by reflection through <see cref="ServerJson.Options"/>)
    /// from a JSON body, with <c>400 validation</c> <c>field</c>/<c>required</c> for each of
    /// <paramref name="required"/> that is missing or null (the record binder would silently default it; a dotted name such
    /// as <c>result.ok</c> is looked up inside its parent object) and <c>reason=format</c> when a value does not parse. A body
    /// that is not an object is <c>field=body</c> <c>reason=schema</c> (contract <c>BadRequest</c>: whole-body reasons).
    /// </summary>
    public static T Read<T>(JsonElement body, params string[] required)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("body", "schema", "JSON object expected");
        }

        foreach (var name in required)
        {
            var value = body;
            foreach (var part in name.Split('.'))
            {
                // A parent that is not an object is left to the binder (format).
                if (value.ValueKind == JsonValueKind.Object && (!value.TryGetProperty(part, out value) || value.ValueKind == JsonValueKind.Null))
                {
                    throw ApiException.Validation(name, "required");
                }
            }
        }

        try
        {
            return body.Deserialize<T>(ServerJson.Options) ?? throw ApiException.Validation("body", "schema");
        }
        catch (JsonException ex) when (ex.Path is { Length: > 2 } path)
        {
            throw ApiException.Validation(path[2..], "format", "Malformed JSON body");
        }
        catch (JsonException)
        {
            throw ApiException.Validation("body", "schema", "JSON body does not match the request schema");
        }
    }
}
