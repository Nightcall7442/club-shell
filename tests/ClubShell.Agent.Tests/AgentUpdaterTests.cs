using ClubShell.Contracts.Ipc;
using ClubShell.Agent.Updates;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Updates;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Tests;

/// <summary>
/// The server's <c>update</c> command from the admin console («Обновить ClubShell»): applied at once on a free PC, never
/// under a player — then remembered (a mark in the cache) and applied when the session ends.
/// </summary>
public sealed class AgentUpdaterTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 14, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("clubshell-upd-").FullName;
    private readonly IUpdateApplier _applier = Substitute.For<IUpdateApplier>();
    private readonly ISessionService _sessions = Substitute.For<ISessionService>();
    private readonly AgentUpdater _updater;

    public AgentUpdaterTests()
    {
        var settings = new AgentSettings();
        settings.Paths.ProgramData = _root;
        IOptionsMonitor<AgentSettings> monitor = TestSupport.Monitor(settings);
        var clock = new SystemClock(new FakeTimeProvider(T0));
        var checker = new UpdateChecker(Substitute.For<IServerClient>(), monitor, clock, NullLogger<UpdateChecker>.Instance);
        var downloader = new UpdateDownloader(Substitute.For<IHttpClientFactory>(), Substitute.For<ITokenStore>(), monitor, clock, NullLogger<UpdateDownloader>.Instance);
        var events = Substitute.For<IUpdateEventSink>();
        var shell = new ShellUpdater(_applier, Substitute.For<Users.IShellRelauncher>(), events, monitor, clock, NullLogger<ShellUpdater>.Instance);
        _updater = new AgentUpdater(checker, downloader, _applier, shell, _sessions, events, monitor, clock, NullLogger<AgentUpdater>.Instance);
        _applier.GetPendingAsync(UpdateComponent.Agent, Arg.Any<CancellationToken>())
            .Returns(new PendingUpdate(UpdateComponent.Agent, "1.0.22", Path.Combine(_root, "a.msi"), "00", false, T0, "1.0.21", 0));
        _applier.ApplyAsync(UpdateComponent.Agent, Arg.Any<CancellationToken>()).Returns(new UpdateApplyResponse(true, T0));
    }

    public void Dispose()
    {
        _updater.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Under_a_player_the_update_waits_for_the_end_of_the_session_and_is_remembered()
    {
        _sessions.State.Returns(SessionState.Active);

        var answer = await _updater.HandleCommandAsync(new UpdateCommand(UpdateComponent.Agent, ApplyNow: true), CancellationToken.None);

        answer.Scheduled.Should().BeTrue();
        await _applier.DidNotReceive().ApplyAsync(Arg.Any<UpdateComponent>(), Arg.Any<CancellationToken>());
        _updater.IsRequested(UpdateComponent.Agent).Should().BeTrue("the request survives the Agent's own restart");
        File.Exists(Path.Combine(_root, "cache", "update-requested-agent")).Should().BeTrue();
    }

    [Fact]
    public async Task On_a_free_pc_the_update_is_applied_at_once()
    {
        _sessions.State.Returns(SessionState.Idle);

        var answer = await _updater.HandleCommandAsync(new UpdateCommand(UpdateComponent.Agent, ApplyNow: true), CancellationToken.None);

        answer.Scheduled.Should().BeTrue();
        await _applier.Received(1).ApplyAsync(UpdateComponent.Agent, Arg.Any<CancellationToken>());
        _updater.IsRequested(UpdateComponent.Agent).Should().BeFalse();
    }

    [Fact]
    public async Task A_mandatory_package_waits_for_the_end_of_the_session_too()
    {
        // D-99: applying it restarts the Agent (or the Shell) under the player's game.
        _applier.GetPendingAsync(UpdateComponent.Agent, Arg.Any<CancellationToken>())
            .Returns(new PendingUpdate(UpdateComponent.Agent, "1.0.22", Path.Combine(_root, "a.msi"), "00", true, T0, "1.0.21", 0));
        _sessions.State.Returns(SessionState.Active);

        var answer = await _updater.HandleCommandAsync(new UpdateCommand(UpdateComponent.Agent, ApplyNow: true), CancellationToken.None);
        var apply = await _updater.ApplyAsync(UpdateComponent.Agent, CancellationToken.None);

        answer.Scheduled.Should().BeTrue();
        apply.Scheduled.Should().BeTrue();
        await _applier.DidNotReceive().ApplyAsync(Arg.Any<UpdateComponent>(), Arg.Any<CancellationToken>());
        AgentUpdater.MayApply(mandatory: true, sessionOpen: true, requested: true, inApplyWindow: true).Should().BeFalse();
        AgentUpdater.MayApply(mandatory: true, sessionOpen: false, requested: false, inApplyWindow: false).Should().BeTrue();
        AgentUpdater.MayApply(mandatory: false, sessionOpen: false, requested: false, inApplyWindow: false).Should().BeFalse();
    }
}
