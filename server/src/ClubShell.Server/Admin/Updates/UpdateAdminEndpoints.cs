using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Updates;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// «Обновления ClubShell» in the admin console (owner only, beyond the contract, UPDATES.md §8):
/// <list type="bullet">
/// <item><c>GET /admin/updates</c> — the releases published to this server, whether the club's PCs update by themselves at
/// night, and each PC's Agent and Shell versions, online and busy;</item>
/// <item><c>PATCH /admin/updates {auto}</c> — «Обновлять автоматически»: <c>getUpdateManifest</c> then offers the release to
/// the club's PCs, which install it in their apply window (04:00–07:00 by default) with nobody on them;</item>
/// <item><c>POST /admin/updates/apply {channel?, pcIds?}</c> — «Обновить ClubShell» now: to every connected PC of the club
/// (or the ones named) whose Agent or Shell is older than the channel's release, an <c>update</c> command per such
/// component with the release's manifest (Shell first: an Agent update restarts the service). A free PC installs at once,
/// a busy one when its session ends (<c>afterSession</c>); an offline PC gets nothing (<c>offline</c>), as an update that
/// fires hours later may surprise a player. Each PC sent to is journalled as <c>pcCommand</c> · <c>update</c>.</item>
/// </list>
/// </summary>
public static class UpdateAdminEndpoints
{
    public static void MapUpdateAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/updates").WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapGet("", StatusAsync);
        api.MapPatch("", SetAutoAsync);
        api.MapPost("/apply", ApplyAsync);
    }

    private static async Task<IResult> StatusAsync(HttpContext context, NpgsqlDataSource db, Releases releases, PcRepository pcs, AgentSocketHub hub)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        await using var c = await db.OpenConnectionAsync();
        var auto = await c.ExecuteScalarAsync<bool>("SELECT auto_update FROM clubs WHERE id = @ClubId", new { staff.ClubId });
        var published = new List<object>();
        foreach (var channel in Releases.Channels)
        {
            if (await releases.LatestAsync(channel) is { } release)
            {
                var agent = release.Component(UpdateComponent.Agent);
                var shell = release.Component(UpdateComponent.Shell);
                published.Add(new
                {
                    channel,
                    version = release.Version,
                    publishedAt = release.PublishedAt,
                    agent = agent?.Version,
                    shell = shell?.Version,
                    notes = shell?.ReleaseNotes ?? agent?.ReleaseNotes ?? "",
                    mandatory = (agent?.Mandatory ?? false) || (shell?.Mandatory ?? false),
                });
            }
        }

        var items = (await pcs.ListAsync(staff.ClubId)).Where(p => p.Approved).Select(p => new
        {
            id = p.Id,
            name = p.Name,
            number = p.Number,
            online = hub.IsConnected(p.Id),
            busy = p.OpenSessionId is not null,
            agentVersion = p.AgentVersion,
            shellVersion = p.ShellVersion,
        });
        return AdminJson.Ok(new { auto, releases = published, pcs = items });
    }

    private static async Task<IResult> SetAutoAsync(
        HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, PcRepository pcs, AgentSocketHub hub, CommandDispatcher dispatcher, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("auto", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw ApiException.Validation("auto", "required");
        }

        var auto = value.GetBoolean();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var changed = await c.ExecuteAsync(
            "UPDATE clubs SET auto_update = @auto, config_version = config_version + 1, updated_at = @now WHERE id = @ClubId AND auto_update <> @auto",
            new { auto, staff.ClubId, now = clock.GetUtcNow() }, tx) > 0;
        await Audit.WriteAsync(c, tx, staff, clock.GetUtcNow(), "settingsSave", detail: auto ? "Автообновление ClubShell включено" : "Автообновление ClubShell выключено",
            meta: new { autoUpdate = auto });
        await tx.CommitAsync();
        if (changed)
        {
            // updates.enabled of the agent config follows the switch: the connected PCs fetch it now.
            foreach (var pc in (await pcs.ListAsync(staff.ClubId)).Where(p => p.Approved && hub.IsConnected(p.Id)))
            {
                await dispatcher.EnqueueAsync(pc.ClubId, pc.Id, NewCommand.RefreshConfig(new RefreshConfigCommand(Config: true)));
            }
        }

        return AdminJson.Ok(new { ok = true, auto });
    }

    private static async Task<IResult> ApplyAsync(
        HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, Releases releases, PcRepository pcs, AgentSocketHub hub,
        CommandDispatcher dispatcher, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (body.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            throw ApiException.Validation("body", "schema", "JSON object expected");
        }

        var channel = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String
            ? ch.GetString()!
            : "stable";
        if (!Releases.Channels.Contains(channel, StringComparer.Ordinal))
        {
            throw ApiException.Validation("channel", "enum");
        }

        HashSet<Guid>? only = null;
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("pcIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            only = [];
            foreach (var id in ids.EnumerateArray())
            {
                only.Add(id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var g) ? g : throw ApiException.Validation("pcIds", "format"));
            }
        }

        var release = await releases.LatestAsync(channel) ?? throw ApiException.NotFound("release");
        var agent = release.Component(UpdateComponent.Agent);
        var shell = release.Component(UpdateComponent.Shell);
        var targets = (await pcs.ListAsync(staff.ClubId)).Where(p => p.Approved && (only is null || only.Contains(p.Id))).ToList();
        if (only is not null && targets.Count != only.Count)
        {
            throw ApiException.NotFound("pc");
        }

        var results = new List<object>();
        var sent = new List<(Guid PcId, ServerCommandEnvelope Command)>();
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            foreach (var pc in targets)
            {
                // Shell first: an Agent package restarts the service, and a Shell command sent after it waits for the new one.
                var needed = new List<UpdateManifest>();
                if (shell is not null && Releases.Compare(shell.Version, pc.ShellVersion) > 0)
                {
                    needed.Add(shell);
                }

                if (agent is not null && Releases.Compare(agent.Version, pc.AgentVersion) > 0)
                {
                    needed.Add(agent);
                }

                string result;
                if (needed.Count == 0)
                {
                    result = "upToDate";
                }
                else if (!hub.IsConnected(pc.Id))
                {
                    result = "offline";
                }
                else
                {
                    foreach (var manifest in needed)
                    {
                        var command = NewCommand.Update(new UpdateCommand(manifest.Component, ApplyNow: true, manifest));
                        sent.Add((pc.Id, await dispatcher.QueueAsync(tx, pc.ClubId, pc.Id, command, issuedByStaffId: staff.StaffId)));
                    }

                    await Audit.WriteAsync(c, tx, staff, now, "pcCommand", pcId: pc.Id, detail: $"{pc.Name} · update {release.Version}",
                        meta: new { kind = "update", version = release.Version, components = needed.Select(m => m.Component == UpdateComponent.Agent ? "agent" : "shell").ToArray() });
                    result = pc.OpenSessionId is not null ? "afterSession" : "sent";
                }

                results.Add(new { pcId = pc.Id, name = pc.Name, result });
            }

            await tx.CommitAsync();
        }

        foreach (var (pcId, command) in sent)
        {
            await dispatcher.SendAsync(pcId, command);
        }

        return AdminJson.Ok(new { channel, version = release.Version, results });
    }
}
