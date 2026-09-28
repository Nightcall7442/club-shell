using System.Net;
using ClubShell.Server.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClubShell.Server.Tests;

/// <summary>Railway hosting knobs (DESIGN §2.3, §2.5): env <c>PORT</c> and <c>Proxy:ClientIpHeader</c>.</summary>
public sealed class HostingTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public void Port_makes_kestrel_listen_on_all_interfaces()
    {
        using var withPort = server.WithWebHostBuilder(b => b.UseSetting("PORT", "5987"));
        Assert.Equal("http://0.0.0.0:5987", withPort.Services.GetRequiredService<IConfiguration>()[WebHostDefaults.ServerUrlsKey]);
    }

    [Theory]
    [InlineData("X-Real-IP", "203.0.113.7", "203.0.113.7")]
    [InlineData("X-Real-IP", "garbage", "10.0.0.1")]
    [InlineData("", "203.0.113.7", "10.0.0.1")]
    public async Task Client_ip_header_replaces_remote_address_only_when_configured(string header, string value, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        context.Request.Headers["X-Real-IP"] = value;
        var middleware = new ClientIpMiddleware(_ => Task.CompletedTask, new ProxyOptions { ClientIpHeader = header });

        await middleware.InvokeAsync(context);

        Assert.Equal(IPAddress.Parse(expected), context.Connection.RemoteIpAddress);
    }
}
