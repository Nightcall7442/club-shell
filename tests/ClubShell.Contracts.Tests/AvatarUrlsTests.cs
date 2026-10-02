using ClubShell.Contracts.Users;

namespace ClubShell.Contracts.Tests;

/// <summary>An avatar is an absolute http(s) URL or a preset picture the Shell ships (<c>/avatars/&lt;id&gt;.svg</c>).</summary>
public sealed class AvatarUrlsTests
{
    [Theory]
    [InlineData("/avatars/wolf.svg")]
    [InlineData("/avatars/pixel-alien2.svg")]
    [InlineData("https://cdn.example.uz/a.png")]
    [InlineData("http://club.local/a.png")]
    public void Accepts_presets_and_web_urls(string value) => AvatarUrls.IsValid(value).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x/a.png")]
    [InlineData("file:///C:/Windows/a.png")]
    [InlineData("/avatars/../secret.svg")]
    [InlineData("/avatars/Wolf.svg")]
    [InlineData("/avatars/wolf.png")]
    [InlineData("/other/wolf.svg")]
    [InlineData("avatars/wolf.svg")]
    public void Refuses_anything_else(string value) => AvatarUrls.IsValid(value).Should().BeFalse();
}
