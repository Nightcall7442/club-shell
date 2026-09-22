using ClubShell.Windows.Network;

namespace ClubShell.Windows.Tests;

/// <summary>
/// Covers the part of <see cref="DnsFilter"/> that decides what may be blocked. The rest of the class writes to the
/// hosts file and drives netsh, which needs a machine; <see cref="DnsFilter.NormalizeDomains"/> and
/// <see cref="DnsFilter.IsProtected"/> are pure and are the single choke point every block goes through.
/// </summary>
public sealed class DnsFilterTests
{
    [Theory]
    [InlineData("steampowered.com")]
    [InlineData("api.steampowered.com")]
    [InlineData("STEAMCOMMUNITY.COM")]
    [InlineData("cdn.riotgames.com")]
    [InlineData("easyanticheat.net")]
    [InlineData("api.faceit.com")]
    public void IsProtected_CoversLauncherAndAntiCheatHosts(string host)
    {
        DnsFilter.IsProtected(host).Should().BeTrue();
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("torrent-site.example")]
    [InlineData("notsteampowered.com")]
    [InlineData("steampowered.com.evil.example")]
    public void IsProtected_DoesNotMatchLookalikes(string host)
    {
        DnsFilter.IsProtected(host).Should().BeFalse();
    }

    [Fact]
    public void NormalizeDomains_DropsProtectedDomains()
    {
        var result = DnsFilter.NormalizeDomains(new[] { "*.steampowered.com", "bad.example", "riotgames.com" });

        result.Should().Equal("bad.example");
    }

    [Fact]
    public void NormalizeDomains_StillNormalisesEverythingElse()
    {
        var result = DnsFilter.NormalizeDomains(new[] { "*.Bad.Example.", "bad.example", "", "not a host" });

        result.Should().Equal("bad.example");
    }
}
