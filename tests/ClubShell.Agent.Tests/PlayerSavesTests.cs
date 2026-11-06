using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Saves;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

/// <summary>
/// The diskless helper's saves channel (SHELL_CHANGES 23): one JSON line each way, the codes it answers with, and when
/// the Agent sends <c>profile.attach</c> and <c>profile.detach</c>.
/// </summary>
public sealed class PlayerSavesTests
{
    private const string KioskSid = "S-1-5-21-1000-2000-3000-1001";

    // ---- protocol -------------------------------------------------------------------------------

    [Fact]
    public void A_request_is_one_utf8_json_line_without_bom()
    {
        var player = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        byte[] line = HelperProtocol.Attach(HelperProtocol.PlayerId(player), KioskSid);

        line[^1].Should().Be((byte)'\n');
        line.Count(b => b == (byte)'\n').Should().Be(1);
        line.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        using JsonDocument doc = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
        doc.RootElement.GetProperty("op").GetString().Should().Be("profile.attach");
        doc.RootElement.GetProperty("playerId").GetString().Should().Be("0f8fad5b-d9cb-469f-a165-70867728950e", "the D form: N would be another folder on the club server");
        doc.RootElement.GetProperty("windowsUserSid").GetString().Should().Be(KioskSid);
        doc.RootElement.EnumerateObject().Should().HaveCount(3, "rememberLaunchers is gone from v1");

        Encoding.UTF8.GetString(HelperProtocol.Detach("p-1")).Should().Be("{\"op\":\"profile.detach\",\"playerId\":\"p-1\"}\n");
        Encoding.UTF8.GetString(HelperProtocol.Ping()).Should().Be("{\"op\":\"ping\"}\n");
    }

    [Theory]
    [InlineData("disabled", HelperErrorCode.Disabled)]
    [InlineData("serverUnavailable", HelperErrorCode.ServerUnavailable)]
    [InlineData("overLimit", HelperErrorCode.OverLimit)]
    [InlineData("notAttached", HelperErrorCode.NotAttached)]
    [InlineData("masterMode", HelperErrorCode.MasterMode)]
    [InlineData("profileNotFound", HelperErrorCode.ProfileNotFound)]
    [InlineData("badRequest", HelperErrorCode.BadRequest)]
    [InlineData("internal", HelperErrorCode.Internal)]
    [InlineData("somethingNew", HelperErrorCode.Internal)]
    public void Error_codes_are_read_and_an_unknown_one_counts_as_internal(string wire, HelperErrorCode expected)
    {
        HelperResult result = HelperProtocol.ParseReply($$"""{"ok":false,"error":"{{wire}}","message":"x","extra":1}""");

        result.Kind.Should().Be(HelperResultKind.Error);
        result.Code.Should().Be(expected);
    }

    [Fact]
    public void Answers_are_parsed_as_json_with_escapes_and_unknown_fields()
    {
        HelperResult ok = HelperProtocol.ParseReply("{\"ok\":true,\"version\":\"1.7.0\",\"later\":{\"a\":1}}");
        ok.IsOk.Should().BeTrue();
        ok.Version.Should().Be("1.7.0");

        HelperResult russian = HelperProtocol.ParseReply("{\"ok\":false,\"error\":\"internal\",\"message\":\"\\u0424\\u0430\\u0439\\u043b \\u0437\\u0430\\u043d\\u044f\\u0442\"}");
        russian.Message.Should().Be("Файл занят");

        HelperProtocol.ParseReply("not json").Kind.Should().Be(HelperResultKind.Failed);
        HelperProtocol.ParseReply("{\"error\":\"internal\"}").Kind.Should().Be(HelperResultKind.Failed);
    }

    [Fact]
    public async Task An_exchange_writes_the_request_and_reads_one_answer_line()
    {
        var stream = new ScriptedStream("{\"ok\":true}\r\n");

        HelperResult result = await HelperProtocol.ExchangeAsync(stream, HelperProtocol.Ping(), TimeSpan.FromSeconds(5), CancellationToken.None);

        result.IsOk.Should().BeTrue();
        Encoding.UTF8.GetString(stream.Written.ToArray()).Should().Be("{\"op\":\"ping\"}\n");
    }

    [Fact]
    public async Task An_exchange_without_an_answer_times_out_and_a_closed_channel_fails()
    {
        HelperResult silent = await HelperProtocol.ExchangeAsync(new ScriptedStream(null), HelperProtocol.Ping(), TimeSpan.FromMilliseconds(100), CancellationToken.None);
        silent.Kind.Should().Be(HelperResultKind.Timeout);

        HelperResult closed = await HelperProtocol.ExchangeAsync(new ScriptedStream("{\"ok\":tr"), HelperProtocol.Ping(), TimeSpan.FromSeconds(5), CancellationToken.None);
        closed.Kind.Should().Be(HelperResultKind.Failed, "the stream ended before the newline");
    }

