using System.Text.Json.Nodes;
using ClubShell.Agent.Storage;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

/// <summary>SHELL_CHANGES 41: the club key leaves agent.json for secure\club.key.</summary>
public sealed class ClubKeyMigrationTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("clubshell-clubkey-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task A_key_in_agent_json_moves_into_secure_and_is_blanked_there()
    {
        using var loader = await LoadAsync(Config("club-key-1"));
        var store = Store(loader);
        using var migration = new ClubKeyMigration(loader, store, NullLogger<ClubKeyMigration>.Instance);

        await migration.StartAsync(CancellationToken.None);

        store.Read().Should().Be("club-key-1");
        File.Exists(Path.Combine(_dir.FullName, "secure", "club.key")).Should().BeTrue();
        ClubKeyInFile().Should().BeEmpty();
        loader.Current.Server.ClubApiKey.Should().BeEmpty();
        loader.Current.Paths.ProgramData.Should().Be(_dir.FullName, "the rest of agent.json is kept");
        await migration.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_new_key_written_to_agent_json_replaces_the_stored_one()
    {
        using var loader = await LoadAsync(Config("club-key-1"));
        var store = Store(loader);
        using var migration = new ClubKeyMigration(loader, store, NullLogger<ClubKeyMigration>.Instance);
        (await migration.MigrateAsync(CancellationToken.None)).Should().BeTrue();

        await File.WriteAllTextAsync(ConfigPath, Config("club-key-2").ToJsonString());
        await loader.ReloadAsync(CancellationToken.None);

        (await migration.MigrateAsync(CancellationToken.None)).Should().BeTrue();
        store.Read().Should().Be("club-key-2");
        ClubKeyInFile().Should().BeEmpty();
        (await migration.MigrateAsync(CancellationToken.None)).Should().BeFalse("nothing is left to move");
    }

    [Fact]
    public async Task A_key_from_the_environment_or_a_blank_key_writes_nothing()
    {
        string before = Config("").ToJsonString();
        using var loader = await LoadAsync(Config(""), new Dictionary<string, string?> { ["CLUBSHELL__Server__ClubApiKey"] = "env-key" });
        var store = Store(loader);
        using var migration = new ClubKeyMigration(loader, store, NullLogger<ClubKeyMigration>.Instance);

        (await migration.MigrateAsync(CancellationToken.None)).Should().BeFalse();

        store.Read().Should().BeNull();
        (await File.ReadAllTextAsync(ConfigPath)).Should().Be(before);
        loader.Current.Server.ClubApiKey.Should().Be("env-key");
    }

    [Fact]
    public async Task A_key_that_cannot_be_stored_stays_in_agent_json()
    {
        using var loader = await LoadAsync(Config("club-key-1"));
        // A file where the secure folder should be: the key file cannot be written.
        await File.WriteAllTextAsync(Path.Combine(_dir.FullName, "secure"), "not a folder");
        var store = Store(loader);
        using var migration = new ClubKeyMigration(loader, store, NullLogger<ClubKeyMigration>.Instance);

        (await migration.MigrateAsync(CancellationToken.None)).Should().BeFalse();

        ClubKeyInFile().Should().Be("club-key-1");
        loader.Current.Server.ClubApiKey.Should().Be("club-key-1", "registration keeps working with it");
    }

    private string ConfigPath => Path.Combine(_dir.FullName, "agent.json");

    private JsonObject Config(string key) => new()
    {
        ["server"] = new JsonObject { ["clubApiKey"] = key },
        ["paths"] = new JsonObject { ["programData"] = _dir.FullName },
    };

    private async Task<SettingsLoader> LoadAsync(JsonObject config, Dictionary<string, string?>? environment = null)
    {
        await File.WriteAllTextAsync(ConfigPath, config.ToJsonString());
        var loader = new SettingsLoader(new SettingsLoaderOptions
        {
            ConfigPath = ConfigPath,
            DefaultsPath = Path.Combine(_dir.FullName, "missing.default.json"),
            CreateConfigIfMissing = false,
            Watch = false,
            EnvironmentOverride = environment ?? new Dictionary<string, string?>(),
        });
        await loader.LoadAsync(CancellationToken.None);
        return loader;
    }

    private static ClubKeyStore Store(SettingsLoader loader) =>
        new(loader.Current.ClubKeyPath, new NullTokenProtector(), NullLogger<ClubKeyStore>.Instance);

    private string? ClubKeyInFile() => JsonNode.Parse(File.ReadAllText(ConfigPath))!["server"]!["clubApiKey"]!.GetValue<string>();
}
