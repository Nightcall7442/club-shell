using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Realtime;
using ClubShell.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClubShell.Server.Tests;

/// <summary>
/// The real agent client against the server (DESIGN §10.b): <see cref="ServerClient"/> through the production
/// <c>ConfigureServerHttpClient</c> pipeline (Polly, <c>RequestSigningHandler</c>) and <see cref="RealtimeClient"/>.
/// With a <see cref="ServerFixture"/> the primary handler is the in-process <c>TestServer</c> handler; with a
/// <see cref="KestrelServerFixture"/> (<c>TestServer</c> is not available there) the client keeps its own socket
/// handler and talks to 127.0.0.1. Every response also passes <see cref="ContractValidatingHandler"/>. The agent has its
/// own clock, so clock skew and token expiry are driven independently of the server's <see cref="FakeClock"/>.
/// </summary>
public sealed class AgentHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _dir;

    private AgentHarness(ServiceProvider services, string dir, FakeClock clock, string hwid)
    {
        _services = services;
        _dir = dir;
        Clock = clock;
        Hwid = hwid;
    }

    /// <summary>The agent's clock; starts at the server's.</summary>
    public FakeClock Clock { get; }

    public string Hwid { get; }

    public string Mac { get; } = TestAgent.RandomMac();

    public ServerClient Client => _services.GetRequiredService<ServerClient>();

    public RealtimeClient Realtime => _services.GetRequiredService<RealtimeClient>();

    public ITokenStore Tokens => _services.GetRequiredService<ITokenStore>();

    /// <summary>The agent-side container (a second <see cref="RealtimeClient"/> of the same PC, …).</summary>
    public IServiceProvider Services => _services;

    public Guid PcId => Tokens.Agent!.PcId;

    public static async Task<AgentHarness> CreateAsync(ServerFixture server)
    {
        var kestrel = server as KestrelServerFixture;
        var root = kestrel?.BaseAddress.ToString().TrimEnd('/') ?? "http://localhost";
        var dir = Path.Combine(Path.GetTempPath(), "clubshell-agent-" + Guid.NewGuid().ToString("N"));
        var clock = new FakeClock();
        clock.Advance(server.Clock.GetUtcNow() - clock.GetUtcNow());
        var board = "board:" + Guid.NewGuid().ToString("N");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<AgentSettings>().Configure(s =>
        {
            s.Server.BaseUrl = root + "/api/v1";
            s.Server.WsUrl = root.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/agent";
            s.Server.ClubApiKey = ServerFixture.ClubKey;
        });
        var http = services.ConfigureServerHttpClient(new ConfigurationBuilder().Build());
        if (kestrel is null)
        {
            http.ConfigurePrimaryHttpMessageHandler(() => server.Server.CreateHandler());
        }

        http.AddHttpMessageHandler(() => new ContractValidatingHandler(server));
        services.AddSingleton<IClock>(new SystemClock(clock));
        services.AddSingleton<ITokenStore>(provider => new TokenStore(Path.Combine(dir, "agent.tokens"), new NullTokenProtector(), provider.GetRequiredService<IClock>()));
        services.AddSingleton(new Hwid(new FixedHardware(board), Path.Combine(dir, "hwid.fallback")));
        services.AddSingleton<ServerClient>();
        services.AddSingleton<RealtimeClient>();
        var provider = services.BuildServiceProvider();

        var harness = new AgentHarness(provider, dir, clock, await provider.GetRequiredService<Hwid>().GetAsync(CancellationToken.None));
        await harness.Tokens.LoadAsync(CancellationToken.None);
        return harness;
    }

    /// <summary>What the agent's <c>ServerConnection</c> sends on (re)registration.</summary>
    public AgentRegisterRequest RegisterRequest(Guid? previousPcId = null) => new(
        Hwid,
        "CLUB-PC",
        "1.4.2",
        new HardwareInfo(new CpuInfo("Ryzen 5 5600", 6, 12), [], 16384, [], [], new NetworkInfo(Mac, "10.0.0.12", "Ethernet"), new OsInfo("10.0.22631", "22631"), []),
        "10.0.0.12",
        Mac,
        previousPcId);

    /// <summary>What the agent's <c>Heartbeat</c> sends.</summary>
    public static HeartbeatRequest Heartbeat(int policyVersion = 0) =>
        new(PcStatus.Free, null, "1.4.2", "1.4.2", 3600, "10.0.0.12", policyVersion, [], 0, true);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class FixedHardware(string board) : IHardwareIdSource
    {
        public Task<IReadOnlyList<string>> GetComponentsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([board, "cpu:AuthenticAMD-A20F10"]);
    }
}
