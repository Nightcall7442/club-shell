using System.Security.Cryptography;
using System.Text;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ClubShell.Agent.Tests;

public sealed class AccountInjectorTests
{
    private const string Password = "hunter2 secret";

    [Theory]
    [InlineData(LauncherType.Steam, false, null)]
    [InlineData(LauncherType.Steam, true, null)]
    [InlineData(LauncherType.Epic, false, null)]
    [InlineData(LauncherType.Epic, true, null)]
    [InlineData(LauncherType.Epic, false, "password")]
    [InlineData(LauncherType.Steam, false, AccountInjector.ExchangeCodeAuthType)]
    public void Launcher_arguments_never_carry_the_password_by_default(LauncherType launcher, bool sessionInjected, string? authType)
    {
        AccountInjector.CredentialArgs(launcher, "pool01", Password, authType, sessionInjected, allowPasswordOnCommandLine: false)
            .Should().BeNull();
    }

    [Fact]
    public void Password_reaches_the_command_line_only_when_allowed_and_no_session_was_injected()
    {
        AccountInjector.CredentialArgs(LauncherType.Steam, "pool01", Password, null, sessionInjected: false, allowPasswordOnCommandLine: true)
            .Should().Be("-login pool01 \"hunter2 secret\"");
        AccountInjector.CredentialArgs(LauncherType.Steam, "pool01", Password, null, sessionInjected: true, allowPasswordOnCommandLine: true)
            .Should().BeNull();
    }

    [Fact]
    public void Epic_exchange_code_is_passed_as_a_one_time_code()
    {
        string? args = AccountInjector.CredentialArgs(LauncherType.Epic, "pool01", "c0de", AccountInjector.ExchangeCodeAuthType, sessionInjected: false, allowPasswordOnCommandLine: false);

        args.Should().Be("-AUTH_LOGIN=unused -AUTH_PASSWORD=c0de -AUTH_TYPE=exchangecode");
    }

    [Fact]
    public async Task A_lease_is_described_for_the_log_without_its_account_login()
    {
        // The Agent log is readable by the kiosk user: a lease goes there by its id, never by the pooled account's login.
        byte[] key = Signing.DeriveAccountPoolKey(Encoding.ASCII.GetBytes("secret"));
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes(Password);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        var server = Substitute.For<IServerClient>();
        Game game = TestSupport.Game("Dota 2", LauncherType.Steam);
        server.LeaseAccountAsync(game.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AccountLease(
            Guid.NewGuid(), LauncherType.Steam, "pool_login_07", Convert.ToBase64String([.. nonce, .. cipher, .. tag]), null, DateTimeOffset.UtcNow.AddHours(2))));
        var tokens = Substitute.For<ITokenStore>();
        tokens.Agent.Returns(new AgentTokens(Guid.NewGuid(), "access", "refresh", "c2VjcmV0", DateTimeOffset.UtcNow.AddHours(1)));
        await using var pool = new AccountPool(server, tokens, TestSupport.Monitor(new AgentSettings()), SystemClock.Instance, NullLogger<AccountPool>.Instance);

        ActiveLease lease = await pool.LeaseAsync(game, Guid.NewGuid(), null, CancellationToken.None);

        lease.Username.Should().Be("pool_login_07");
        lease.ToString().Should().Contain(lease.LeaseId.ToString()).And.NotContain("pool_login_07").And.NotContain(Password);
    }

    [Fact]
    public void Steam_auto_login_reset_targets_the_kiosk_users_hive()
    {
        const string sid = "S-1-5-21-1111-2222-3333-1001";

        IReadOnlyList<(string Key, string Name, object Value, RegistryValueKind Kind)> values = AccountInjector.SteamAutoLogin(sid, string.Empty);

        values.Should().OnlyContain(v => v.Key == sid + @"\Software\Valve\Steam");
        values.Should().Contain((sid + @"\Software\Valve\Steam", "AutoLoginUser", (object)string.Empty, RegistryValueKind.String));
        values.Should().Contain((sid + @"\Software\Valve\Steam", "RememberPassword", (object)0, RegistryValueKind.DWord));
    }
}
