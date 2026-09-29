using ClubShell.Contracts.Commands;
using ClubShell.Server.Realtime;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// After a bump of <c>config_version</c>/<c>catalog_version</c> (DESIGN §5.9): <c>refreshConfig</c> with explicit flags for the
/// club's connected PCs, queued in the bump's transaction and sent after its commit. A PC that is not connected learns
/// the new version from its next heartbeat (<c>configVersion</c>) or catalog ETag, so nothing is queued for it.
/// </summary>
public static class ConfigRefresh
{
    public static async Task<IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)>> QueueAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, CommandDispatcher commands, AgentSocketHub hub, Guid clubId, RefreshConfigCommand flags, Guid? staffId = null)
    {
        var queued = new List<(Guid, ServerCommandEnvelope)>();
        var command = NewCommand.RefreshConfig(flags);
        foreach (var pcId in await c.QueryAsync<Guid>(
                     "SELECT id FROM pcs WHERE club_id = @clubId AND approved AND deleted_at IS NULL ORDER BY id", new { clubId }, tx))
        {
            if (hub.IsConnected(pcId))
            {
                queued.Add((pcId, await commands.QueueAsync(tx, clubId, pcId, command, issuedByStaffId: staffId)));
            }
        }

        return queued;
    }

    public static async Task SendAsync(CommandDispatcher commands, IEnumerable<(Guid PcId, ServerCommandEnvelope Command)> queued)
    {
        foreach (var (pcId, command) in queued)
        {
            await commands.SendAsync(pcId, command);
        }
    }
}