    // ---- attach ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_member_signing_in_gets_an_attach_with_the_kiosk_sid_and_the_shell_hears_loading_then_ready()
    {
        var harness = new Harness();
        User player = Member();

        harness.Saves.SignedIn(player);
        await harness.Saves.WaitForAttachAsync(CancellationToken.None);

        harness.Channel.Ops.Should().Equal("profile.attach");
        harness.Channel.Requests[0].GetProperty("playerId").GetString().Should().Be(player.Id.ToString("D"));
        harness.Channel.Requests[0].GetProperty("windowsUserSid").GetString().Should().Be(KioskSid);
        harness.Saves.State.Should().Be(PlayerSavesState.Ready);
        harness.Saves.Attached.Should().BeTrue();
        (await harness.Events.WaitForAsync(PlayerSavesState.Ready)).Should().Equal(PlayerSavesState.Loading, PlayerSavesState.Ready);
    }

    [Fact]
    public async Task Signing_in_again_as_the_same_player_does_not_attach_twice()
    {
        var harness = new Harness();
        User player = Member();

        harness.Saves.SignedIn(player);
        await harness.Saves.WaitForAttachAsync(CancellationToken.None);
        harness.Saves.SignedIn(player);
        await harness.Saves.WaitForAttachAsync(CancellationToken.None);

        harness.Channel.Ops.Should().Equal("profile.attach");
    }

