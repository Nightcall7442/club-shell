using ClubShell.Agent.Ipc;
using ClubShell.Agent.Ipc.Handlers;
using ClubShell.Agent.Policy;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;

namespace ClubShell.Agent.Users;

/// <summary>
/// The member's Shell preferences and favourites follow their account, not the PC (D-86). At a member's sign-in an empty
/// overlay goes over the PC's settings at once (<see cref="ShellSettingsStore.SetAccount"/>), so whatever they change
/// from the first second is theirs and not the club's, and their saved preferences (<c>GET /users/{id}/prefs</c>) are
/// laid in when the server answers; a change the player made meanwhile wins over the saved value. Every change they make
/// is saved back (<c>PUT</c>), one request at a time with the latest state; a failed save is retried with the next
/// change, and a sign-in whose preferences could not be read reads them first, so a PC that was offline at sign-in never
/// overwrites the account with its half. At sign-out (or a guest, or another player) the overlay goes and the PC's own
/// settings are back. Each time the effective settings change without the Shell asking, <c>settings.changed</c> tells it,
/// so the theme, the language and the volume follow live.
/// </summary>
public sealed class AccountPrefsSync : IDisposable
{
    private readonly ShellUserContext _users;
    private readonly ShellSettingsStore _settings;
    private readonly IServerClient _server;
    private readonly IIpcEventPublisher _events;
    private readonly IPolicyEnforcer _enforcer;
    private readonly ILogger<AccountPrefsSync> _logger;
    private readonly object _gate = new();
    private Guid? _userId;
    private bool _loaded;
    private bool _dirty;
    private int _generation;
    private Task _pending = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Creates the sync and starts following the signed-in player.</summary>
    public AccountPrefsSync(
        ShellUserContext users,
        ShellSettingsStore settings,
        IServerClient server,
        IIpcEventPublisher events,
        IPolicyEnforcer enforcer,
        ILogger<AccountPrefsSync> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(enforcer);
        ArgumentNullException.ThrowIfNull(logger);
        _users = users;
        _settings = settings;
        _server = server;
        _events = events;
        _enforcer = enforcer;
        _logger = logger;
        _users.Changed += OnUsersChanged;
        _settings.AccountChanged += OnAccountChanged;
        OnUsersChanged(this, EventArgs.Empty);
    }

    /// <summary>Completes when the reads and saves started so far are done (tests, shutdown).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _pending;
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
        _settings.AccountChanged -= OnAccountChanged;
        GC.SuppressFinalize(this);
    }

    private void OnUsersChanged(object? sender, EventArgs e)
    {
        // A guest has no account to keep anything on: the PC's settings stay theirs to change, as before.
        Guid? member = _users.User is { Role: not UserRole.Guest } user ? user.Id : null;
        int generation;
        lock (_gate)
        {
            if (member == _userId)
            {
                return;
            }

            _userId = member;
            _loaded = false;
            _dirty = false;
            generation = ++_generation;
            _settings.SetAccount(member is null ? null : new UserPrefs());
        }

        Publish();
        if (member is { } id)
        {
            _logger.LogInformation("Account preferences of {UserId} over the PC's settings; reading the saved ones", id);
            Enqueue(() => LoadAsync(id, generation));
        }
        else
        {
            _logger.LogDebug("No member signed in: the PC's own settings apply");
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        Guid id;
        int generation;
        lock (_gate)
        {
            if (_userId is not { } member)
            {
                return;
            }

            id = member;
            generation = _generation;
            _dirty = true;
        }

        Enqueue(() => SaveAsync(id, generation));
    }

    /// <summary>Runs <paramref name="work"/> after the work queued before it: one request to the server at a time.</summary>
    private void Enqueue(Func<Task> work)
    {
        lock (_gate)
        {
            _pending = RunAsync(_pending, work);
        }
    }

    private async Task RunAsync(Task before, Func<Task> work)
    {
        await before.ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Account preferences could not be synced");
        }
    }

    /// <summary>Reads the saved preferences and lays them under what the player changed since sign-in; saves the merge when they changed something.</summary>
    private async Task<bool> LoadAsync(Guid userId, int generation)
    {
        UserPrefs saved;
        try
        {
            saved = await _server.GetUserPrefsAsync(userId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Account preferences of {UserId} could not be read; the PC's settings apply until they can", userId);
            return false;
        }

        bool save;
        lock (_gate)
        {
            if (generation != _generation || _settings.Account is not { } local)
            {
                return false;
            }

            _settings.SetAccount(new UserPrefs(
                local.Theme ?? saved.Theme,
                local.Locale ?? saved.Locale,
                local.Volume ?? saved.Volume,
                local.Muted ?? saved.Muted,
                local.UiSounds ?? saved.UiSounds,
                local.ShowMetricsOverlay ?? saved.ShowMetricsOverlay,
                local.Favorites ?? saved.Favorites ?? Array.Empty<string>()));
            _loaded = true;
            save = _dirty;
        }

        _logger.LogInformation("Account preferences of {UserId} applied", userId);
        Publish();
        if (save)
        {
            await SaveAsync(userId, generation).ConfigureAwait(false);
        }

        return true;
    }

    private async Task SaveAsync(Guid userId, int generation)
    {
        UserPrefs prefs;
        bool loaded;
        lock (_gate)
        {
            if (generation != _generation || !_dirty || _settings.Account is null)
            {
                return;
            }

            loaded = _loaded;
        }

        if (!loaded)
        {
            // The saved preferences were never read on this PC: read (and merge, then save) first, or the account
            // would lose everything this PC does not know.
            await LoadAsync(userId, generation).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            if (generation != _generation || _settings.Account is not { } account)
            {
                return;
            }

            prefs = account;
            _dirty = false;
        }

        try
        {
            await _server.SaveUserPrefsAsync(userId, prefs, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("Account preferences of {UserId} saved", userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _dirty = true;
                }
            }

            _logger.LogInformation(ex, "Account preferences of {UserId} could not be saved; retried with the next change", userId);
        }
    }

    private void Publish()
    {
        try
        {
            ShellSettings effective = PolicyHandlers.Effective(_settings.Get(), _settings, _enforcer);
            _ = _events.PublishAsync(IpcMessages.Events.SettingsChanged, effective, CancellationToken.None).AsTask();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "settings.changed not published");
        }
    }
}
