using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Agent.Ipc;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Session;

/// <summary>What <c>cache\signed-in-user.bin</c> holds: the signed-in player of the open session.</summary>
/// <param name="User">The player as last seen (display name, avatar, balance, flags).</param>
/// <param name="ExpiresAt">Expiry of the player's access token as issued (the token itself stays in <c>secure\agent.tokens</c>).</param>
/// <param name="Mode">Whether the sign-in was checked by the server or by the offline cache.</param>
/// <param name="SessionId">The player's session open on this PC when saved.</param>
/// <param name="SavedAt">Time of the save.</param>
public sealed record SignedInUser(User User, DateTimeOffset ExpiresAt, ConnectivityState Mode, Guid SessionId, DateTimeOffset SavedAt);

/// <summary>
/// Keeps the signed-in player across an Agent restart while their session runs: an update, a crash or a watchdog
/// restart of the service no longer drops the player to the sign-in screen with the session still running for them.
/// The file is protected like the agent tokens (<see cref="ITokenProtector"/>: DPAPI, machine scope) and exists only
/// while the signed-in player has an open session on this PC: it is written on every sign-in, profile or balance change
/// and session start, and deleted on sign-out, revocation, expiry and session end. <see cref="Restore"/> takes it back at
/// start once the session is restored, when that session (as the server reports it, or the offline store when the
/// server is unreachable) is still the player's.
/// </summary>
public sealed class SignedInUserStore : IDisposable
{
    /// <summary>File name in <c>paths.cache</c> (persistent root).</summary>
    public const string FileName = "signed-in-user.bin";

    private readonly ShellUserContext _users;
    private readonly ISessionService _sessions;
    private readonly ITokenProtector _protector;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<SignedInUserStore> _logger;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Creates the store and starts following the signed-in player and the session.</summary>
    public SignedInUserStore(
        ShellUserContext users,
        ISessionService sessions,
        ITokenProtector protector,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<SignedInUserStore> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _users = users;
        _sessions = sessions;
        _protector = protector;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _users.Changed += OnUsersChanged;
        _sessions.Changed += OnSessionChanged;
    }

    /// <summary>Path of the file.</summary>
    public string FilePath
    {
        get
        {
            AgentSettings settings = _settings.CurrentValue;
            return Path.Combine(settings.ResolvePersistentPath(settings.Paths.Cache), FileName);
        }
    }

    /// <summary>
    /// At Agent start, after the session restore: signs the saved player back in when the open session is still theirs,
    /// and deletes the file otherwise. <see langword="true"/> when the player was restored.
    /// </summary>
    public bool Restore()
    {
        SignedInUser? saved = Load();
        if (saved is null)
        {
            return false;
        }

        if (_users.User is { } current)
        {
            _logger.LogInformation("Saved sign-in of {UserId} not restored: {Current} is signed in already", saved.User.Id, current.Id);
            return false;
        }

        if (!_sessions.State.IsOpen() || _sessions.Current is not { } session || session.UserId != saved.User.Id)
        {
            _logger.LogInformation("Saved sign-in of {UserId} dropped: no session of theirs is open on this PC any more", saved.User.Id);
            Delete();
            return false;
        }

        _users.Set(saved.User, saved.ExpiresAt, saved.Mode);
        _logger.LogInformation("Sign-in of {UserId} ({Username}) restored after an Agent restart; session {SessionId} goes on", saved.User.Id, saved.User.Username, session.Id);
        return true;
    }

    /// <summary>Reads the file; <see langword="null"/> when absent or unreadable (an unreadable file is deleted).</summary>
    public SignedInUser? Load()
    {
        lock (_gate)
        {
            string path = FilePath;
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                byte[] plaintext = _protector.Unprotect(File.ReadAllBytes(path));
                try
                {
                    return JsonSerializer.Deserialize(plaintext, AgentStateJson.Default.SignedInUserFile)?.ToRecord();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(ex, "Saved sign-in at {Path} is unreadable; dropped", path);
                TryDelete(path);
                return null;
            }
        }
    }

    /// <summary>Writes <paramref name="saved"/> (atomically, protected).</summary>
    public void Save(SignedInUser saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        lock (_gate)
        {
            string path = FilePath;
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(SignedInUserFile.From(saved), AgentStateJson.Default.SignedInUserFile);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, _protector.Protect(plaintext));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                _logger.LogWarning(ex, "Sign-in of {UserId} could not be saved; an Agent restart would sign the player out", saved.User.Id);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <summary>Deletes the file (no-op when absent).</summary>
    public void Delete()
    {
        lock (_gate)
        {
            TryDelete(FilePath);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _users.Changed -= OnUsersChanged;
        _sessions.Changed -= OnSessionChanged;
        GC.SuppressFinalize(this);
    }

    private void OnUsersChanged(object? sender, EventArgs e) => Persist();

    private void OnSessionChanged(object? sender, SessionEvent e)
    {
        if (e.Type is SessionEventType.Started or SessionEventType.Ended)
        {
            Persist();
        }
    }

    /// <summary>Saved while the signed-in player has an open session here, deleted otherwise.</summary>
    private void Persist()
    {
        if (_disposed)
        {
            return;
        }

        User? user = _users.User;
        if (user is null || !_sessions.State.IsOpen() || _sessions.Current is not { } session || session.UserId != user.Id)
        {
            Delete();
            return;
        }

        Save(new SignedInUser(user, _users.ExpiresAt ?? _clock.UtcNow, _users.Mode, session.Id, _clock.UtcNow));
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Saved sign-in at {Path} could not be deleted", path);
        }
    }
}

/// <summary>Wire form of <see cref="SignedInUser"/> (the player as contract JSON inside).</summary>
/// <param name="Version">File version.</param>
/// <param name="User">The player, serialized by <see cref="JsonDefaults"/>.</param>
/// <param name="ExpiresAt">Token expiry as issued.</param>
/// <param name="Online">Checked by the server (vs the offline cache).</param>
/// <param name="SessionId">The player's open session when saved.</param>
/// <param name="SavedAt">Time of the save.</param>
internal sealed record SignedInUserFile(int Version, string User, DateTimeOffset ExpiresAt, bool Online, Guid SessionId, DateTimeOffset SavedAt)
{
    /// <summary>Current version.</summary>
    public const int CurrentVersion = 1;

    public static SignedInUserFile From(SignedInUser saved) =>
        new(CurrentVersion, JsonDefaults.Serialize(saved.User), saved.ExpiresAt, saved.Mode == ConnectivityState.Online, saved.SessionId, saved.SavedAt);

    public SignedInUser? ToRecord() =>
        JsonDefaults.Deserialize<User>(User) is { } user
            ? new SignedInUser(user, ExpiresAt, Online ? ConnectivityState.Online : ConnectivityState.Offline, SessionId, SavedAt)
            : null;
}

/// <summary>Source-generated JSON of the Agent's own state files (<c>signed-in-user.bin</c>, <c>session-lock.json</c>).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SignedInUserFile))]
[JsonSerializable(typeof(LockOriginRecord))]
internal sealed partial class AgentStateJson : JsonSerializerContext
{
}
