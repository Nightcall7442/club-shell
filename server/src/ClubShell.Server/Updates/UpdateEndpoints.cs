using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace ClubShell.Server.Updates;

/// <summary>
/// ClubShell updates from this server (UPDATES.md §8). The publisher (CI's <c>publish.ps1 -Target Http</c>, with the
/// release publisher token) uploads the packages to <c>PUT /&lt;channel&gt;/&lt;version&gt;/&lt;file&gt;</c> (outside
/// <c>/api/v1</c>, as the contract's <c>x-note</c> says) and then posts the release's <c>manifest.json</c> to
/// <c>POST /api/v1/updates/{channel}/manifest</c> (<c>publishUpdateManifest</c>), all with its <c>publishToken</c>
/// (<c>Updates:PublishToken</c>; none configured — <c>501</c>, as before).
/// <para>
/// <c>getUpdateManifest</c> answers a PC the component of its channel's release when it is newer than <c>current</c> and
/// the PC's club lets its PCs update by themselves (<c>clubs.auto_update</c>, «Обновлять автоматически» in the admin
/// console; the Agent installs at night, in its apply window, with nobody on the PC); otherwise <c>204</c>, as before the
/// owner turned it on. The owner's «Обновить ClubShell» sends the manifest with the <c>update</c> command instead
/// (<c>UpdateAdminEndpoints</c>). An Agent downloads the packages from <c>GET /&lt;channel&gt;/&lt;version&gt;/&lt;file&gt;</c> with
/// its access token (no request signature: the downloader sends the token alone; <c>Range</c> supported) — the package is
/// trusted for its SHA-256 and RSA-PSS signature, checked by the Agent, not for where it came from.
/// </para>
/// </summary>
public static class UpdateEndpoints
{
    public static readonly string[] Operations = ["getUpdateManifest", "publishUpdateManifest"];

    private static readonly string[] Components = ["agent", "shell"];

    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/updates");
        api.MapGet("/{channel}/manifest", ManifestAsync).WithMetadata(new AuthRequirement(AuthMode.AgentOrPublisher));
        api.MapPost("/{channel}/manifest", PublishAsync).WithMetadata(new AuthRequirement(AuthMode.Publisher));

