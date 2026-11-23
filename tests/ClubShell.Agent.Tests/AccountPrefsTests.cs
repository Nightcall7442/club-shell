using System.Net;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Ipc.Handlers;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Users;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace ClubShell.Agent.Tests;

/// <summary>
/// The member's Shell preferences and favourites on their account (D-86), through the real <c>settings.*</c> handlers and
/// <see cref="ShellSettingsStore"/> with a substituted server: applied at sign-in, saved on change, the PC's own back at
/// sign-out, nothing for a guest, and a PC offline at sign-in never overwriting the account with its half.
/// </summary>
public sealed class AccountPrefsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly IServerClient _server = Substitute.For<IServerClient>();
    private readonly IIpcEventPublisher _publisher = Substitute.For<IIpcEventPublisher>();
    private readonly IPolicyEnforcer _enforcer = Substitute.For<IPolicyEnforcer>();
    private readonly ShellUserContext _users = new();
    private readonly ShellSettingsStore _settings;
    private readonly AccountPrefsSync _sync;
    private readonly MessageDispatcher _dispatcher;
    private readonly User _member;
    private readonly User _guest;

    public AccountPrefsTests()
    {
        var settings = new AgentSettings { PcName = "PC-07", Zone = "vip" };
        settings.Paths.ProgramData = _dir.Root;
        Directory.CreateDirectory(settings.ThemesDir);
        File.WriteAllText(Path.Combine(settings.ThemesDir, "neon.json"), "{}");
        File.WriteAllText(Path.Combine(settings.ThemesDir, "club.json"), "{}");
        var monitor = TestSupport.Monitor(settings);
        _settings = new ShellSettingsStore(monitor, NullLogger<ShellSettingsStore>.Instance);
        // The club's own choice for this PC.
        _settings.Apply(new SettingsSetRequest(Locale.Ru, "club", 60, false, ShowMetricsOverlay: false, UiSounds: true));
        _sync = new AccountPrefsSync(_users, _settings, _server, _publisher, _enforcer, NullLogger<AccountPrefsSync>.Instance);
        var store = new PolicyStore(_server, monitor, NullLogger<PolicyStore>.Instance, cachePath: Path.Combine(_dir.Root, "policy.json"));
        var handlers = new PolicyHandlers(store, _enforcer, _settings, NullLogger<PolicyHandlers>.Instance);
        _dispatcher = new MessageDispatcher([handlers], new SystemClock(TimeProvider.System), NullLogger<MessageDispatcher>.Instance);
        _member = new User(Guid.NewGuid(), "an", "Anna", null, UserRole.Member, Money.Uzs(50_000), 0, 0, T0.AddDays(-10), T0, Locale.Ru, []);
        _guest = _member with { Id = Guid.NewGuid(), Username = "guest-12", DisplayName = "Guest 12", Role = UserRole.Guest };
        _server.SaveUserPrefsAsync(Arg.Any<Guid>(), Arg.Any<UserPrefs>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<UserPrefs>());
    }

    public void Dispose()
    {
        _sync.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task A_members_saved_prefs_apply_at_sign_in_and_the_shell_is_told()
    {
        _server.GetUserPrefsAsync(_member.Id, Arg.Any<CancellationToken>())
            .Returns(new UserPrefs("neon", Locale.Uz, 25, true, false, true, ["game:1", "app:discord"]));

        await SignInAsync(_member);

        ShellSettings settings = await GetAsync();
        settings.Should().BeEquivalentTo(new { Theme = "neon", Locale = Locale.Uz, Volume = 25, Muted = true, UiSounds = false, ShowMetricsOverlay = true, AccountPrefs = true });
        settings.Favorites.Should().Equal("game:1", "app:discord");
        await _publisher.Received().PublishAsync(
            IpcMessages.Events.SettingsChanged, Arg.Is<ShellSettings>(s => s.Theme == "neon" && s.Locale == Locale.Uz), Arg.Any<CancellationToken>());
        // Nothing was changed by the player: nothing to write back.
        await _server.DidNotReceive().SaveUserPrefsAsync(Arg.Any<Guid>(), Arg.Any<UserPrefs>(), Arg.Any<CancellationToken>());
        _settings.GetDevice().Theme.Should().Be("club", "the PC's shell.json keeps the club's choice");
    }

    [Fact]
    public async Task A_change_is_saved_to_the_account_not_the_pc_and_sign_out_brings_the_pcs_back()
    {
        _server.GetUserPrefsAsync(_member.Id, Arg.Any<CancellationToken>()).Returns(new UserPrefs(Favorites: ["game:1"]));
        await SignInAsync(_member);

        (await SetAsync(new SettingsSetRequest(Theme: "neon", Volume: 30, Favorites: ["game:1", "game:2"]))).IsError.Should().BeFalse();
        _settings.SetVolume(35, muted: true); // the volume slider (sys.setVolume)
        await _sync.WhenIdleAsync();

        await _server.Received().SaveUserPrefsAsync(
            _member.Id, Arg.Is<UserPrefs>(p => p.Theme == "neon" && p.Volume == 35 && p.Muted == true && p.Favorites!.SequenceEqual(new[] { "game:1", "game:2" })), Arg.Any<CancellationToken>());
        _settings.GetDevice().Should().BeEquivalentTo(new { Theme = "club", Volume = 60, Muted = false });
        File.ReadAllText(_settings.FilePath).Should().NotContain("neon");

        _publisher.ClearReceivedCalls();
        _users.Clear();
        await _sync.WhenIdleAsync();

        ShellSettings after = await GetAsync();
        after.Should().BeEquivalentTo(new { Theme = "club", Volume = 60, Muted = false, AccountPrefs = false });
        after.Favorites.Should().BeEmpty();
        await _publisher.Received(1).PublishAsync(
            IpcMessages.Events.SettingsChanged, Arg.Is<ShellSettings>(s => s.Theme == "club" && !s.AccountPrefs), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_guest_has_no_account_prefs_nothing_is_saved_and_favourites_are_refused()
    {
        await SignInAsync(_guest);

        (await GetAsync()).AccountPrefs.Should().BeFalse();
        IpcEnvelope favourites = await SetAsync(new SettingsSetRequest(Favorites: ["game:1"]));
        favourites.Error!.Code.Should().Be(ErrorCode.Forbidden);
        // A guest's theme stays the PC's business, as before accounts kept any.
        (await SetAsync(new SettingsSetRequest(Theme: "neon"))).IsError.Should().BeFalse();
        await _sync.WhenIdleAsync();

        _settings.GetDevice().Theme.Should().Be("neon");
        await _server.DidNotReceive().GetUserPrefsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _server.DidNotReceive().SaveUserPrefsAsync(Arg.Any<Guid>(), Arg.Any<UserPrefs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Favourites_with_nobody_signed_in_are_refused_and_bad_ones_rejected()
    {
        (await SetAsync(new SettingsSetRequest(Favorites: ["game:1"]))).Error!.Code.Should().Be(ErrorCode.Forbidden);

        _server.GetUserPrefsAsync(_member.Id, Arg.Any<CancellationToken>()).Returns(new UserPrefs());
        await SignInAsync(_member);
        IpcEnvelope duplicate = await SetAsync(new SettingsSetRequest(Favorites: ["game:1", "game:1"]));
        duplicate.Error!.Code.Should().Be(ErrorCode.Validation);
        IpcEnvelope tooMany = await SetAsync(new SettingsSetRequest(Favorites: Enumerable.Range(0, 61).Select(i => $"game:{i}").ToArray()));
        tooMany.Error!.Code.Should().Be(ErrorCode.Validation);
    }

    [Fact]
    public async Task Offline_at_sign_in_the_pcs_settings_apply_and_the_first_change_reads_the_account_before_saving()
    {
        _server.GetUserPrefsAsync(_member.Id, Arg.Any<CancellationToken>()).ThrowsAsync(
            new ServerApiException(ErrorCode.ServerUnavailable, HttpStatusCode.ServiceUnavailable, null, null, "connection refused"));
        await SignInAsync(_member);
        (await GetAsync()).Should().BeEquivalentTo(new { Theme = "club", AccountPrefs = true });

        // The server is back; the player picks a theme: their saved favourites are kept, the new theme wins.
        _server.GetUserPrefsAsync(_member.Id, Arg.Any<CancellationToken>()).Returns(new UserPrefs("club", Locale.En, Favorites: ["app:discord"]));
        (await SetAsync(new SettingsSetRequest(Theme: "neon"))).IsError.Should().BeFalse();
        await _sync.WhenIdleAsync();

        await _server.Received(1).SaveUserPrefsAsync(
            _member.Id, Arg.Is<UserPrefs>(p => p.Theme == "neon" && p.Locale == Locale.En && p.Favorites!.SequenceEqual(new[] { "app:discord" })), Arg.Any<CancellationToken>());
        (await GetAsync()).Should().BeEquivalentTo(new { Theme = "neon", Locale = Locale.En });
    }

    [Fact]
    public async Task A_theme_this_pc_does_not_have_keeps_the_pcs_own()
    {
        _server.GetUserPrefsAsync(_member.Id, Arg.Any<CancellationToken>()).Returns(new UserPrefs("aurora", Volume: 40));

        await SignInAsync(_member);

        (await GetAsync()).Should().BeEquivalentTo(new { Theme = "club", Volume = 40 });
    }

    private async Task SignInAsync(User user)
    {
        _users.Set(user, T0.AddHours(8), ConnectivityState.Online);
        await _sync.WhenIdleAsync();
    }

    private async Task<ShellSettings> GetAsync() =>
        (await _dispatcher.DispatchAsync(Context(), IpcEnvelope.Request(IpcMessages.Settings.Get), CancellationToken.None)).PayloadAs<ShellSettings>()!;

    private Task<IpcEnvelope> SetAsync(SettingsSetRequest request) =>
        _dispatcher.DispatchAsync(Context(), IpcEnvelope.Request(IpcMessages.Settings.Set, request), CancellationToken.None);

    private IpcContext Context() =>
        new(Guid.NewGuid(), _users.User is null ? IpcAuthLevel.Hello : IpcAuthLevel.User, _users.User, null, 1, 1, static (_, _, _) => { });
}
