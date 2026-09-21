using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games.Accounts;

/// <summary>
/// An account-pool lease held by this Agent with its secret decrypted in memory. The secret is only reachable
/// through <see cref="RevealSecret"/> and is never part of <see cref="ToString"/> or logs.
/// </summary>
public sealed class ActiveLease
{
    private readonly string _secret;

    internal ActiveLease(AccountLease contract, Guid gameId, Guid sessionId, string secret, DateTimeOffset leasedAt)
    {
        Contract = contract;
        GameId = gameId;
        SessionId = sessionId;
        _secret = secret;
        LeasedAt = leasedAt;
    }

    /// <summary>Server lease (its <see cref="AccountLease.Secret"/> is still encrypted).</summary>
    public AccountLease Contract { get; }

    /// <summary>Lease id.</summary>
    public Guid LeaseId => Contract.LeaseId;

    /// <summary>Game the lease was taken for.</summary>
    public Guid GameId { get; }

    /// <summary>Session the lease was taken for.</summary>
    public Guid SessionId { get; }

    /// <summary>Launcher the credentials belong to.</summary>
    public LauncherType Launcher => Contract.Launcher;

    /// <summary>Account login.</summary>
    public string Username => Contract.Username;

    /// <summary>Lease expiry (server time).</summary>
    public DateTimeOffset ExpiresAt => Contract.ExpiresAt;

    /// <summary>When the lease was obtained.</summary>
    public DateTimeOffset LeasedAt { get; }

    /// <summary>Cloud-save bundle to restore before launch, when any.</summary>
    public CloudSaveDownload? CloudSave => Contract.CloudSave;

    /// <summary>Launcher-specific extras.</summary>
    public JsonElement? Extra => Contract.Extra;

    /// <summary>Decrypted password / token. Callers must not log or persist it.</summary>
    public string RevealSecret() => _secret;

    /// <summary>String member of <see cref="Extra"/>, or <see langword="null"/>.</summary>
    public string? ExtraString(string key) =>
        Extra is { ValueKind: JsonValueKind.Object } extra && extra.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary><see langword="true"/> when the lease expires within <paramref name="leeway"/> of <paramref name="now"/>.</summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan? leeway = null) => now + (leeway ?? TimeSpan.Zero) >= ExpiresAt;

    /// <summary>Redacted description (no secret).</summary>
    public override string ToString() => $"lease {LeaseId} ({Launcher} account {Username}, expires {ExpiresAt:O})";
}

