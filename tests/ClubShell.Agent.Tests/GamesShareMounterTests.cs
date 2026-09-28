using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Agent.Storage;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using ClubShell.Windows.Network;
using ClubShell.Windows.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Tests;

/// <summary>Who owns the games library volume (the Agent's SMB <c>storage.gamesShare</c> or ClubDisklessHelper) and how the Agent follows its settings.</summary>
public sealed class GamesShareMounterTests
{
    private static readonly IscsiSettings LegacyIscsi = new() { Portal = "10.0.0.5:3260", TargetIqn = "iqn.2026-09.uz.club:games-7" };

    [Fact]
    public async Task InstalledDisklessHelper_OwnsTheLibrary_EvenWhenGamesShareIsConfigured()
    {
        var settings = new AgentSettings();
        settings.Storage.GamesShare.Enabled = true;
        var logger = new ListLogger();
        using var network = new NetworkProbe();
        using var mounter = Create(TestSupport.Monitor(settings), network, logger, helperInstalled: true);

        await mounter.StartAsync(CancellationToken.None);
        await mounter.StopAsync(CancellationToken.None);

        mounter.OwnedByDisklessHelper.Should().BeTrue();
        mounter.IsMounted.Should().BeFalse();
        mounter.VolumeState().Should().Be(new HeartbeatGamesVolume(GamesVolumeOwner.DisklessHelper, null, null, null));
        // The stand-down warning is the only thing that happened: no mount loop, no unmap on stop.
        logger.Entries.Should().ContainSingle()
            .Which.Should().Match<(LogLevel Level, string Message)>(e => e.Level == LogLevel.Warning && e.Message.Contains(GamesShareMounter.DisklessHelperServiceName));
    }

    [Fact]
    public async Task WithoutTheHelper_TheAgentKeepsItsOwnGamesShareSetting()
    {
        var settings = new AgentSettings();
        var logger = new ListLogger();
        using var network = new NetworkProbe();
        using var mounter = Create(TestSupport.Monitor(settings), network, logger, helperInstalled: false);

        await mounter.StartAsync(CancellationToken.None);
        await mounter.StopAsync(CancellationToken.None);

        mounter.OwnedByDisklessHelper.Should().BeFalse();
        mounter.VolumeState().Should().Be(new HeartbeatGamesVolume(GamesVolumeOwner.None, false, null, null));
        logger.Entries.Should().ContainSingle(e => e.Message.Contains("disabled"));
    }

    [Fact]
    public async Task IscsiConfig_MountsNothing_AndSaysTheHelperHandlesIt()
    {
        var settings = new AgentSettings();
        settings.Storage.GamesShare.Enabled = true;
        settings.Storage.GamesShare.Iscsi = LegacyIscsi;
        var logger = new ListLogger();
        using var network = new NetworkProbe();
        using var mounter = Create(TestSupport.Monitor(settings), network, logger, helperInstalled: false);

        await mounter.StartAsync(CancellationToken.None);
        await mounter.StopAsync(CancellationToken.None);

        mounter.IsMounted.Should().BeFalse();
        mounter.VolumeState().Should().Be(new HeartbeatGamesVolume(GamesVolumeOwner.Agent, false, "G", null));
        // The refusal is the only thing that happened: no map attempt, no unmap on stop.
        logger.Entries.Should().ContainSingle()
            .Which.Should().Match<(LogLevel Level, string Message)>(e => e.Level == LogLevel.Error && e.Message.Contains(LegacyIscsi.TargetIqn) && e.Message.Contains(GamesShareMounter.DisklessHelperServiceName));
    }

    [Fact]
    public async Task SettingsChange_AfterStart_IsFollowedWithoutARestart()
    {
        var current = new AgentSettings();
        Action<AgentSettings, string?>? listener = null;
        var monitor = Substitute.For<IOptionsMonitor<AgentSettings>>();
        monitor.CurrentValue.Returns(_ => current);
        monitor.OnChange(Arg.Do<Action<AgentSettings, string?>>(l => listener = l)).Returns(Substitute.For<IDisposable>());
        var logger = new ListLogger();
        using var network = new NetworkProbe();
        using var mounter = Create(monitor, network, logger, helperInstalled: false);
        await mounter.StartAsync(CancellationToken.None);

        // The server config enables the share after start-up (here with iSCSI, so nothing is actually mapped).
        var changed = new AgentSettings();
        changed.Storage.GamesShare.Enabled = true;
        changed.Storage.GamesShare.Iscsi = LegacyIscsi;
        current = changed;
        listener.Should().NotBeNull();
        listener!(changed, null);

        SpinWait.SpinUntil(() => logger.Entries.Any(e => e.Level == LogLevel.Error), TimeSpan.FromSeconds(10)).Should().BeTrue("the loop reacts to OnChange");
        await mounter.StopAsync(CancellationToken.None);
        mounter.IsMounted.Should().BeFalse();
    }

    [Fact]
    public async Task ServerConfig_IsCachedOnDisk_SoItAppliesFromTheNextBoot()
    {
        var dir = Directory.CreateTempSubdirectory("clubshell-cfg-");
        try
        {
            var options = new SettingsLoaderOptions
            {
                ConfigPath = Path.Combine(dir.FullName, "agent.json"),
                DefaultsPath = Path.Combine(dir.FullName, "missing.default.json"),
                CreateConfigIfMissing = false,
                Watch = false,
                EnvironmentOverride = new Dictionary<string, string?>(),
            };
            await File.WriteAllTextAsync(options.ConfigPath, new JsonObject { ["paths"] = new JsonObject { ["programData"] = dir.FullName } }.ToJsonString());
            using var storage = JsonDocument.Parse("""{ "gamesShare": { "enabled": true, "uncPath": "\\\\nas02\\games-v8" } }""");
            var config = new AgentServerConfig(7, "PC-07", "vip", 7, Storage: storage.RootElement.Clone());

            using (var first = new SettingsLoader(options))
            {
                await first.LoadAsync(CancellationToken.None);
                first.Current.Storage.GamesShare.Enabled.Should().BeFalse();
                first.ApplyServerOverrides(config);
            }

            // Next boot: no server yet, the cached config already applies.
            using var second = new SettingsLoader(options);
            var settings = await second.LoadAsync(CancellationToken.None);
            settings.Storage.GamesShare.Enabled.Should().BeTrue();
            settings.Storage.GamesShare.UncPath.Should().Be(@"\\nas02\games-v8");
            second.ServerConfig!.Version.Should().Be(7);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static GamesShareMounter Create(IOptionsMonitor<AgentSettings> settings, NetworkProbe network, ILogger<GamesShareMounter> logger, bool helperInstalled) =>
        new(new NetworkShare(), network, new NullTokenProtector(), settings, SystemClock.Instance, logger, () => helperInstalled);

    private sealed class ListLogger : ILogger<GamesShareMounter>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
