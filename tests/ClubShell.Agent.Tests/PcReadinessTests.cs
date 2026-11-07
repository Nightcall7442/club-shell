using System.Collections.Concurrent;
using System.Text;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Readiness;
using ClubShell.Agent.Saves;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

/// <summary>
/// The PC lets a player in only once the games disks are there (SHELL_CHANGES 86): by the diskless helper's word when its
/// <c>ping</c> reports them, else by the library roots, within <c>shell.readiness.timeoutSec</c>.
/// </summary>
public sealed class PcReadinessTests
{
    [Fact]
    public async Task Without_the_helper_the_pc_is_ready_at_once_after_a_rescan()
    {
        var harness = new Harness(helperInstalled: false);

        PcReadiness ready = await harness.RunUntilAsync(PcReadinessState.Ready);

        ready.Disks.Should().BeEmpty();
        harness.Library.Rescans.Should().Be(1);
        harness.Channel.Pings.Should().Be(0);
    }

    [Fact]
    public async Task The_helper_reporting_its_disks_decides_and_the_pc_waits_for_it()
    {
        var harness = new Harness();
        harness.Channel.Script(
            Ping(false, ("games", "G", "attaching"), ("solo", "H", "attaching")),
            Ping(false, ("games", "G", "mounted"), ("solo", "H", "attaching")),
            Ping(true, ("games", "G", "mounted"), ("solo", "H", "mounted")));

        PcReadiness ready = await harness.RunUntilAsync(PcReadinessState.Ready);

        ready.Disks.Should().HaveCount(2).And.OnlyContain(d => d.State == "mounted");
        harness.States.Should().StartWith(PcReadinessState.Preparing).And.EndWith(PcReadinessState.Ready);
        harness.Library.Rescans.Should().Be(1);
    }

    [Fact]
    public async Task No_disk_at_all_is_not_ready_until_one_comes()
    {
        var harness = new Harness();
        harness.Channel.Script(
            Ping(true, ("games", "G", "noServer")),
            Ping(true, ("games", "G", "noServer")),
            Ping(true, ("games", "G", "mounted")));

        await harness.RunUntilAsync(PcReadinessState.Ready);

        harness.States.Should().Contain(PcReadinessState.NotReady);
        harness.States.Last().Should().Be(PcReadinessState.Ready);
    }

