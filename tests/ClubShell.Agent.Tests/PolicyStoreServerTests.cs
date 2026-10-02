using ClubShell.Agent.Policy;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Tests;

/// <summary>
/// The pilot PC never took the server policy (on-screen keyboard kept, a removed 05:00 shutdown kept): agent.json has no
/// <c>pcId</c>, the registration keeps it in the agent tokens, and <see cref="PolicyStore"/> asked only the settings.
/// </summary>
public sealed class PolicyStoreServerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_server_policy_is_fetched_with_the_pc_id_of_the_agent_tokens()
    {
        var pcId = Guid.NewGuid();
        var server = Substitute.For<IServerClient>();
        server.GetPoliciesAsync(pcId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new EtagResponse<PcPolicy>(PolicyFactory.Create(version: 14), "\"p14\"", false));
        var tokens = Substitute.For<ITokenStore>();
        tokens.Agent.Returns(new AgentTokens(pcId, "access", "refresh", "c2lnbmluZw==", DateTimeOffset.UtcNow.AddHours(1)));
        using var store = new PolicyStore(server, TestSupport.Monitor(new AgentSettings()), NullLogger<PolicyStore>.Instance,
            cachePath: Path.Combine(_dir.Root, "policies.json"), shippedDefaultsPath: Path.Combine(_dir.Root, "none.json"), tokens: tokens);

        var (policy, source) = await store.LoadAsync(force: true, CancellationToken.None);

        source.Should().Be(PolicySource.Server);
        policy.Version.Should().Be(14);
    }
}
