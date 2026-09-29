using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Updates;

/// <summary>
/// <c>getUpdateManifest</c> (slice S3): the owner's decision of 2026-09-27 — server v1 distributes no updates and always
/// answers <c>204</c> ("up to date") after validating the request, so old agents that retry a 501 never open their circuit
/// breaker (<c>x-server-note</c>). Unknown channel or component — <c>404</c>; no <c>current</c> — <c>400</c>.
/// </summary>
public static class UpdateEndpoints
{
    public static readonly string[] Operations = ["getUpdateManifest"];

    private static readonly string[] Channels = ["stable", "beta"];
    private static readonly string[] Components = ["agent", "shell"];

    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app) =>
        app.MapApiGroup("/api/v1/updates").MapGet("/{channel}/manifest", (string channel, string? component, string? current) =>
        {
            if (!Channels.Contains(channel, StringComparer.Ordinal))
            {
                throw ApiException.NotFound("channel");
            }

            if (string.IsNullOrEmpty(component))
            {
                throw ApiException.Validation("component", "required");
            }

            if (!Components.Contains(component, StringComparer.Ordinal))
            {
                throw ApiException.NotFound("component");
            }

            return string.IsNullOrWhiteSpace(current) ? throw ApiException.Validation("current", "required") : Results.NoContent();
        }).WithMetadata(new AuthRequirement(AuthMode.Agent));
}
