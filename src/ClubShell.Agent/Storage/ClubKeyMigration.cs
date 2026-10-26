using System.Text.Json.Nodes;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;

namespace ClubShell.Agent.Storage;

/// <summary>
/// Moves the club key out of <c>agent.json</c>, which every local account can read, into <c>secure\club.key</c>
/// (<see cref="ClubKeyStore"/>, SHELL_CHANGES 41): at start and whenever <c>agent.json</c> gets a key again (the
/// installer's <c>CLUBAPIKEY</c>, an edit). The key is blanked in <c>agent.json</c> only once it is stored; until then
/// it stays there and registration uses it. A key given through the environment is never in the file and stays where it is.
/// Registered after <see cref="PersistentStateDirectory"/>, which gives <c>paths.persistent</c> its ACL first.
/// </summary>
public sealed class ClubKeyMigration : IHostedService, IDisposable
{
    private readonly SettingsLoader _loader;
    private readonly ClubKeyStore _store;
    private readonly ILogger<ClubKeyMigration> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _stopping;

    /// <summary>Creates the service.</summary>
    public ClubKeyMigration(SettingsLoader loader, ClubKeyStore store, ILogger<ClubKeyMigration> logger)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _loader = loader;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        await MigrateAsync(cancellationToken).ConfigureAwait(false);
        _loader.Changed += OnSettingsChanged;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _loader.Changed -= OnSettingsChanged;
        _stopping?.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loader.Changed -= OnSettingsChanged;
        _stopping?.Dispose();
        _gate.Dispose();
    }

    /// <summary>Moves a key found in <c>agent.json</c>; <see langword="true"/> when one was moved.</summary>
    public async Task<bool> MigrateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool moved = await _loader.TryPatchAsync(TakeKey, cancellationToken).ConfigureAwait(false);
            if (moved)
            {
                _logger.LogInformation("Club key moved from agent.json into {Path}", _store.FilePath);
            }

            return moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // agent.json keeps the key and registration still works with it; the next start tries again.
            _logger.LogWarning(ex, "Could not move the club key out of agent.json");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TakeKey(JsonObject document)
    {
        if (SettingsJson.FindKey(document, "server") is not { } serverKey || document[serverKey] is not JsonObject server)
        {
            return false;
        }

        if (SettingsJson.FindKey(server, "clubApiKey") is not { } keyName
            || server[keyName] is not JsonValue value
            || !value.TryGetValue(out string? key)
            || string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (!_store.Store(key))
        {
            return false;
        }

        server[keyName] = "";
        return true;
    }

    private void OnSettingsChanged(object? sender, AgentSettings settings)
    {
        // Raised inside the loader's reload; the move reloads again, so it runs after this one returns.
        if (string.IsNullOrEmpty(settings.Server.ClubApiKey) || _stopping is not { IsCancellationRequested: false } stopping)
        {
            return;
        }

        _ = Task.Run(() => MigrateQuietlyAsync(stopping.Token), CancellationToken.None);
    }

    private async Task MigrateQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not move the club key out of agent.json");
        }
    }
}
