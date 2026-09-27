using ClubShell.Agent.Storage;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using ClubShell.Windows.Hardware;
using ClubShell.Windows.Network;
using ClubShell.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace ClubShell.Agent.Tests;

/// <summary>Who owns the games library volume: the Agent's <c>storage.gamesShare</c> or ClubDisklessHelper.</summary>
public sealed class GamesShareMounterTests
{
    [Fact]
    public async Task InstalledDisklessHelper_OwnsTheLibrary_EvenWhenGamesShareIsConfigured()
    {
        var settings = new AgentSettings();
        settings.Storage.GamesShare.Enabled = true;
        settings.Storage.GamesShare.Iscsi = new IscsiSettings { Portal = "10.0.0.5:3260", TargetIqn = "iqn.2026-09.uz.club:games-7", ReadOnly = true };
        var logger = new ListLogger();
        using var network = new NetworkProbe();
        using var mounter = Create(settings, network, logger, helperInstalled: true);

        await mounter.StartAsync(CancellationToken.None);
        await mounter.StopAsync(CancellationToken.None);

        mounter.OwnedByDisklessHelper.Should().BeTrue();
        mounter.IsMounted.Should().BeFalse();
        // The stand-down warning is the only thing that happened: no mount loop, no logout on stop.
        logger.Entries.Should().ContainSingle()
            .Which.Should().Match<(LogLevel Level, string Message)>(e => e.Level == LogLevel.Warning && e.Message.Contains(GamesShareMounter.DisklessHelperServiceName));
    }

    [Fact]
    public async Task WithoutTheHelper_TheAgentKeepsItsOwnGamesShareSetting()
    {
        var settings = new AgentSettings();
        var logger = new ListLogger();
        using var network = new NetworkProbe();
        using var mounter = Create(settings, network, logger, helperInstalled: false);

        await mounter.StartAsync(CancellationToken.None);
        await mounter.StopAsync(CancellationToken.None);

        mounter.OwnedByDisklessHelper.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Message.Contains("disabled"));
    }

    private static GamesShareMounter Create(AgentSettings settings, NetworkProbe network, ILogger<GamesShareMounter> logger, bool helperInstalled) =>
        new(new NetworkShare(), new IscsiInitiator(new WmiQueries()), network, new NullTokenProtector(), TestSupport.Monitor(settings), SystemClock.Instance, logger, () => helperInstalled);

    private sealed class ListLogger : ILogger<GamesShareMounter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
