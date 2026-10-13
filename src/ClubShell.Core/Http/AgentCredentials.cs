using ClubShell.Core.Abstractions;
using ClubShell.Core.Realtime;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging;

namespace ClubShell.Core.Http;

/// <summary>
/// How the Agent gets usable credentials when it starts (D-78). Stored tokens valid beyond the leeway are reused. Expiring
/// ones are refreshed first, and only a refresh the server rejects (401/403: the refresh token expired, was reused or
/// revoked) leads to <c>POST /agents/register</c>, which then proves the stored signing secret (<see cref="ServerClient"/>
/// signs it). A transport or server error never does: the refresh is retried with the registration backoff for as long as
/// the server cannot be reached. Without stored tokens the PC registers unsigned. Before D-78 an expired access token
/// meant a registration, so every PC switched off overnight re-registered each morning.
/// </summary>
public static class AgentCredentials
{
    /// <summary>First wait after a failed attempt; it doubles up to <see cref="RetryMax"/> (with jitter).</summary>
    public static readonly TimeSpan RetryMin = TimeSpan.FromSeconds(3);

    /// <summary>Longest wait between attempts.</summary>
    public static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Returns once <paramref name="tokens"/> hold credentials valid for more than <paramref name="leeway"/> (by
    /// <see cref="IServerClient.ServerNow"/>), retrying until <paramref name="cancellationToken"/> fires.
    /// <paramref name="register"/> registers the PC and stores its tokens.
    /// </summary>
    public static async Task EnsureAsync(
        IServerClient server,
        ITokenStore tokens,
        Func<CancellationToken, Task> register,
        IClock clock,
        TimeSpan leeway,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        var attempt = 0;
        var refreshRejected = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = tokens.Agent;
            if (stored is not null && !stored.IsExpired(server.ServerNow, leeway))
            {
                logger.LogInformation("Reusing stored agent tokens for PC {PcId}", stored.PcId);
                return;
            }

            var refreshing = stored is not null && !refreshRejected;
            try
            {
                if (refreshing && stored is not null)
                {
                    try
                    {
                        await server.RefreshAsync(cancellationToken).ConfigureAwait(false);
                        logger.LogInformation("Stored agent tokens refreshed for PC {PcId}", stored.PcId);
                        return;
                    }
                    catch (ServerApiException ex) when (ex.IsAuthFailure)
                    {
                        // Expired (offline for longer than the refresh token lives), reused (a refresh response was lost) or
                        // revoked: the secret is still the PC's, and the registration proves it.
                        refreshRejected = true;
                        refreshing = false;
                        logger.LogWarning("Stored refresh token rejected ({Reason}); registering again with proof of the signing secret", ex.Reason ?? ex.Code.ToString());
                    }
                }

                await register(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = Reconnector.ComputeDelay(attempt, RetryMin, RetryMax, 2.0);
                if (refreshing)
                {
                    logger.LogWarning(ex, "Token refresh attempt {Attempt} failed; retrying in {Delay}", attempt, delay);
                }
                else
                {
                    logger.LogWarning(ex, "Registration attempt {Attempt} failed; retrying in {Delay}", attempt, delay);
                }

                await clock.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
