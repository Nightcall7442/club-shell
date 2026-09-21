using System.ComponentModel;
using System.Diagnostics;
using ClubShell.Agent.Policy;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Tests;

/// <summary>Builds complete <see cref="PcPolicy"/> documents with one or two sections overridden.</summary>
internal static class PolicyFactory
{
    public static readonly DateTimeOffset UpdatedAt = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    public static PcPolicy Create(int version = 1, UsbPolicy? usb = null, ProcessAllowlistPolicy? allowlist = null, ExplorerPolicy? explorer = null) =>
        new(
            version,
            UpdatedAt,
            new ShellReplacementPolicy(false, @"C:\Program Files\ClubShell\Shell\clubshell-shell.exe"),
            allowlist ?? new ProcessAllowlistPolicy(AllowlistMode.Deny, []),
            usb ?? new UsbPolicy(false, true),
            new WebFilterPolicy(false, [], [], []),
            explorer ?? new ExplorerPolicy(true, true, true, true, true, true, []),
            new PowerPolicy(),
            new UpdatesPolicy(UpdateChannel.Stable, true),
            new AntiCheatPolicy([], false),
            new KioskPolicy(300, 60, true));
}

// ---------------------------------------------------------------------------------------------
// PolicyEnforcer over fake modules
// ---------------------------------------------------------------------------------------------