    [Fact]
    public async Task A_guest_gets_neither_attach_nor_detach()
    {
        var harness = new Harness();

        harness.Saves.SignedIn(Member(UserRole.Guest));
        harness.Saves.SignedIn(Member(), guest: true);
        await harness.Saves.WaitForAttachAsync(CancellationToken.None);
        harness.Saves.BeginRelease(SessionEndReason.User, closeGames: true);
        (await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().BeTrue();

        harness.Channel.Ops.Should().BeEmpty();
        harness.Saves.State.Should().Be(PlayerSavesState.None);
    }

    [Theory]
    [InlineData("off", true)]
    [InlineData("auto", false)]
    public async Task Saves_stay_off_when_configured_so_or_without_the_helper(string mode, bool helperInstalled)
    {
        var harness = new Harness(mode: mode, helperInstalled: helperInstalled);

        harness.Saves.SignedIn(Member());
        await harness.Saves.WaitForAttachAsync(CancellationToken.None);

        harness.Channel.Ops.Should().BeEmpty();
        harness.Saves.State.Should().Be(PlayerSavesState.None);
    }

    [Theory]
    [InlineData(HelperResultKind.Error)]
    [InlineData(HelperResultKind.Unreachable)]
    [InlineData(HelperResultKind.Timeout)]
    [InlineData(HelperResultKind.Refused)]
    public async Task A_failed_attach_lets_the_player_play_without_saves_and_is_still_detached(HelperResultKind failure)
    {
        var harness = new Harness();
        harness.Channel.Answer("profile.attach", new HelperResult(failure, failure == HelperResultKind.Error ? HelperErrorCode.ServerUnavailable : HelperErrorCode.None));

        harness.Saves.SignedIn(Member());
        await harness.Saves.WaitForAttachAsync(CancellationToken.None);
        harness.Saves.State.Should().Be(PlayerSavesState.Unavailable);

        harness.Saves.BeginRelease(SessionEndReason.TimeUp, closeGames: false);
        (await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeTrue();

        harness.Channel.Ops.Should().Equal("profile.attach", "profile.detach");
    }

    // ---- detach ---------------------------------------------------------------------------------

    [Fact]
    public async Task Detach_is_retried_while_the_helper_may_still_succeed()
    {
        var harness = new Harness();
        harness.Channel.Answer("profile.detach", Error(HelperErrorCode.ServerUnavailable), Error(HelperErrorCode.Disabled), new HelperResult(HelperResultKind.Unreachable), new HelperResult(HelperResultKind.Ok));
        await AttachAsync(harness);

        harness.Saves.BeginRelease(SessionEndReason.User, closeGames: false);
        (await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeTrue();

        harness.Channel.Ops.Should().Equal("profile.attach", "profile.detach", "profile.detach", "profile.detach", "profile.detach");
        harness.Saves.Attached.Should().BeFalse();
        harness.Saves.State.Should().Be(PlayerSavesState.None);
    }

    [Theory]
    [InlineData(HelperErrorCode.NotAttached)]
    [InlineData(HelperErrorCode.MasterMode)]
    [InlineData(HelperErrorCode.BadRequest)]
    [InlineData(HelperErrorCode.ProfileNotFound)]
    public async Task Detach_is_not_repeated_when_nothing_could_be_sent(HelperErrorCode code)
    {
        var harness = new Harness();
        harness.Channel.Answer("profile.detach", Error(code));
        await AttachAsync(harness);

        harness.Saves.BeginRelease(SessionEndReason.User, closeGames: false);
        await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        harness.Channel.Ops.Should().Equal("profile.attach", "profile.detach");
    }

    [Fact]
    public async Task Saves_over_the_limit_are_reported_to_the_player_and_not_retried()
    {
        var harness = new Harness();
        harness.Channel.Answer("profile.detach", Error(HelperErrorCode.OverLimit));
        await AttachAsync(harness);

        harness.Saves.BeginRelease(SessionEndReason.User, closeGames: false);
        await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        harness.Channel.Ops.Should().Equal("profile.attach", "profile.detach");
        (await harness.Events.WaitForAsync(PlayerSavesState.TooLarge)).Should().Contain(PlayerSavesState.TooLarge);
    }

    [Fact]
    public async Task An_internal_error_kills_the_games_still_holding_files_before_the_retry()
    {
        var harness = new Harness();
        harness.Channel.Answer("profile.detach", Error(HelperErrorCode.Internal), new HelperResult(HelperResultKind.Ok));
        await AttachAsync(harness);

        harness.Saves.BeginRelease(SessionEndReason.Admin, closeGames: false);
        await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        harness.Log.Should().Equal("profile.attach", "profile.detach", "kill graceful=False", "profile.detach");
    }

    [Fact]
    public async Task Signing_out_without_a_session_end_closes_the_games_gently_before_the_detach()
    {
        var harness = new Harness();
        await AttachAsync(harness);

        harness.Saves.BeginRelease(SessionEndReason.User, closeGames: true);
        await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        harness.Log.Should().Equal("profile.attach", "kill graceful=True", "profile.detach");
    }

    [Fact]
    public async Task Detach_gives_up_when_its_time_is_over()
    {
        var harness = new Harness(detachTimeoutSec: 1);
        harness.Channel.Answer("profile.detach", Enumerable.Repeat(Error(HelperErrorCode.ServerUnavailable), 1000).ToArray());
        await AttachAsync(harness);

        harness.Saves.BeginRelease(SessionEndReason.User, closeGames: false);
        (await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(10), CancellationToken.None)).Should().BeTrue();

        harness.Saves.Attached.Should().BeFalse();
        harness.Channel.Ops.Count(op => op == "profile.detach").Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task The_profile_reset_waits_for_a_detach_that_has_not_started_yet()
    {
        var harness = new Harness();
        await AttachAsync(harness);

        Task<bool> waiting = harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(100);
        waiting.IsCompleted.Should().BeFalse("the games are still closing; the detach comes after them");

        harness.Saves.BeginRelease(SessionEndReason.User, closeGames: false);
        (await waiting).Should().BeTrue();
        harness.Channel.Ops.Should().Equal("profile.attach", "profile.detach");
    }

    [Fact]
    public async Task The_profile_reset_stops_waiting_when_no_detach_ever_starts()
    {
        var harness = new Harness();
        await AttachAsync(harness);

        (await harness.Saves.WaitReleasedAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_session_ending_right_after_sign_in_detaches_only_after_the_attach_answered()
    {
        var harness = new Harness();
        harness.Channel.AttachDelay = TimeSpan.FromMilliseconds(200);

        harness.Saves.SignedIn(Member());
        harness.Saves.BeginRelease(SessionEndReason.Admin, closeGames: false);
        (await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeTrue();

        harness.Channel.Ops.Should().Equal("profile.attach", "profile.detach");
        harness.Saves.State.Should().Be(PlayerSavesState.None, "the attach answered after its release began and changes nothing");
    }

    [Fact]
    public async Task Nothing_is_detached_without_an_attach()
    {
        var harness = new Harness();

        harness.Saves.BeginRelease(SessionEndReason.User, closeGames: true);
        (await harness.Saves.WaitReleasedAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().BeTrue();

        harness.Log.Should().BeEmpty("a detach that was never attached, or after a restart, is not caught up");
    }

    [Fact]
    public async Task Another_player_signing_in_has_the_previous_saves_collected_first()
    {
        var harness = new Harness();
        User first = Member();
        User second = Member();
        harness.Saves.SignedIn(first);
        await harness.Saves.WaitForAttachAsync(CancellationToken.None);

        harness.Saves.SignedIn(second);
        await harness.Saves.WaitForAttachAsync(CancellationToken.None);

        harness.Channel.Ops.Should().Equal("profile.attach", "profile.detach", "profile.attach");
        harness.Channel.Requests[1].GetProperty("playerId").GetString().Should().Be(first.Id.ToString("D"));
        harness.Channel.Requests[2].GetProperty("playerId").GetString().Should().Be(second.Id.ToString("D"));
        harness.Saves.State.Should().Be(PlayerSavesState.Ready);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task AttachAsync(Harness harness)
    {
        harness.Saves.SignedIn(Member());
        await harness.Saves.WaitForAttachAsync(CancellationToken.None);
    }

    private static HelperResult Error(HelperErrorCode code) => new(HelperResultKind.Error, code, "test");

    private static User Member(UserRole role = UserRole.Member) =>
        new(Guid.NewGuid(), "player", "Player", null, role, Money.Zero, 0, 0, DateTimeOffset.UnixEpoch, null, Locale.Ru, []);

    private sealed class Harness
    {
        public Harness(string mode = "on", bool helperInstalled = true, int detachTimeoutSec = 120)
        {
            var settings = new AgentSettings();
            settings.Games.PlayerSaves.Mode = mode;
            settings.Games.PlayerSaves.DetachTimeoutSec = detachTimeoutSec;
            Channel = new FakeChannel(Log);
            Games = new FakeGames(Log);
            Saves = new PlayerSavesService(
                Channel,
                new FakeKiosk(),
                TestSupport.Monitor(settings),
                new SystemClock(),
                NullLogger<PlayerSavesService>.Instance,
                Events,
                Games,
                () => helperInstalled)
            {
                RetryDelay = TimeSpan.FromMilliseconds(10),
            };
        }

        /// <summary>Helper commands and game kills, in order (the fakes lock it).</summary>
        public List<string> Log { get; } = [];

        public FakeChannel Channel { get; }

        public FakeGames Games { get; }

        public RecordingEvents Events { get; } = new();

        public PlayerSavesService Saves { get; }
    }

    private sealed class FakeChannel(List<string> log) : IDisklessHelperChannel
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<HelperResult>> _answers = new();
        private readonly object _gate = new();

        public List<JsonElement> Requests { get; } = [];

        public List<string> Ops
        {
            get
            {
                lock (_gate)
                {
                    return Requests.Select(r => r.GetProperty("op").GetString()!).ToList();
                }
            }
        }

        public TimeSpan AttachDelay { get; set; }

        public void Answer(string op, params HelperResult[] results) => _answers[op] = new ConcurrentQueue<HelperResult>(results);

        public async Task<HelperResult> SendAsync(byte[] request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            JsonElement parsed = JsonDocument.Parse(request.AsMemory(0, request.Length - 1)).RootElement.Clone();
            string op = parsed.GetProperty("op").GetString()!;
            lock (_gate)
            {
                Requests.Add(parsed);
            }

            lock (log)
            {
                log.Add(op);
            }

            if (op == "profile.attach" && AttachDelay > TimeSpan.Zero)
            {
                await Task.Delay(AttachDelay, cancellationToken);
            }

            return _answers.TryGetValue(op, out var queue) && queue.TryDequeue(out var result) ? result : new HelperResult(HelperResultKind.Ok);
        }
    }

    private sealed class FakeGames(List<string> log) : IGameSessionCleanup
    {
        public Task KillAllAsync(SessionEndReason reason, bool graceful, CancellationToken cancellationToken)
        {
            lock (log)
            {
                log.Add($"kill graceful={graceful}");
            }

            return Task.CompletedTask;
        }

        public Task ReleaseLeasesAsync(SessionEndReason reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeKiosk : IKioskCredentials
    {
        public string UserName => "player";

        public string Password => string.Empty;

        public string Sid => KioskSid;
    }

    private sealed class RecordingEvents : IIpcEventPublisher
    {
        private readonly List<PlayerSavesState> _states = [];

        public ValueTask PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken cancellationToken)
        {
            if (eventName == IpcMessages.Events.SavesStatus && payload is PlayerSavesStatus status)
            {
                lock (_states)
                {
                    _states.Add(status.State);
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync(IpcEnvelope envelope, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <summary>The states sent so far, once <paramref name="state"/> is among them (events are sent off the caller's thread).</summary>
        public async Task<List<PlayerSavesState>> WaitForAsync(PlayerSavesState state)
        {
            for (int i = 0; i < 200; i++)
            {
                lock (_states)
                {
                    if (_states.Contains(state))
                    {
                        return [.. _states];
                    }
                }

                await Task.Delay(10);
            }

            lock (_states)
            {
                return [.. _states];
            }
        }
    }

    /// <summary>A channel end: records what is written, reads back a scripted answer, or nothing at all (<see langword="null"/>).</summary>
    private sealed class ScriptedStream(string? answer) : Stream
    {
        private readonly byte[]? _answer = answer is null ? null : Encoding.UTF8.GetBytes(answer);
        private int _position;

        public MemoryStream Written { get; } = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_answer is null)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            int n = Math.Min(buffer.Length, _answer!.Length - _position);
            _answer.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
