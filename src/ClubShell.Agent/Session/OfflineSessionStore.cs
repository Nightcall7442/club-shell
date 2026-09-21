using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Session;

/// <summary>The <c>POST /sessions</c> call still owed to the server for a session created offline.</summary>
/// <param name="Request">Create request (<c>startedAt</c> and <c>clientSessionId</c> set).</param>
/// <param name="IdempotencyKey">Key reused on every replay so the server never creates a duplicate.</param>
public sealed record OfflineSessionCreate(SessionCreateRequest Request, Guid IdempotencyKey);

/// <summary>Session row loaded from the store.</summary>
/// <param name="Session">Persisted session (seconds as of <paramref name="UpdatedAt"/>).</param>
/// <param name="UpdatedAt">Time of the last save; the restorer subtracts the gap from the remaining time.</param>
/// <param name="PendingCreate">Create call still owed to the server, when created offline.</param>
public sealed record StoredSession(PlaySession Session, DateTimeOffset UpdatedAt, OfflineSessionCreate? PendingCreate);

/// <summary>Outbox row.</summary>
/// <param name="Id">Row id (FIFO order).</param>
/// <param name="SessionId">Target session.</param>
/// <param name="Event">Event to replay.</param>
/// <param name="CreatedAt">Enqueue time.</param>
/// <param name="Attempts">Delivery attempts that ended in a non-retryable error.</param>
public sealed record QueuedSessionEvent(long Id, Guid SessionId, SessionEvent Event, DateTimeOffset CreatedAt, int Attempts);

/// <summary>Outbox counters.</summary>
/// <param name="Pending">Events waiting for delivery.</param>
/// <param name="DeadLettered">Events given up on.</param>
/// <param name="OldestPendingAt">Enqueue time of the oldest pending event.</param>
public sealed record OfflineQueueStats(int Pending, int DeadLettered, DateTimeOffset? OldestPendingAt);

/// <summary>Outcome of one <see cref="OfflineSessionStore.FlushAsync"/> round; maps onto <c>offlineQueueFlushed</c>.</summary>
/// <param name="Sent">Events acknowledged by the server.</param>
/// <param name="DeadLettered">Events moved to the dead-letter set during this round.</param>
/// <param name="Remaining">Events still pending (server unreachable or backoff active).</param>
/// <param name="OfflineFrom">Enqueue time of the oldest event sent in this round.</param>
/// <param name="OfflineTo">Enqueue time of the newest event sent in this round.</param>
public sealed record OfflineFlushResult(int Sent, int DeadLettered, int Remaining, DateTimeOffset? OfflineFrom, DateTimeOffset? OfflineTo)
{
    /// <summary>Round that sent nothing and changed nothing.</summary>
    public static OfflineFlushResult Empty { get; } = new(0, 0, 0, null, null);
}

/// <summary>
/// SQLite store (<c>offline.storePath</c>, WAL) holding the current session, the session-event outbox and the offline
/// login cache (ARCHITECTURE.md §8). One connection per operation (Microsoft.Data.Sqlite pools them); writes are
/// serialized by a semaphore so callers never see <c>SQLITE_BUSY</c>.
/// </summary>
public sealed class OfflineSessionStore : IDisposable
{
    /// <summary>Non-retryable delivery failures after which an event is dead-lettered.</summary>
    public const int MaxDeliveryAttempts = 5;

    /// <summary>PBKDF2-SHA256 iterations used by <see cref="HashPassword"/>.</summary>
    public const int Pbkdf2Iterations = 210_000;

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(30);
    private const int DeadLetterCap = 1000;

    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<OfflineSessionStore> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;
    private long _nextFlushAllowedAt;
    private TimeSpan _backoff = MinBackoff;
    private bool _backoffActive;