        // Packages, outside /api/v1 (the auth middleware does not see them): each handler authenticates itself.
        // Literal channel prefixes, not a constrained parameter: a pattern matching any first segment would answer 405 to
        // every other POST path of two segments or more.
        foreach (var channel in Releases.Channels)
        {
            app.MapPut($"/{channel}/{{version}}/{{**path}}", (HttpContext context, string version, string path, Releases releases, UpdateOptions options, ILoggerFactory logs) =>
                UploadAsync(context, channel, version, path, releases, options, logs));
            app.MapGet($"/{channel}/{{version}}/{{**path}}", (HttpContext context, string version, string path, Releases releases, TokenService tokens, PcRepository pcs) =>
                DownloadAsync(context, channel, version, path, releases, tokens, pcs));
        }
    }

    private static async Task<IResult> ManifestAsync(HttpContext context, string channel, string? component, string? current, Releases releases, NpgsqlDataSource db)
    {
        channel = Channel(channel);
        if (string.IsNullOrEmpty(component))
        {
            throw ApiException.Validation("component", "required");
        }

        if (!Components.Contains(component, StringComparer.Ordinal))
        {
            throw ApiException.NotFound("component");
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            throw ApiException.Validation("current", "required");
        }

        // The publisher's check after publishing (publish.ps1 compares every component with current=0.0.0) sees the release
        // as it is; a PC only while its club updates by itself.
        if (context.Features.Get<PublisherContext>() is null)
        {
            var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
            await using var c = await db.OpenConnectionAsync();
            if (!await c.ExecuteScalarAsync<bool>("SELECT auto_update FROM clubs WHERE id = @ClubId", new { pc.ClubId }))
            {
                return Results.NoContent();
            }
        }

        var entry = await releases.LatestAsync(channel) is { } release
            ? release.Component(component == "agent" ? UpdateComponent.Agent : UpdateComponent.Shell)
            : null;
        return entry is not null && Releases.Compare(entry.Version, current) > 0 ? TypedResults.Ok(entry) : Results.NoContent();
    }

    /// <summary>
    /// The publisher's <c>manifest.json</c> (package.ps1): <c>version</c> and <c>channel</c> (the route's), and
    /// <c>components.agent</c> / <c>components.shell</c> — each an <see cref="UpdateManifest"/> of that version, signed
    /// (an empty <c>signature</c> is <c>400</c>: an Agent would refuse the package anyway).
    /// </summary>
    private static async Task<IResult> PublishAsync(string channel, JsonElement body, Releases releases, TimeProvider clock, ILoggerFactory logs)
    {
        channel = Channel(channel);
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("body", "schema", "JSON object expected");
        }

        var version = body.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Releases.Parse(version) is null)
        {
            throw ApiException.Validation("version", "semver");
        }

        if (!body.TryGetProperty("channel", out var ch) || ch.GetString() != channel)
        {
            throw ApiException.Validation("channel", "mismatch", "The manifest was packaged for another channel");
        }

        var release = new Release(channel, version!, body.Clone(), clock.GetUtcNow());
        var any = false;
        foreach (var component in new[] { UpdateComponent.Agent, UpdateComponent.Shell })
        {
            UpdateManifest? entry;
            try
            {
                entry = release.Component(component);
            }
            catch (JsonException)
            {
                throw ApiException.Validation($"components.{Name(component)}", "schema");
            }

            if (entry is null)
            {
                continue;
            }

            if (entry.Component != component || entry.Version != version || string.IsNullOrEmpty(entry.Url) || entry.Size <= 0)
            {
                throw ApiException.Validation($"components.{Name(component)}", "schema");
            }

            if (string.IsNullOrEmpty(entry.Signature))
            {
                throw ApiException.Validation($"components.{Name(component)}.signature", "required", "Unsigned packages are refused by every Agent: run sign.ps1");
            }

            any = true;
        }

        if (!any)
        {
            throw ApiException.Validation("components", "required");
        }

        await releases.SaveAsync(channel, version!, release.Manifest, release.PublishedAt);
        logs.CreateLogger(typeof(UpdateEndpoints).FullName!).LogWarning("ClubShell {Version} published to the {Channel} channel", version, channel);
        return Results.Ok(new { ok = true, channel, version });
    }

    private static async Task<IResult> UploadAsync(HttpContext context, string channel, string version, string path, Releases releases, UpdateOptions options, ILoggerFactory logs)
    {
        var logger = logs.CreateLogger(typeof(UpdateEndpoints).FullName!);
        if (!options.PublishEnabled)
        {
            throw ApiException.NotImplemented("publishUpdatePackage");
        }

        if (!options.IsPublishToken(AgentAuthMiddleware.BearerToken(context)))
        {
            logger.LogWarning("Wrong release publisher token from {Ip}", context.Connection.RemoteIpAddress);
            throw ApiException.Unauthorized("publishToken", "Missing or invalid release publisher token");
        }

        var file = releases.FilePath(channel, version, path);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            // An Agent MSI is well over Kestrel's 30 MB default.
            limit.MaxRequestBodySize = 2L * 1024 * 1024 * 1024;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".part";
        await using (var target = File.Create(temp))
        {
            await context.Request.Body.CopyToAsync(target, context.RequestAborted);
        }

        File.Move(temp, file, overwrite: true);
        logger.LogInformation("Update package {Channel}/{Version}/{Path} stored ({Bytes} bytes)", channel, version, path, new FileInfo(file).Length);
        return Results.StatusCode(StatusCodes.Status201Created);
    }

    private static async Task<IResult> DownloadAsync(HttpContext context, string channel, string version, string path, Releases releases, TokenService tokens, PcRepository pcs)
    {
        // The Agent's downloader sends its access token alone (no request signature), as the /ws/agent handshake.
        _ = await AgentAuthMiddleware.AuthenticateTokenAsync(AgentAuthMiddleware.BearerToken(context), tokens, pcs);
        var file = releases.FilePath(channel, version, path);
        if (!File.Exists(file))
        {
            throw ApiException.NotFound("file");
        }

        var type = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            ".msi" => "application/x-msi",
            _ => "application/octet-stream",
        };
        return Results.File(Path.GetFullPath(file), type, enableRangeProcessing: true);
    }

    /// <summary>A known channel, else <c>404 channel</c>.</summary>
    private static string Channel(string channel) =>
        Releases.Channels.Contains(channel, StringComparer.Ordinal) ? channel : throw ApiException.NotFound("channel");

    private static string Name(UpdateComponent component) => component == UpdateComponent.Agent ? "agent" : "shell";
}
