using ClubShell.Agent.Server;
using ClubShell.Contracts.Pcs;
using FluentAssertions;
using Xunit;

namespace ClubShell.Agent.Tests;

/// <summary>D-93: the club's uploaded pictures reach the Shell as full addresses on the club's server.</summary>
public sealed class ServerMediaTests
{
    [Fact]
    public void Server_paths_get_the_servers_address_and_full_urls_stay_as_they_are()
    {
        var shell = new ShellConfigOverride(Club: new ShellClub(
            LogoUrl: "/api/v1/media/a1",
            WallpaperUrl: "https://cdn.example.com/w.jpg",
            Banners: [new ClubBanner("b", "B", "/api/v1/media/b2")],
            Screensaver: new ShellScreensaver(true, 300, [new ScreensaverItem("/api/v1/media/c3", "image", 10)])));

        var club = CommandReceiver.WithServerMedia(shell, "https://club.example.uz/api/v1").Club!;

        club.LogoUrl.Should().Be("https://club.example.uz/api/v1/media/a1");
        club.WallpaperUrl.Should().Be("https://cdn.example.com/w.jpg");
        club.Banners![0].ImageUrl.Should().Be("https://club.example.uz/api/v1/media/b2");
        club.Screensaver!.Items[0].Url.Should().Be("https://club.example.uz/api/v1/media/c3");
        CommandReceiver.WithServerMedia(shell, null).Should().BeSameAs(shell);
    }
}