    [Fact]
    public async Task An_older_helper_is_judged_by_the_library_roots()
    {
        var harness = new Harness(roots: [@"G:\Steam", @"H:\"]);
        harness.Channel.Script(new HelperResult(HelperResultKind.Ok, Version: "1.8.1"));
        harness.Existing.Add(@"G:\Steam");

        await Task.Delay(300);
        harness.Saves.Current.State.Should().Be(PcReadinessState.Preparing, "H: is not there yet and the wait is not over");
        harness.Existing.Add(@"H:\");

        PcReadiness ready = await harness.RunUntilAsync(PcReadinessState.Ready);
        ready.Disks.Select(d => d.Letter).Should().Equal("G", "H");
    }

    [Fact]
    public async Task An_agent_restarted_long_after_boot_does_not_wait_for_a_missing_root()
    {
        var harness = new Harness(roots: [@"G:\Steam", @"H:\"], uptime: TimeSpan.FromHours(5));
        harness.Channel.Script(new HelperResult(HelperResultKind.Ok, Version: "1.8.1"));
        harness.Existing.Add(@"G:\Steam");

        await harness.RunUntilAsync(PcReadinessState.Ready);
    }

    [Fact]
    public async Task A_missing_channel_falls_back_to_the_roots()
    {
        var harness = new Harness(roots: [@"G:\Steam"]);
        harness.Channel.Script(new HelperResult(HelperResultKind.Unreachable));
        harness.Existing.Add(@"G:\Steam");

        await harness.RunUntilAsync(PcReadinessState.Ready);
    }

    [Fact]
    public async Task A_changed_game_list_is_announced_to_the_shell()
    {
        var harness = new Harness(helperInstalled: false);
        await harness.RunUntilAsync(PcReadinessState.Ready);

        harness.Library.RaiseChanged("v42");

        for (int i = 0; i < 100 && !harness.Events.Names.Contains(IpcMessages.Events.GamesChanged); i++)
        {
            await Task.Delay(10);
        }

        harness.Events.Names.Should().Contain(IpcMessages.Events.GamesChanged);
        harness.Events.Names.Should().Contain(IpcMessages.Events.SysReadinessChanged);
    }

    private static HelperResult Ping(bool ready, params (string Library, string Letter, string State)[] disks)
    {
        var json = new StringBuilder("{\"ok\":true,\"version\":\"1.9.0\",\"ready\":").Append(ready ? "true" : "false").Append(",\"disks\":[");
        json.AppendJoin(',', disks.Select(d => $"{{\"library\":\"{d.Library}\",\"letter\":\"{d.Letter}\",\"state\":\"{d.State}\"}}"));
        return HelperProtocol.ParseReply(json.Append("]}").ToString());
    }

    private sealed class Harness
    {
        public Harness(bool helperInstalled = true, string[]? roots = null, TimeSpan? uptime = null)
        {
            var settings = new AgentSettings();
            settings.Shell.Readiness.TimeoutSec = 1;
            settings.Games.LibraryRoots = roots ?? [@"G:\Games"];
            Saves = new PcReadinessService(
                Channel,
                Library,
                TestSupport.Monitor(settings),
                new SystemClock(),
                NullLogger<PcReadinessService>.Instance,
                Events,
                () => helperInstalled,
                path => Existing.Contains(path),
                () => uptime ?? TimeSpan.Zero)
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
                NoChannelAfter = TimeSpan.FromMilliseconds(60),
            };
            Saves.Changed += (_, r) => States.Enqueue(r.State);
        }

        public FakeChannel Channel { get; } = new();

        public FakeLibrary Library { get; } = new();

        public RecordingEvents Events { get; } = new();

        public ConcurrentBag<string> Existing { get; } = [];

        public ConcurrentQueue<PcReadinessState> States { get; } = new();

        public PcReadinessService Saves { get; }

        private bool _started;

        public async Task<PcReadiness> RunUntilAsync(PcReadinessState state)
        {
            if (!_started)
            {
                _started = true;
                await Saves.StartAsync(CancellationToken.None);
            }

            for (int i = 0; i < 300 && Saves.Current.State != state; i++)
            {
                await Task.Delay(10);
            }

            await Saves.StopAsync(CancellationToken.None);
            Saves.Current.State.Should().Be(state);
            return Saves.Current;
        }
    }

    private sealed class FakeChannel : IDisklessHelperChannel
    {
        private readonly ConcurrentQueue<HelperResult> _script = new();
        private HelperResult _last = new(HelperResultKind.Unreachable);

        public int Pings { get; private set; }

        public void Script(params HelperResult[] results)
        {
            foreach (HelperResult r in results)
            {
                _script.Enqueue(r);
            }
        }

        public Task<HelperResult> SendAsync(byte[] request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Pings++;
            if (_script.TryDequeue(out HelperResult? next))
            {
                _last = next;
            }

            return Task.FromResult(_last);
        }
    }

    private sealed class FakeLibrary : IReadinessLibrary
    {
        public bool Loaded => true;

        public string CatalogVersion => "1";

        public int Rescans { get; private set; }

        public event EventHandler<string>? Changed;

        public Task RescanAsync(CancellationToken cancellationToken)
        {
            Rescans++;
            return Task.CompletedTask;
        }

        public void RaiseChanged(string version) => Changed?.Invoke(this, version);
    }

    private sealed class RecordingEvents : IIpcEventPublisher
    {
        public ConcurrentBag<string> Names { get; } = [];

        public ValueTask PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken cancellationToken)
        {
            Names.Add(eventName);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync(IpcEnvelope envelope, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