public sealed class PolicyEnforcerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<string> _log = new();
    private readonly IPolicyEventSink _sink = Substitute.For<IPolicyEventSink>();
    private readonly PolicyStore _store;
    private readonly string _cachePath;

    public PolicyEnforcerTests()
    {
        _cachePath = Path.Combine(_dir.Root, "policies.json");
        _store = new PolicyStore(Substitute.For<IServerClient>(), TestSupport.Monitor(new AgentSettings()), NullLogger<PolicyStore>.Instance, cachePath: _cachePath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FirstApply_AppliesEverySection_PersistsAndPublishes()
    {
        using PolicyEnforcer enforcer = Enforcer(Module("usb"), Module("webFilter"), Module("explorer"));
        PcPolicy policy = PolicyFactory.Create();

        PolicyApplyResult result = await enforcer.ApplyAsync(policy, CancellationToken.None);

        result.Applied.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.Changed.Should().Equal(PcPolicy.SectionKeys, "nothing was applied before, so every section counts as changed");
        enforcer.Current.Should().BeSameAs(policy);
        enforcer.AppliedSections.Should().Equal(PcPolicy.SectionKeys);
        _log.Should().Equal("apply:usb", "apply:webFilter", "apply:explorer");

        _store.Last.Should().BeSameAs(policy);
        File.Exists(_cachePath).Should().BeTrue();
        PcPolicy? persisted = JsonDefaults.Deserialize<PcPolicy>(await File.ReadAllBytesAsync(_cachePath));
        persisted!.Version.Should().Be(1);
        persisted.Usb.Should().Be(policy.Usb);

        await _sink.Received(1).PublishAsync(
            Arg.Is<PolicyChanged>(c => c.Version == 1 && c.Changed.Count == PcPolicy.SectionKeys.Count && ReferenceEquals(c.Policy, policy)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SecondApply_TouchesOnlyTheSectionsThatChanged()
    {
        IPolicyModule usb = Module("usb");
        IPolicyModule explorer = Module("explorer");
        using PolicyEnforcer enforcer = Enforcer(usb, explorer);
        await enforcer.ApplyAsync(PolicyFactory.Create(), CancellationToken.None);
        _log.Clear();
        _sink.ClearReceivedCalls();
        PcPolicy updated = PolicyFactory.Create(version: 2, usb: new UsbPolicy(true, true));

        PolicyApplyResult result = await enforcer.ApplyAsync(updated, CancellationToken.None);

        result.Applied.Should().BeTrue();
        result.Changed.Should().Equal("usb");
        _log.Should().Equal("apply:usb");
        enforcer.Current.Should().BeSameAs(updated);
        _store.Last.Should().BeSameAs(updated);
        await _sink.Received(1).PublishAsync(
            Arg.Is<PolicyChanged>(c => c.Version == 2 && c.Changed.Count == 1 && c.Changed[0] == "usb"),
            Arg.Any<CancellationToken>());
        await explorer.Received(1).ApplyAsync(Arg.Any<PcPolicy>(), Arg.Any<PolicyContext>(), Arg.Any<CancellationToken>());
        await usb.Received(2).ApplyAsync(Arg.Any<PcPolicy>(), Arg.Any<PolicyContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdenticalPolicy_IsANoChange_WithoutTouchingModulesOrPublishing()
    {
        using PolicyEnforcer enforcer = Enforcer(Module("usb"), Module("explorer"));
        await enforcer.ApplyAsync(PolicyFactory.Create(), CancellationToken.None);
        _log.Clear();
        _sink.ClearReceivedCalls();

        PolicyApplyResult result = await enforcer.ApplyAsync(PolicyFactory.Create(version: 3), CancellationToken.None);

        result.Should().BeSameAs(PolicyApplyResult.NoChange, "a version bump without section changes applies nothing");
        _log.Should().BeEmpty();
        await _sink.DidNotReceive().PublishAsync(Arg.Any<PolicyChanged>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThrowingModule_IsIsolated_AndRetriedOnTheNextApply()
    {
        using PolicyEnforcer enforcer = Enforcer(Module("usb"), Module("webFilter"), Module("explorer", failTimes: 1));
        PcPolicy policy = PolicyFactory.Create();

        PolicyApplyResult first = await enforcer.ApplyAsync(policy, CancellationToken.None);

        first.Applied.Should().BeFalse();
        first.Changed.Should().Equal(PcPolicy.SectionKeys);
        PolicyApplyError error = first.Errors.Should().ContainSingle().Which;
        error.Section.Should().Be("explorer");
        error.Message.Should().Be("registry locked");
        error.ExceptionType.Should().Be(nameof(InvalidOperationException));
        _log.Should().Equal("apply:usb", "apply:webFilter", "apply:explorer");
        enforcer.Current.Should().BeSameAs(policy, "the policy is current even with a failed section");
        enforcer.AppliedSections.Should().NotContain("explorer").And.Contain("usb").And.Contain("webFilter");
        _store.Last.Should().BeSameAs(policy);
        await _sink.Received(1).PublishAsync(Arg.Any<PolicyChanged>(), Arg.Any<CancellationToken>());

        _log.Clear();
        PolicyApplyResult second = await enforcer.ApplyAsync(policy, CancellationToken.None);

        second.Applied.Should().BeTrue();
        second.Changed.Should().Equal("explorer");
        _log.Should().Equal("apply:explorer");
        enforcer.AppliedSections.Should().Equal(PcPolicy.SectionKeys);
    }

    [Fact]
    public async Task ModuleReportingFailure_CountsLikeAThrowingOne()
    {
        IPolicyModule usb = Substitute.For<IPolicyModule>();
        usb.Section.Returns("usb");
        usb.ApplyAsync(Arg.Any<PcPolicy>(), Arg.Any<PolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PolicyModuleResult.Failed(new Win32Exception(5), "storage class left as is")));
        using PolicyEnforcer enforcer = Enforcer(usb);

        PolicyApplyResult result = await enforcer.ApplyAsync(PolicyFactory.Create(), CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Section.Should().Be("usb");
        result.Errors[0].ExceptionType.Should().Be(nameof(Win32Exception));
        enforcer.AppliedSections.Should().NotContain("usb");
    }

    [Fact]
    public async Task RevertAll_RunsModulesInReverseOrder_AndSurvivesAFailingOne()
    {
        IPolicyModule usb = Module("usb");
        usb.RevertAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _log.Add("revert:usb");
            return Task.FromException(new InvalidOperationException("device busy"));
        });
        using PolicyEnforcer enforcer = Enforcer(usb, Module("webFilter"), Module("explorer"));
        await enforcer.ApplyAsync(PolicyFactory.Create(), CancellationToken.None);
        _log.Clear();

        await enforcer.RevertAllAsync(CancellationToken.None);

        _log.Should().Equal("revert:explorer", "revert:webFilter", "revert:usb");
        enforcer.Current.Should().BeNull();
        enforcer.AppliedSections.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAfterRevert_StartsFromScratch()
    {
        using PolicyEnforcer enforcer = Enforcer(Module("usb"));
        PcPolicy policy = PolicyFactory.Create();
        await enforcer.ApplyAsync(policy, CancellationToken.None);
        await enforcer.RevertAllAsync(CancellationToken.None);
        _log.Clear();

        PolicyApplyResult result = await enforcer.ApplyAsync(policy, CancellationToken.None);

        result.Changed.Should().Equal(PcPolicy.SectionKeys);
        _log.Should().Equal("apply:usb");
    }

    [Fact]
    public async Task PublishFailure_DoesNotFailTheApply()
    {
        using PolicyEnforcer enforcer = new([Module("usb")], _store, new ThrowingSink(), NullLogger<PolicyEnforcer>.Instance);

        PolicyApplyResult result = await enforcer.ApplyAsync(PolicyFactory.Create(), CancellationToken.None);

        result.Applied.Should().BeTrue();
        enforcer.Current.Should().NotBeNull();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private PolicyEnforcer Enforcer(params IPolicyModule[] modules) =>
        new(modules, _store, _sink, NullLogger<PolicyEnforcer>.Instance);

    /// <summary>A module that records its calls in <see cref="_log"/>; the first <paramref name="failTimes"/> applies throw.</summary>
    private IPolicyModule Module(string section, int failTimes = 0)
    {
        int failures = failTimes;
        IPolicyModule module = Substitute.For<IPolicyModule>();
        module.Section.Returns(section);
        module.ApplyAsync(Arg.Any<PcPolicy>(), Arg.Any<PolicyContext>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _log.Add("apply:" + section);
            return failures-- > 0
                ? Task.FromException<PolicyModuleResult>(new InvalidOperationException("registry locked"))
                : Task.FromResult(PolicyModuleResult.Ok("applied"));
        });
        module.RevertAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _log.Add("revert:" + section);
            return Task.CompletedTask;
        });
        return module;
    }

    /// <summary>A sink whose publish always fails (a substitute cannot configure a <see cref="ValueTask"/> without tripping CA2012).</summary>
    private sealed class ThrowingSink : IPolicyEventSink
    {
        public ValueTask PublishAsync(PolicyChanged changed, CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("pipe down"));
    }
}

// ---------------------------------------------------------------------------------------------
// ProcessAllowlist: glob → regex and the allow/deny evaluation
// ---------------------------------------------------------------------------------------------

public sealed class ProcessAllowlistTests
{
    /// <summary>A WTS session no process runs in, so the initial sweep of <see cref="ProcessAllowlistModule.ApplyAsync"/> kills nothing.</summary>
    private static readonly PolicyContext UnusedSession = new(null, 0x7FFF0000);

    [Theory]
    [InlineData("*.exe", "game.exe", true)]
    [InlineData("*.exe", "GAME.EXE", true)]
    [InlineData("*.exe", "game.dll", false)]
    [InlineData("*.exe", "game.exe.bak", false)]
    [InlineData(@"C:\Games\**", @"C:\Games\CS2\game\bin\win64\cs2.exe", true)]
    [InlineData(@"C:\Games\**", @"c:\games\dota 2\dota2.exe", true)]
    [InlineData(@"C:\Games\**", @"D:\Games\cs2.exe", false)]
    [InlineData(@"C:\Games\**", @"C:\GamesOld\cs2.exe", false)]
    [InlineData("*cheat*", "SuperCheatEngine.exe", true)]
    [InlineData("*cheat*", "chess.exe", false)]
    [InlineData("cmd.exe", "cmd.exe", true)]
    [InlineData("cmd.exe", "Cmd.EXE", true)]
    [InlineData("cmd.exe", "cmd.exe.lnk", false)]
    [InlineData("c?d.exe", "cmd.exe", true)]
    [InlineData("c?d.exe", "cmmd.exe", false)]
    [InlineData("a+b(1).exe", "a+b(1).exe", true)]
    public void WildcardToRegex_MatchesLikeAnAnchoredCaseInsensitiveGlob(string pattern, string input, bool expected) =>
        ProcessKiller.WildcardToRegex(pattern).IsMatch(input).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WildcardToRegex_RejectsBlankPatterns(string pattern)
    {
        Action act = () => ProcessKiller.WildcardToRegex(pattern);

        act.Should().Throw<ArgumentException>();
    }

    [WindowsFact]
    public async Task AllowMode_OnlyMatchingProcessesMayRun()
    {
        using var watcher = new ProcessWatcher();
        using var module = new ProcessAllowlistModule(watcher, new ProcessKiller(), [], SystemClock.Instance, NullLogger<ProcessAllowlistModule>.Instance);
        module.IsActive.Should().BeFalse();
        module.IsAllowed(@"C:\Windows\System32\cmd.exe").Should().BeTrue("no rules are active yet");
        PcPolicy policy = PolicyFactory.Create(allowlist: new ProcessAllowlistPolicy(AllowlistMode.Allow, [@"C:\Games\**", "steam.exe", "*launcher*.exe", " "]));

        PolicyModuleResult result = await module.ApplyAsync(policy, UnusedSession, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Changed.Should().BeTrue();
        module.IsActive.Should().BeTrue();
        module.IsAllowed(@"C:\Games\CS2\game\bin\win64\cs2.exe").Should().BeTrue();
        module.IsAllowed(@"c:\games\dota 2\dota2.exe").Should().BeTrue("paths match case-insensitively");
        module.IsAllowed(@"D:\Steam\STEAM.EXE").Should().BeTrue("the image name matches");
        module.IsAllowed(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe").Should().BeTrue();
        module.IsAllowed(@"C:\Windows\System32\cmd.exe").Should().BeFalse();
        module.IsAllowed("regedit.exe").Should().BeFalse();

        await module.RevertAsync(CancellationToken.None);

        module.IsActive.Should().BeFalse();
        module.IsAllowed(@"C:\Windows\System32\cmd.exe").Should().BeTrue("revert lifts every rule");
    }

    [WindowsFact]
    public async Task DenyMode_BlocksOnlyMatchingProcesses()
    {
        using var watcher = new ProcessWatcher();
        using var module = new ProcessAllowlistModule(watcher, new ProcessKiller(), [], SystemClock.Instance, NullLogger<ProcessAllowlistModule>.Instance);
        PcPolicy policy = PolicyFactory.Create(allowlist: new ProcessAllowlistPolicy(AllowlistMode.Deny, ["*cheat*", "cmd.exe", @"C:\Windows\System32\wscript.exe"]));

        PolicyModuleResult result = await module.ApplyAsync(policy, UnusedSession, CancellationToken.None);

        result.Error.Should().BeNull();
        module.IsAllowed("CheatEngine.exe").Should().BeFalse();
        module.IsAllowed(@"C:\Tools\cheat-o-matic\loader.exe").Should().BeFalse("the full path matches *cheat*");
        module.IsAllowed("CMD.EXE").Should().BeFalse();
        module.IsAllowed(@"C:\Windows\System32\wscript.exe").Should().BeFalse();
        module.IsAllowed(@"C:\Windows\SysWOW64\wscript.exe").Should().BeTrue("a full-path pattern only matches that path");
        module.IsAllowed(@"C:\Games\CS2\cs2.exe").Should().BeTrue();
        module.IsAllowed("chess.exe").Should().BeTrue();
    }

    [WindowsFact]
    public async Task EmptyPatternList_AllowsEverything()
    {
        using var watcher = new ProcessWatcher();
        using var module = new ProcessAllowlistModule(watcher, new ProcessKiller(), [], SystemClock.Instance, NullLogger<ProcessAllowlistModule>.Instance);

        PolicyModuleResult result = await module.ApplyAsync(PolicyFactory.Create(allowlist: new ProcessAllowlistPolicy(AllowlistMode.Allow, [])), UnusedSession, CancellationToken.None);

        result.Error.Should().BeNull();
        module.IsActive.Should().BeFalse();
        module.IsAllowed("anything.exe").Should().BeTrue();
    }

    [WindowsFact]
    public async Task DenyMode_KillsMatchingProcesses_ButNeverProtectedOnes()
    {
        int session;
        using (Process current = Process.GetCurrentProcess())
        {
            session = current.SessionId;
        }

        if (session <= 0)
        {
            return; // Session 0 (service host): the sweep is a no-op by design, nothing to observe.
        }

        using var dir = new TempDir();
        // A private copy of cmd.exe with a unique name: the deny pattern matches only our two children.
        string exe = Path.Combine(dir.Root, "clubshell-allowlist-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        using Process guarded = Spawn(exe);
        using Process victim = Spawn(exe);
        try
        {
            IProtectedProcesses guard = Substitute.For<IProtectedProcesses>();
            guard.IsProtected(guarded.Id).Returns(true);
            using var watcher = new ProcessWatcher();
            using var module = new ProcessAllowlistModule(watcher, new ProcessKiller(), [guard], SystemClock.Instance, NullLogger<ProcessAllowlistModule>.Instance);
            var blocked = new List<ProcessBlockedEvent>();
            module.ProcessBlocked += (_, e) =>
            {
                lock (blocked)
                {
                    blocked.Add(e);
                }
            };

            PcPolicy policy = PolicyFactory.Create(allowlist: new ProcessAllowlistPolicy(AllowlistMode.Deny, [Path.GetFileName(exe)]));
            PolicyModuleResult result = await module.ApplyAsync(policy, new PolicyContext(null, session), CancellationToken.None);

            result.Error.Should().BeNull();
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await victim.WaitForExitAsync(exit.Token);
            guarded.HasExited.Should().BeFalse("protected pids are never killed");
            lock (blocked)
            {
                blocked.Should().ContainSingle().Which.Pid.Should().Be(victim.Id);
                blocked[0].Rule.Should().Be(Path.GetFileName(exe));
            }

            module.IsAllowed(exe).Should().BeFalse();
        }
        finally
        {
            KillQuietly(guarded);
            KillQuietly(victim);
        }
    }

    private static Process Spawn(string exe)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        return Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            // Already gone or access denied; nothing else to do in a test.
        }
    }
}
