using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;
using ClubShell.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Core.Security;

/// <summary>Agent credentials issued by <c>POST /agents/register</c> / <c>POST /agents/refresh</c> (stored in <c>secure\agent.tokens</c>).</summary>
/// <param name="PcId">PC id the tokens belong to.</param>
/// <param name="AccessToken">Agent JWT (≈ 1 h).</param>
/// <param name="RefreshToken">Opaque single-use refresh token (30 d).</param>
/// <param name="SigningSecret">Base64 32-byte HMAC key.</param>
/// <param name="ExpiresAt">Access token expiry.</param>
public sealed record AgentTokens(
    Guid PcId,
    string AccessToken,
    string RefreshToken,
    string SigningSecret,
    DateTimeOffset ExpiresAt)
{
    /// <summary><see langword="true"/> when the access token expires within <paramref name="leeway"/> of <paramref name="now"/>.</summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan? leeway = null) => ExpiresAt - (leeway ?? TimeSpan.Zero) <= now;

    /// <summary>Decoded HMAC key.</summary>
    public byte[] DecodeSigningSecret() => Signing.DecodeSecret(SigningSecret);
}

/// <summary>User credentials issued by <c>POST /auth/*</c>; held by the Agent only, never sent to the Shell.</summary>
/// <param name="UserId">User id.</param>
/// <param name="AccessToken">User access token (<c>X-User-Token</c>).</param>
/// <param name="RefreshToken">User refresh token.</param>
/// <param name="ExpiresAt">Access token expiry.</param>
public sealed record UserTokens(
    Guid UserId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt)
{
    /// <summary><see langword="true"/> when the access token expires within <paramref name="leeway"/> of <paramref name="now"/>.</summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan? leeway = null) => ExpiresAt - (leeway ?? TimeSpan.Zero) <= now;
}

/// <summary>Encrypts the token file at rest.</summary>
public interface ITokenProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Decrypts <paramref name="ciphertext"/>; throws <see cref="CryptographicException"/> when it cannot.</summary>
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>DPAPI (<see cref="DataProtectionScope.LocalMachine"/>) protector with application entropy (ARCHITECTURE.md §3).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenProtector : ITokenProtector
{
    private static readonly byte[] DefaultEntropy = Encoding.UTF8.GetBytes("ClubShell.Agent.tokens.v1");
    private readonly byte[] _entropy;

    /// <summary>Creates a protector with the default application entropy.</summary>
    public DpapiTokenProtector()
        : this(DefaultEntropy)
    {
    }

    /// <summary>Creates a protector with custom entropy.</summary>
    public DpapiTokenProtector(byte[] entropy)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        _entropy = entropy;
    }

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.LocalMachine);
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ProtectedData.Unprotect(ciphertext, _entropy, DataProtectionScope.LocalMachine);
    }
}

/// <summary>Pass-through protector for tests and non-Windows tooling. Never use in production.</summary>
public sealed class NullTokenProtector : ITokenProtector
{
    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return plaintext;
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ciphertext;
    }
}

/// <summary>Persistent, encrypted store of agent and user tokens with an in-memory cache.</summary>
public interface ITokenStore
{
    /// <summary>Agent tokens, or <see langword="null"/> when not registered / not loaded.</summary>
    AgentTokens? Agent { get; }

    /// <summary>Logged-in user's tokens, or <see langword="null"/>.</summary>
    UserTokens? User { get; }

    /// <summary><see langword="true"/> after <see cref="LoadAsync"/> completed (even when the file was absent).</summary>
    bool IsLoaded { get; }

    /// <summary>Raised after any change is persisted.</summary>
    event EventHandler? Changed;

    /// <summary>Loads the file; an unreadable/undecryptable file is treated as empty (forces re-registration).</summary>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the agent tokens and persists.</summary>
    Task SetAgentAsync(AgentTokens tokens, CancellationToken cancellationToken);

    /// <summary>Replaces (or clears with <see langword="null"/>) the user tokens and persists.</summary>
    Task SetUserAsync(UserTokens? tokens, CancellationToken cancellationToken);

    /// <summary>Removes every token and deletes the file.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Serialized form of the token file.</summary>
internal sealed record TokenFile(int Version, AgentTokens? Agent, UserTokens? User)
{
    /// <summary>Current file version.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>
/// <see cref="ITokenStore"/> over an <see cref="ITokenProtector"/>-wrapped JSON file (<c>secure\agent.tokens</c>).
/// Writes are atomic (temp + move) and serialized; reads are lock-free from the in-memory snapshot.
/// </summary>
public sealed class TokenStore : ITokenStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private readonly string _filePath;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;
    private readonly ILogger<TokenStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TokenFile _snapshot = new(TokenFile.CurrentVersion, null, null);
    private bool _loaded;
    private bool _disposed;

    /// <summary>Creates a store at <paramref name="filePath"/>.</summary>
    public TokenStore(string filePath, ITokenProtector protector, IClock clock, ILogger<TokenStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clock);
        _filePath = filePath;
        _protector = protector;
        _clock = clock;
        _logger = logger ?? NullLogger<TokenStore>.Instance;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>File the tokens are persisted to.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc />
    public AgentTokens? Agent => Volatile.Read(ref _snapshot).Agent;

    /// <inheritdoc />
    public UserTokens? User => Volatile.Read(ref _snapshot).User;

    /// <inheritdoc />
    public bool IsLoaded => Volatile.Read(ref _loaded);

    /// <summary><see langword="true"/> when the agent access token is missing or expires within <paramref name="leeway"/>.</summary>
    public bool IsAgentTokenExpiring(TimeSpan leeway) => Agent is not { } agent || agent.IsExpired(_clock.UtcNow, leeway);

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, loaded);
            Volatile.Write(ref _loaded, true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public Task SetAgentAsync(AgentTokens tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return UpdateAsync(current => current with { Agent = tokens }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetUserAsync(UserTokens? tokens, CancellationToken cancellationToken) =>
        UpdateAsync(current => current with { User = tokens }, cancellationToken);

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _snapshot, new TokenFile(TokenFile.CurrentVersion, null, null));
            Volatile.Write(ref _loaded, true);
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task UpdateAsync(Func<TokenFile, TokenFile> mutate, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                Volatile.Write(ref _snapshot, await ReadFileAsync(cancellationToken).ConfigureAwait(false));
                Volatile.Write(ref _loaded, true);
            }

            var updated = mutate(_snapshot) with { Version = TokenFile.CurrentVersion };
            await WriteFileAsync(updated, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, updated);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<TokenFile> ReadFileAsync(CancellationToken cancellationToken)
    {
        var empty = new TokenFile(TokenFile.CurrentVersion, null, null);
        if (!File.Exists(_filePath))
        {
            return empty;
        }

        try
        {
            var ciphertext = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            if (ciphertext.Length == 0)
            {
                return empty;
            }

            var plaintext = _protector.Unprotect(ciphertext);
            var file = JsonSerializer.Deserialize<TokenFile>(plaintext, JsonOptions);
            return file is null ? empty : file with { Version = TokenFile.CurrentVersion };
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Token file {Path} is unreadable; treating as empty (re-registration required)", _filePath);
            return empty;
        }
    }

    private async Task WriteFileAsync(TokenFile file, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
        byte[] ciphertext;
        try
        {
            ciphertext = _protector.Protect(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var temp = _filePath + ".tmp";
        await File.WriteAllBytesAsync(temp, ciphertext, cancellationToken).ConfigureAwait(false);
        File.Move(temp, _filePath, overwrite: true);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
