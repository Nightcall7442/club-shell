using Dapper;
using Npgsql;

namespace ClubShell.Server.Agents;

/// <summary>What authentication needs of a PC; S1 widens it with the registry columns.</summary>
public sealed class PcRow
{
    public Guid Id { get; init; }
    public Guid ClubId { get; init; }
    public string? Hwid { get; init; }
    public byte[]? SigningSecret { get; init; }
    public int CredentialsVersion { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed class PcRepository(NpgsqlDataSource db)
{
    public async Task<PcRow?> FindAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<PcRow>(
            "SELECT id, club_id, hwid, signing_secret, credentials_version, deleted_at FROM pcs WHERE id = @id",
            new { id });
    }
}
