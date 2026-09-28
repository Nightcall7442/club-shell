using System.Text.Json;
using ClubShell.Agent.Ipc.Handlers;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Remote;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

/// <summary>
/// Everything that needs a server endpoint or can hurt a player stays off until the server's config turns it on
/// (SHELL_CHANGES items 4, 5, 12, 13).
/// </summary>
public sealed class ServerGatedDefaultsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Server_backed_shell_features_are_off_when_shell_json_does_not_mention_them()
    {
        var settings = new AgentSettings { PcName = "PC-TEST", Zone = "test" };
        settings.Paths.ProgramData = _dir.Root;
        var store = new ShellSettingsStore(TestSupport.Monitor(settings), NullLogger<ShellSettingsStore>.Instance);
        File.WriteAllText(store.FilePath, """{"version":1}""");

        ShellFeatures features = store.Features;

        features.Should().Be(new ShellFeatures(Shop: false, Chat: false, Booking: false, Tournaments: false, Profile: true, Topup: false, Apps: true, CallAdmin: true));
    }

    [Fact]
    public void Account_pool_cloud_saves_and_remote_input_are_off_until_the_server_config_says_otherwise()
    {
        var local = new AgentSettings();
        local.Games.AccountPool.Enabled.Should().BeFalse();
        local.Games.CloudSave.Enabled.Should().BeFalse();
        local.RemoteAdmin.AllowRemoteInput.Should().BeFalse();
        local.Updates.Enabled.Should().BeTrue();
        local.Anticheat.ReportViolations.Should().BeTrue();

        using var games = JsonDocument.Parse("""{"accountPool":{"enabled":true},"cloudSave":{"enabled":true}}""");
        using var anticheat = JsonDocument.Parse("""{"reportViolations":false}""");
        var config = new AgentServerConfig(
            1,
            "",
            "",
            1,
            Games: games.RootElement.Clone(),
            Updates: new UpdatesConfigOverride(Enabled: false),
            Anticheat: anticheat.RootElement.Clone());
        var merged = SettingsJson.ToNode(local);
        SettingsJson.Merge(merged, SettingsJson.FromServerConfig(config));
        AgentSettings effective = SettingsJson.FromNode(merged);

        effective.Games.AccountPool.Enabled.Should().BeTrue();
        effective.Games.CloudSave.Enabled.Should().BeTrue();
        effective.Updates.Enabled.Should().BeFalse();
        effective.Anticheat.ReportViolations.Should().BeFalse();
    }

    [Fact]
    public void Remote_input_is_refused_while_a_game_with_an_anti_cheat_runs()
    {
        Game dota = TestSupport.Game("Dota 2", LauncherType.Steam);

        RemoteInputService.BlocksRemoteInput([dota]).Should().BeFalse();
        RemoteInputService.BlocksRemoteInput([dota, dota with { AntiCheat = AntiCheatKind.Eac }]).Should().BeTrue();
        RemoteInputService.BlocksRemoteInput([TestSupport.Game("VALORANT", LauncherType.Riot)]).Should().BeTrue("Riot titles run Vanguard even without a catalogue tag");
    }

    [Fact]
    public void Web_filter_never_blocks_the_server_host_or_domains_the_server_protects()
    {
        var filter = new WebFilterPolicy(
            true,
            ["club-server.example", "api.club-server.example", "cdn.partner.example", "bad.example", "steampowered.com"],
            [],
            [],
            ProtectedDomains: ["*.Partner.Example."]);

        IReadOnlyList<string> protectedDomains = WebFilterPolicyModule.ProtectedDomainsFor(filter, "https://club-server.example/api/v1");

        DnsFilter.NormalizeDomains(filter.BlockedDomains, protectedDomains).Should().Equal("bad.example");
        protectedDomains.Should().Contain(DnsFilter.ProtectedDomains).And.Contain("partner.example");
        var policy = PolicyFactory.Create();
        policy.DiffSections(policy with { WebFilter = policy.WebFilter with { BlockResolvedIps = true } }).Should().Equal("webFilter");
    }
}