/// <summary>
/// Leases pooled launcher accounts from the server (SERVER_API.md §4.6), decrypts their secrets with the agent
/// signing key, keeps active leases in memory, releases them (with cloud-save upload info) and expires them on time.
/// </summary>
public sealed class AccountPool : IAsyncDisposable, IDisposable
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    private readonly IServerClient _server;
    private readonly ITokenStore _tokens;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<AccountPool> _logger;
    private readonly ConcurrentDictionary<Guid, ActiveLease> _leases = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _watchdog;

    /// <summary>Creates the pool (call <see cref="Start"/> to run the expiry watchdog).</summary>
    public AccountPool(IServerClient server, ITokenStore tokens, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<AccountPool> logger)
    {
        _server = server;
        _tokens = tokens;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>A lease reached its expiry while still held; subscribers should stop the game using it.</summary>
    public event EventHandler<ActiveLease>? LeaseExpired;

    /// <summary><c>games.accountPool.enabled</c>.</summary>
    public bool Enabled => _settings.CurrentValue.Games.AccountPool.Enabled;

    /// <summary>Leases currently held.</summary>
    public IReadOnlyList<ActiveLease> Active => _leases.Values.ToList();

    /// <summary>Held lease by id, or <see langword="null"/>.</summary>
    public ActiveLease? Get(Guid leaseId) => _leases.TryGetValue(leaseId, out ActiveLease? lease) ? lease : null;

    /// <summary>Starts the expiry watchdog (idempotent).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _watchdog ??= Task.Run(() => WatchdogAsync(_cts.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Returns an unexpired held lease for <paramref name="reuseLeaseId"/> or for the same game+session, otherwise
    /// leases a new account (retrying transient failures). Throws <see cref="IpcException"/> with
    /// <see cref="ErrorCode.AccountPoolExhausted"/> when no account is available or the server cannot be reached.
    /// </summary>
    public async Task<ActiveLease> LeaseAsync(Game game, Guid sessionId, Guid? reuseLeaseId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        Start();
        DateTimeOffset now = _clock.UtcNow;
        if (reuseLeaseId is { } reuse && _leases.TryGetValue(reuse, out ActiveLease? held) && !held.IsExpired(now, TimeSpan.FromMinutes(1)))
        {
            return held;
        }

        ActiveLease? existing = _leases.Values.FirstOrDefault(l => l.GameId == game.Id && l.SessionId == sessionId && !l.IsExpired(now, TimeSpan.FromMinutes(1)));
        if (existing is not null)
        {
            return existing;
        }

        byte[]? signingSecret = _tokens.Agent?.DecodeSigningSecret();
        if (signingSecret is null)
        {
            throw IpcError.Unauthorized("Agent is not registered; account pool unavailable").ToException();
        }

        try
        {
            AccountLease contract = await LeaseFromServerAsync(game.Id, sessionId, cancellationToken).ConfigureAwait(false);
            string secret;
            try
            {
                secret = Signing.DecryptAccountPoolSecret(signingSecret, contract.Secret);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                _logger.LogError(ex, "Cannot decrypt account-pool secret for lease {LeaseId}", contract.LeaseId);
                await ReleaseOnServerAsync(game.Id, contract.LeaseId, new AccountLeaseRelease(AccountLeaseReleaseReason.LaunchFailed), CancellationToken.None).ConfigureAwait(false);
                throw IpcError.GameLaunchFailed("lease", "Account-pool secret cannot be decrypted").ToException();
            }

            var lease = new ActiveLease(contract, game.Id, sessionId, secret, _clock.UtcNow);
            _leases[lease.LeaseId] = lease;
            _logger.LogInformation("Leased {Lease} for {Title}", lease, game.Title);
            return lease;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingSecret);
        }
    }

    /// <summary>Releases a held lease (no-op when unknown); server errors are logged, never thrown.</summary>
    public async Task ReleaseAsync(Guid leaseId, AccountLeaseReleaseReason reason, CloudSaveUpload? cloudSave, CancellationToken cancellationToken)
    {
        if (!_leases.TryRemove(leaseId, out ActiveLease? lease))
        {
            return;
        }

        await ReleaseOnServerAsync(lease.GameId, leaseId, new AccountLeaseRelease(reason, cloudSave), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Released {Lease} ({Reason})", lease, reason);
    }

    /// <summary>Releases every held lease.</summary>
    public async Task ReleaseAllAsync(AccountLeaseReleaseReason reason, CancellationToken cancellationToken)
    {
        foreach (Guid id in _leases.Keys.ToArray())
        {
            await ReleaseAsync(id, reason, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        Task? watchdog;
        lock (_gate)
        {
            watchdog = _watchdog;
        }

        if (watchdog is not null)
        {
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _cts.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async Task<AccountLease> LeaseFromServerAsync(Guid gameId, Guid sessionId, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _server.LeaseAccountAsync(gameId, sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (ServerApiException ex) when (ex.Code == ErrorCode.AccountPoolExhausted)
            {
                _logger.LogWarning("Account pool exhausted for game {GameId}", gameId);
                throw IpcError.AccountPoolExhausted().ToException();
            }
            catch (ServerApiException ex) when (!ex.IsRetryable)
            {
                _logger.LogWarning(ex, "Account lease refused for game {GameId}: {Code}", gameId, ex.Code);
                throw ex.ToIpcError().ToException();
            }
            catch (Exception ex) when (ex is ServerApiException or HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (attempt >= MaxAttempts)
                {
                    _logger.LogWarning(ex, "Account lease for game {GameId} failed after {Attempts} attempts", gameId, attempt);
                    throw IpcError.Of(ErrorCode.AccountPoolExhausted, "Account pool unreachable; no cached lease").ToException();
                }

                TimeSpan delay = BaseDelay * Math.Pow(2, attempt - 1);
                _logger.LogDebug(ex, "Account lease attempt {Attempt} failed; retrying in {Delay}", attempt, delay);
                await _clock.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReleaseOnServerAsync(Guid gameId, Guid leaseId, AccountLeaseRelease release, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await _server.ReleaseAccountLeaseAsync(gameId, leaseId, release, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is ServerApiException or HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                bool retry = attempt < MaxAttempts && (ex is not ServerApiException api || api.IsRetryable);
                if (!retry)
                {
                    _logger.LogWarning(ex, "Lease {LeaseId} release not acknowledged by server; it will expire server-side", leaseId);
                    return;
                }

                await _clock.Delay(BaseDelay * Math.Pow(2, attempt - 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(WatchdogInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset now = _clock.UtcNow;
            foreach (ActiveLease lease in _leases.Values)
            {
                if (!lease.IsExpired(now))
                {
                    continue;
                }

                _logger.LogWarning("{Lease} expired while held", lease);
                try
                {
                    LeaseExpired?.Invoke(this, lease);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LeaseExpired handler threw for {LeaseId}", lease.LeaseId);
                }

                await ReleaseAsync(lease.LeaseId, AccountLeaseReleaseReason.Exit, null, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
