using System.Text.Json;
using ClubShell.Agent.Ipc.Handlers;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Users;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

public sealed class ShellSettingsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ShellSettingsStore _store;

    public ShellSettingsStoreTests()
    {
        var settings = new AgentSettings { PcName = "PC-TEST", Zone = "test" };
        settings.Paths.ProgramData = _dir.Root;
        _store = new ShellSettingsStore(TestSupport.Monitor(settings), NullLogger<ShellSettingsStore>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Settings_without_a_club_block_report_no_club()
    {
        Assert.Null(_store.Get().Club);
    }

    [Fact]
    public void The_admin_pin_comes_from_the_secure_folder_and_never_from_shell_json()
    {
        // shell.json is readable by the kiosk account: a hash there is ignored.
        File.WriteAllText(_store.FilePath, """{"kiosk":{"adminPinHash":"pbkdf2$1000$AAAA$BBBB"}}""");
        Assert.Null(_store.AdminPinHash);

        // What set-admin-pin.ps1 writes for 246810 (PowerShell's Rfc2898DeriveBytes, salt 01..10).
        const string fromScript = "pbkdf2$600000$AQIDBAUGBwgJCgsMDQ4PEA==$1eCGxgfZmbn5ifpTtpK9AF9WFNcVurDyW7TgsTCZpvc=";
        Directory.CreateDirectory(Path.GetDirectoryName(_store.AdminPinPath)!);
        File.WriteAllText(_store.AdminPinPath, fromScript + "\r\n");
        Assert.Equal(fromScript, _store.AdminPinHash);
        Assert.True(ClubShell.Agent.Session.OfflineSessionStore.VerifyPassword(fromScript, "246810"));
        Assert.False(ClubShell.Agent.Session.OfflineSessionStore.VerifyPassword(fromScript, "246811"));
    }

    [Fact]
    public void Server_club_block_is_persisted_and_exposed()
    {
        var club = new ShellClub(
            "CyberArena Tashkent",
            "#FF8A3D",
            "https://club.example/logo.png",
            null,
            [new ClubBanner("b1", "Night pack", "https://club.example/night.jpg")],
            new ClubRules("Читы запрещены.", "Chitlar taqiqlangan.", "No cheats."));

        _store.ApplyServerOverride(new ShellConfigOverride(Club: club));

        var read = _store.Get().Club;
        Assert.NotNull(read);
        Assert.Equal("CyberArena Tashkent", read.Name);
        Assert.Equal("#FF8A3D", read.Accent);
        Assert.Equal("Night pack", Assert.Single(read.Banners!).Title);
        Assert.Equal("No cheats.", read.Rules!.En);
        Assert.Contains("\"club\"", File.ReadAllText(_store.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Club_block_is_replaced_whole_so_removed_banners_disappear()
    {
        _store.ApplyServerOverride(new ShellConfigOverride(Club: new ShellClub(
            "Club",
            Banners: [new ClubBanner("b1", "One", "https://x/1.jpg"), new ClubBanner("b2", "Two", "https://x/2.jpg")])));

        _store.ApplyServerOverride(new ShellConfigOverride(Club: new ShellClub("Club", Banners: [])));

        Assert.Empty(_store.Get().Club!.Banners!);
    }

    [Fact]
    public void Override_without_club_keeps_the_existing_one()
    {
        _store.ApplyServerOverride(new ShellConfigOverride(Club: new ShellClub("Club")));

        using var features = JsonDocument.Parse("""{"shop":false}""");
        _store.ApplyServerOverride(new ShellConfigOverride(Features: features.RootElement.Clone()));

        var settings = _store.Get();
        Assert.Equal("Club", settings.Club!.Name);
        Assert.False(settings.Features.Shop);
    }

    [Fact]
    public void Malformed_club_block_reads_as_no_club()
    {
        File.WriteAllText(_store.FilePath, """{"version":1,"club":{"banners":"not-a-list"}}""");

        Assert.Null(_store.Get().Club);
    }

    [Fact]
    public void Locale_a_newer_server_sent_never_replaces_the_current_one()
    {
        File.WriteAllText(_store.FilePath, """{"version":1,"locale":"unknown"}""");
        Assert.Equal(Locale.Ru, _store.Get().Locale);

        _store.ApplyServerOverride(new ShellConfigOverride(Locale: Locale.Uz));
        _store.ApplyServerOverride(new ShellConfigOverride(Locale: Locale.Unknown));
        Assert.Equal(Locale.Uz, _store.Get().Locale);
    }
}
