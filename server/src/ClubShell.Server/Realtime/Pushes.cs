using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Server.Auth;
using ClubShell.Server.Wallet;
using Npgsql;

namespace ClubShell.Server.Realtime;

/// <summary>
/// The required pushes of AsyncAPI (DESIGN §6.5), sent after the commit that caused them: <c>sessionUpdated</c> to the
/// session's PC, <c>walletUpdated</c> to every PC where the user holds a live token, <c>userRevoked</c> to the PCs whose
/// player token the server just took away (cashier password reset, blacklist, S5): a logout the PC asked for is not a
/// revocation.
/// </summary>
public sealed class Pushes(AgentSocketHub hub, UserTokens tokens, NpgsqlDataSource db)
{
    public Task SessionAsync(Session session) => hub.TryPushAsync(session.PcId, WsPushKind.SessionUpdated, JsonDefaults.ToElement(session));

    /// <summary><c>userRevoked {userId, reason}</c> to <paramref name="pcIds"/>; the agent signs the player out and ends the session.</summary>
    public async Task UserRevokedAsync(Guid userId, string reason, IEnumerable<Guid> pcIds)
    {
        var payload = JsonDefaults.ToElement(new UserRevokedPush(userId, reason));
        foreach (var pcId in pcIds)
        {
            await hub.TryPushAsync(pcId, WsPushKind.UserRevoked, payload);
        }
    }

    public async Task WalletAsync(Guid userId)
    {
        Contracts.Wallet.Balance? balance;
        await using (var c = await db.OpenConnectionAsync())
        {
            balance = await Ledger.BalanceAsync(c, userId);
        }

        if (balance is not null)
        {
            var payload = JsonDefaults.ToElement(balance);
            foreach (var pcId in await tokens.PcsOfAsync(userId))
            {
                await hub.TryPushAsync(pcId, WsPushKind.WalletUpdated, payload);
            }
        }
    }
}