    /// <summary>Creates the store; the file is created on first use.</summary>
    public OfflineSessionStore(IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<OfflineSessionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _clock = clock;
        _logger = logger;
        var current = settings.CurrentValue;
        DatabasePath = current.ResolvePath(current.Offline.StorePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Absolute path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Time before which <see cref="FlushAsync"/> returns without contacting the server (exponential backoff after a failure).</summary>
    public TimeSpan FlushBackoffRemaining
    {
        get
        {
            if (!_backoffActive)
            {
                return TimeSpan.Zero;
            }

            var elapsed = _clock.GetElapsedTime(_nextFlushAllowedAt);
            return elapsed >= TimeSpan.Zero ? TimeSpan.Zero : -elapsed;
        }
    }

    /// <summary>Creates the schema when missing and switches the file to WAL. Idempotent; every operation calls it.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS sessions(
                    id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    state TEXT NOT NULL,
                    updatedAt TEXT NOT NULL,
                    createJson TEXT NULL,
                    createKey TEXT NULL);
                CREATE TABLE IF NOT EXISTS events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    sessionId TEXT NOT NULL,
                    json TEXT NOT NULL,
                    createdAt TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    deadLetter INTEGER NOT NULL DEFAULT 0,
                    lastError TEXT NULL);
                CREATE INDEX IF NOT EXISTS ix_events_pending ON events(deadLetter, id);
                CREATE TABLE IF NOT EXISTS users(
                    username TEXT PRIMARY KEY,
                    userId TEXT NOT NULL,
                    hash TEXT NULL,
                    userJson TEXT NOT NULL,
                    cachedAt TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_users_userId ON users(userId);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
            _logger.LogInformation("Offline store ready at {Path}", DatabasePath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    #region Sessions

    /// <summary>Upserts the current session (only one row is kept) together with the create call still owed to the server.</summary>
    public async Task SaveSessionAsync(PlaySession session, OfflineSessionCreate? pendingCreate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM sessions WHERE id <> @id;
                INSERT INTO sessions(id, json, state, updatedAt, createJson, createKey)
                VALUES (@id, @json, @state, @updatedAt, @createJson, @createKey)
                ON CONFLICT(id) DO UPDATE SET
                    json = excluded.json,
                    state = excluded.state,
                    updatedAt = excluded.updatedAt,
                    createJson = excluded.createJson,
                    createKey = excluded.createKey;
                """;
            command.Parameters.AddWithValue("@id", session.Id.ToString("D"));
            command.Parameters.AddWithValue("@json", JsonDefaults.Serialize(session));
            command.Parameters.AddWithValue("@state", StateName(session.State));
            command.Parameters.AddWithValue("@updatedAt", Stamp(_clock.UtcNow));
            command.Parameters.AddWithValue("@createJson", pendingCreate is null ? DBNull.Value : (object)JsonDefaults.Serialize(pendingCreate.Request));
            command.Parameters.AddWithValue("@createKey", pendingCreate is null ? DBNull.Value : (object)pendingCreate.IdempotencyKey.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Loads the persisted open session, or <see langword="null"/> when none (ended rows are ignored).</summary>
    public async Task<StoredSession?> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json, updatedAt, createJson, createKey FROM sessions
            WHERE state NOT IN ('idle', 'ended')
            ORDER BY updatedAt DESC LIMIT 1;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        PlaySession? session;
        try
        {
            session = JsonDefaults.Deserialize<PlaySession>(reader.GetString(0));
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Persisted session is unreadable and will be discarded");
            return null;
        }

        if (session is null)
        {
            return null;
        }

        var updatedAt = ParseStamp(reader.GetString(1));
        OfflineSessionCreate? pending = null;
        if (!reader.IsDBNull(2) && !reader.IsDBNull(3))
        {
            var request = JsonDefaults.Deserialize<SessionCreateRequest>(reader.GetString(2));
            if (request is not null && Guid.TryParse(reader.GetString(3), out var key))
            {
                pending = new OfflineSessionCreate(request, key);
            }
        }

        return new StoredSession(session, updatedAt, pending);
    }

    /// <summary>Removes every persisted session row (called once a session is settled).</summary>
    public async Task ClearSessionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sessions;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Renames a session (the server declined the client-generated id at replay): the session row and every queued event
    /// are rewritten with <paramref name="newId"/>.
    /// </summary>
    public async Task RemapSessionAsync(Guid oldId, Guid newId, CancellationToken cancellationToken)
    {
        if (oldId == newId)
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var rewrites = new List<(long Id, string Json)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id, json FROM events WHERE sessionId = @old;";
                select.Parameters.AddWithValue("@old", oldId.ToString("D"));
                using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var sessionEvent = JsonDefaults.Deserialize<SessionEvent>(reader.GetString(1));
                    if (sessionEvent is not null)
                    {
                        rewrites.Add((reader.GetInt64(0), JsonDefaults.Serialize(sessionEvent with { SessionId = newId })));
                    }
                }
            }

            foreach (var (id, json) in rewrites)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE events SET sessionId = @new, json = @json WHERE id = @id;";
                update.Parameters.AddWithValue("@new", newId.ToString("D"));
                update.Parameters.AddWithValue("@json", json);
                update.Parameters.AddWithValue("@id", id);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var session = connection.CreateCommand())
            {
                session.Transaction = transaction;
                session.CommandText = "UPDATE sessions SET id = @new WHERE id = @old;";
                session.Parameters.AddWithValue("@new", newId.ToString("D"));
                session.Parameters.AddWithValue("@old", oldId.ToString("D"));
                await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
            _logger.LogInformation("Remapped offline session {OldId} to server id {NewId} ({Events} queued events rewritten)", oldId, newId, rewrites.Count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    #region Outbox

    /// <summary>Appends an event to the outbox. When <c>offline.maxQueue</c> is reached the oldest pending event is dropped.</summary>
    public async Task EnqueueEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var max = Math.Max(1, _settings.CurrentValue.Offline.MaxQueue);
            using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM events WHERE deadLetter = 0;";
                var pending = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (pending >= max)
                {
                    using var drop = connection.CreateCommand();
                    drop.CommandText = "DELETE FROM events WHERE id IN (SELECT id FROM events WHERE deadLetter = 0 ORDER BY id LIMIT @n);";
                    drop.Parameters.AddWithValue("@n", pending - max + 1);
                    var dropped = await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning("Offline outbox full ({Max}); dropped {Dropped} oldest event(s)", max, dropped);
                }
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO events(sessionId, json, createdAt) VALUES (@sessionId, @json, @createdAt);";
            insert.Parameters.AddWithValue("@sessionId", sessionEvent.SessionId.ToString("D"));
            insert.Parameters.AddWithValue("@json", JsonDefaults.Serialize(sessionEvent));
            insert.Parameters.AddWithValue("@createdAt", Stamp(_clock.UtcNow));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Oldest pending events (FIFO), at most <paramref name="max"/>. Rows stay in the outbox until <see cref="AckAsync"/>.</summary>
    public async Task<IReadOnlyList<QueuedSessionEvent>> DequeueBatchAsync(int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, sessionId, json, createdAt, attempts FROM events WHERE deadLetter = 0 ORDER BY id LIMIT @max;";
        command.Parameters.AddWithValue("@max", max);
        var result = new List<QueuedSessionEvent>();
        var corrupt = new List<long>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                SessionEvent? sessionEvent = null;
                try
                {
                    sessionEvent = JsonDefaults.Deserialize<SessionEvent>(reader.GetString(2));
                }
                catch (System.Text.Json.JsonException)
                {
                    // Handled below.
                }

                if (sessionEvent is null || !Guid.TryParse(reader.GetString(1), out var sessionId))
                {
                    corrupt.Add(id);
                    continue;
                }

                result.Add(new QueuedSessionEvent(id, sessionId, sessionEvent, ParseStamp(reader.GetString(3)), reader.GetInt32(4)));
            }
        }

        if (corrupt.Count > 0)
        {
            await DeadLetterAsync(corrupt, "unreadable row", cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Deletes delivered events.</summary>
    public Task AckAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        UpdateByIdsAsync("DELETE FROM events WHERE id IN ({0});", ids, null, cancellationToken);

    /// <summary>Records a non-retryable failure; rows reaching <see cref="MaxDeliveryAttempts"/> are dead-lettered.</summary>
    public Task MarkFailedAsync(IReadOnlyCollection<long> ids, string error, CancellationToken cancellationToken) =>
        UpdateByIdsAsync(
            "UPDATE events SET attempts = attempts + 1, lastError = @error, deadLetter = CASE WHEN attempts + 1 >= " + MaxDeliveryAttempts.ToString(CultureInfo.InvariantCulture) + " THEN 1 ELSE 0 END WHERE id IN ({0});",
            ids,
            error,
            cancellationToken);

    /// <summary>Gives up on the events (kept for diagnostics until <see cref="PurgeAsync"/>).</summary>
    public Task DeadLetterAsync(IReadOnlyCollection<long> ids, string reason, CancellationToken cancellationToken) =>
        UpdateByIdsAsync("UPDATE events SET deadLetter = 1, lastError = @error WHERE id IN ({0});", ids, reason, cancellationToken);

    /// <summary>Outbox counters for heartbeats and telemetry.</summary>
    public async Task<OfflineQueueStats> GetQueueStatsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM events WHERE deadLetter = 0),
                (SELECT COUNT(*) FROM events WHERE deadLetter = 1),
                (SELECT MIN(createdAt) FROM events WHERE deadLetter = 0);
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new OfflineQueueStats(0, 0, null);
        }

        return new OfflineQueueStats(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : ParseStamp(reader.GetString(2)));
    }

    /// <summary>
    /// Replays the outbox through <c>POST /sessions/{id}/events</c> in FIFO batches of ≤ 100 grouped by session. A retryable
    /// failure (server unreachable, auth) stops the round and arms an exponential backoff (5 s → 5 min); any other server
    /// error counts one attempt and dead-letters the batch after <see cref="MaxDeliveryAttempts"/>.
    /// </summary>
    public async Task<OfflineFlushResult> FlushAsync(IServerClient server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (FlushBackoffRemaining > TimeSpan.Zero)
        {
            var stats = await GetQueueStatsAsync(cancellationToken).ConfigureAwait(false);
            return new OfflineFlushResult(0, 0, stats.Pending, null, null);
        }

        var sent = 0;
        var deadLettered = 0;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        var stopped = false;
        while (!stopped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await DequeueBatchAsync(SessionEventsBatch.MaxEvents, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            var sessionId = batch[0].SessionId;
            var run = new List<QueuedSessionEvent>();
            foreach (var item in batch)
            {
                if (item.SessionId != sessionId)
                {
                    break;
                }

                run.Add(item);
            }

            var ids = run.Select(e => e.Id).ToArray();
            var key = BatchKey(sessionId, ids[0], ids[^1]);
            try
            {
                await server.PostSessionEventsAsync(sessionId, new SessionEventsBatch(run.Select(e => e.Event).ToArray()), key, cancellationToken).ConfigureAwait(false);
                await AckAsync(ids, cancellationToken).ConfigureAwait(false);
                sent += run.Count;
                from ??= run[0].CreatedAt;
                to = run[^1].CreatedAt;
                ResetBackoff();
            }
            catch (ServerApiException ex) when (ex.IsRetryable || ex.IsAuthFailure)
            {
                _logger.LogWarning("Offline flush paused: {Code} ({Message}); next attempt in {Backoff}", ex.Code, ex.Message, _backoff);
                ArmBackoff();
                stopped = true;
            }
            catch (ServerApiException ex)
            {
                var exhausted = run.Count(e => e.Attempts + 1 >= MaxDeliveryAttempts);
                await MarkFailedAsync(ids, $"{ex.Code}: {ex.Message}", cancellationToken).ConfigureAwait(false);
                deadLettered += exhausted;
                _logger.LogWarning("Offline flush batch for session {SessionId} rejected: {Code} ({Message}); {DeadLettered} dead-lettered", sessionId, ex.Code, ex.Message, exhausted);
                if (ex.Code == ErrorCode.NotFound)
                {
                    // The session does not exist server-side; nothing else for it will succeed either.
                    ArmBackoff();
                    stopped = true;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (ex is HttpRequestException or OperationCanceledException or TimeoutException))
            {
                _logger.LogWarning(ex, "Offline flush transport failure; next attempt in {Backoff}", _backoff);
                ArmBackoff();
                stopped = true;
            }
        }

        var remaining = (await GetQueueStatsAsync(cancellationToken).ConfigureAwait(false)).Pending;
        if (sent > 0 || deadLettered > 0)
        {
            _logger.LogInformation("Offline flush: sent={Sent} deadLettered={DeadLettered} remaining={Remaining}", sent, deadLettered, remaining);
        }

        return new OfflineFlushResult(sent, deadLettered, remaining, from, to);
    }

    #endregion

    #region Users

    /// <summary>
    /// Caches a user for offline login. A <see langword="null"/> <paramref name="offlineHash"/> (QR/card/token login) keeps the
    /// previously stored password hash.
    /// </summary>
    public async Task CacheUserAsync(string username, string? offlineHash, User user, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(user);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO users(username, userId, hash, userJson, cachedAt)
                VALUES (@username, @userId, @hash, @userJson, @cachedAt)
                ON CONFLICT(username) DO UPDATE SET
                    userId = excluded.userId,
                    hash = COALESCE(excluded.hash, users.hash),
                    userJson = excluded.userJson,
                    cachedAt = excluded.cachedAt;
                """;
            command.Parameters.AddWithValue("@username", NormalizeUsername(username));
            command.Parameters.AddWithValue("@userId", user.Id.ToString("D"));
            command.Parameters.AddWithValue("@hash", string.IsNullOrWhiteSpace(offlineHash) ? DBNull.Value : (object)offlineHash);
            command.Parameters.AddWithValue("@userJson", JsonDefaults.Serialize(user));
            command.Parameters.AddWithValue("@cachedAt", Stamp(_clock.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Cached profile of <paramref name="userId"/> (any age), or <see langword="null"/>.</summary>
    public async Task<User?> GetCachedUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT userJson FROM users WHERE userId = @userId ORDER BY cachedAt DESC LIMIT 1;";
        command.Parameters.AddWithValue("@userId", userId.ToString("D"));
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : JsonDefaults.Deserialize<User>(json);
    }

    /// <summary>
    /// Verifies <paramref name="password"/> against the cached hash of <paramref name="username"/>. Returns the cached profile,
    /// or <see langword="null"/> when the user is unknown, the cache is older than <c>offline.userCacheTtlHours</c>, no
    /// password hash was stored, or the password is wrong.
    /// </summary>
    public async Task<User?> TryOfflineLoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hash, userJson, cachedAt FROM users WHERE username = @username;";
        command.Parameters.AddWithValue("@username", NormalizeUsername(username));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        var ttl = TimeSpan.FromHours(Math.Max(1, _settings.CurrentValue.Offline.UserCacheTtlHours));
        var cachedAt = ParseStamp(reader.GetString(2));
        if (_clock.UtcNow - cachedAt > ttl)
        {
            _logger.LogInformation("Offline login for {Username} refused: cache expired", username);
            return null;
        }

        if (!VerifyPassword(reader.GetString(0), password))
        {
            return null;
        }

        return JsonDefaults.Deserialize<User>(reader.GetString(1));
    }

    #endregion

    /// <summary>
    /// Drops expired user cache rows and old/excess dead-lettered events, then runs <c>VACUUM</c> when anything was removed.
    /// </summary>
    /// <returns>Rows deleted.</returns>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.UtcNow;
            var deleted = 0;
            using (var users = connection.CreateCommand())
            {
                users.CommandText = "DELETE FROM users WHERE cachedAt < @before;";
                users.Parameters.AddWithValue("@before", Stamp(now - TimeSpan.FromHours(Math.Max(1, _settings.CurrentValue.Offline.UserCacheTtlHours))));
                deleted += await users.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var old = connection.CreateCommand())
            {
                old.CommandText = "DELETE FROM events WHERE deadLetter = 1 AND createdAt < @before;";
                old.Parameters.AddWithValue("@before", Stamp(now - DeadLetterRetention));
                deleted += await old.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var excess = connection.CreateCommand())
            {
                excess.CommandText = "DELETE FROM events WHERE deadLetter = 1 AND id NOT IN (SELECT id FROM events WHERE deadLetter = 1 ORDER BY id DESC LIMIT @cap);";
                excess.Parameters.AddWithValue("@cap", DeadLetterCap);
                deleted += await excess.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (deleted > 0)
            {
                using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM;";
                await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Offline store purged {Deleted} row(s)", deleted);
            }

            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Hashes a secret as <c>pbkdf2$&lt;iterations&gt;$&lt;salt-base64&gt;$&lt;hash-base64&gt;</c> (PBKDF2-SHA256, 16-byte salt, 32-byte hash).</summary>
    public static string HashPassword(string password, int iterations = Pbkdf2Iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1000);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return string.Create(CultureInfo.InvariantCulture, $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>
    /// Verifies <paramref name="password"/> against a <see cref="HashPassword"/> string. Any other format (including Argon2id
    /// PHC strings, which this build cannot evaluate) fails closed.
    /// </summary>
    public static bool VerifyPassword(string encoded, string password)
    {
        if (string.IsNullOrEmpty(encoded) || password is null)
        {
            return false;
        }

        var parts = encoded.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], "pbkdf2", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1 || iterations > 10_000_000)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _writeLock.Dispose();
        _initLock.Dispose();
        using var probe = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(probe);
        GC.SuppressFinalize(this);
    }

    private static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    private static string StateName(SessionState state)
    {
        var name = state.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : DateTimeOffset.UnixEpoch;

    private static Guid BatchKey(Guid sessionId, long firstId, long lastId)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{sessionId:D}:{firstId}:{lastId}"));
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return new Guid(digest[..16]);
    }

    private void ArmBackoff()
    {
        _backoffActive = true;
        _nextFlushAllowedAt = _clock.GetTimestamp() + (long)(_backoff.TotalSeconds * _clock.Provider.TimestampFrequency);
        var next = TimeSpan.FromTicks(_backoff.Ticks * 2);
        _backoff = next > MaxBackoff ? MaxBackoff : next;
    }

    private void ResetBackoff()
    {
        _backoffActive = false;
        _backoff = MinBackoff;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task UpdateByIdsAsync(string sqlTemplate, IReadOnlyCollection<long> ids, string? error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            var names = new List<string>(ids.Count);
            var index = 0;
            foreach (var id in ids)
            {
                var name = "@p" + index.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                command.Parameters.AddWithValue(name, id);
                index++;
            }

            command.CommandText = string.Format(CultureInfo.InvariantCulture, sqlTemplate, string.Join(",", names));
            if (error is not null)
            {
                command.Parameters.AddWithValue("@error", error.Length > 512 ? error[..512] : error);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
