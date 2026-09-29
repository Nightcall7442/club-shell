using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ClubShell.Server.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>The HTTP side of webhook delivery replaced: records every POST and answers the queued statuses (then 200).</summary>
public sealed class FakeWebhookClient : WebhookClient
{
    public ConcurrentQueue<int> Statuses { get; } = new();

    public ConcurrentQueue<(Uri Url, string Event, string Payload)> Calls { get; } = new();

    public override Task<int> PostAsync(Uri url, string @event, string payload, CancellationToken cancellationToken)
    {
        Calls.Enqueue((url, @event, payload));
        return Task.FromResult(Statuses.TryDequeue(out var status) ? status : 200);
    }
}

public sealed class WebhookServerFixture : ServerFixture
{
    public FakeWebhookClient Webhooks { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<WebhookClient>(Webhooks));
    }
}

/// <summary>
/// Webhook delivery (DESIGN §8, OQ-11): an address that is loopback, private, link-local (cloud metadata) or otherwise
/// not public is refused without a request (<c>lastStatus = 0</c>, not retried); a failed delivery is tried three more
/// times after the delays, then given up; every attempt writes <c>lastStatus</c>/<c>lastAt</c>; the body is
/// <c>AdminWebhookPayload</c> with <c>X-ClubShell-Event</c>.
/// </summary>
public sealed class WebhookTests(WebhookServerFixture server) : IClassFixture<WebhookServerFixture>
{
    private const string Public = "http://93.184.216.34/hook";

    [Fact]
    public void Only_public_addresses_are_allowed()
    {
        foreach (var ip in new[] { "93.184.216.34", "8.8.8.8", "172.32.0.1", "2001:4860:4860::8888" })
        {
            Assert.True(WebhookTargets.IsAllowed(IPAddress.Parse(ip)), ip);
        }

        foreach (var ip in new[]
                 {
                     "127.0.0.1", "10.0.0.1", "172.16.0.1", "172.31.255.255", "192.168.1.1", "169.254.169.254", "100.64.0.1", "0.0.0.0", "224.0.0.1",
                     "255.255.255.255", "::1", "::", "fe80::1", "fd00::1", "fc00::1", "::ffff:10.0.0.1", "::ffff:127.0.0.1", "ff02::1",
                 })
        {
            Assert.False(WebhookTargets.IsAllowed(IPAddress.Parse(ip)), ip);
        }
    }

    /// <summary>The IPv4 inside an IPv6 address (NAT64, 6to4, IPv4-compatible) is judged as IPv4; the discard and documentation prefixes never pass.</summary>
    [Theory]
    [InlineData("http://[64:ff9b::a9fe:a9fe]/", false)] // NAT64 of 169.254.169.254
    [InlineData("http://[64:ff9b::7f00:1]/", false)] // NAT64 of 127.0.0.1
    [InlineData("http://[64:ff9b:1::a9fe:a9fe]/", false)] // local-use NAT64, last 32 bits
    [InlineData("http://[64:ff9b:1:a9fe:a9:fe00:808:808]/", false)] // local-use NAT64, RFC 6052 /48 slots: 169.254.169.254 (last 32 bits are 8.8.8.8)
    [InlineData("http://[2002:c0a8:0101::]/", false)] // 6to4 of 192.168.1.1
    [InlineData("http://[2002:a9fe:a9fe::1]/", false)] // 6to4 of 169.254.169.254
    [InlineData("http://[::a9fe:a9fe]/", false)] // IPv4-compatible 169.254.169.254
    [InlineData("http://[::a00:1]/", false)] // IPv4-compatible 10.0.0.1
    [InlineData("http://[100::1]/", false)] // discard prefix
    [InlineData("http://[100::ffff:1:2:3]/", false)]
    [InlineData("http://[2001:db8::1]/", false)] // documentation
    [InlineData("http://[2001:db8:ffff::8]/", false)]
    [InlineData("http://[::ffff:10.0.0.1]/", false)] // mapped, as before
    [InlineData("http://[fd00::1]/", false)]
    [InlineData("http://93.184.216.34/", true)]
    [InlineData("http://[2001:4860:4860::8888]/", true)]
    [InlineData("http://[64:ff9b::808:808]/", true)] // NAT64 of 8.8.8.8
    [InlineData("http://[2002:5db8:d822::1]/", true)] // 6to4 of 93.184.216.34
    [InlineData("http://[::808:808]/", true)] // IPv4-compatible 8.8.8.8
    [InlineData("http://[100:0:0:1::1]/", true)] // outside 100::/64
    public async Task Addresses_hidden_in_ipv6_forms_are_judged_like_ipv4(string url, bool allowed)
    {
        Assert.Equal(allowed, await WebhookTargets.IsAllowedAsync(new Uri(url)));
        Assert.Equal(allowed, WebhookTargets.IsAllowed(IPAddress.Parse(new Uri(url).IdnHost.Trim('[', ']'))));
    }

    [Fact]
    public async Task Private_loopback_and_metadata_addresses_are_refused_without_a_request()
    {
        var urls = new[]
        {
            "http://127.0.0.1:8080/h", "http://10.1.2.3/h", "http://169.254.169.254/latest/meta-data", "http://[::1]/h", "http://localhost/h",
            "http://192.168.0.10/h", "http://[fd00::1]/h", "http://0x7f000001/h",
        };
        await SaveAsync(urls.Select((url, i) => Hook($"ssrf{i}", url)).ToArray());
        var before = server.Webhooks.Calls.Count;
        await EnqueueAsync("lowStock");
        await server.Services.GetRequiredService<WebhookWorker>().RunOnceAsync();

        Assert.Equal(before, server.Webhooks.Calls.Count);
        var hooks = (await ExpectAsync(server, 200, HttpMethod.Get, "/club", await LoginAsync(server, OwnerPin))).GetProperty("webhooks").EnumerateArray().ToList();
        Assert.All(hooks, h => Assert.Equal(0, h.GetProperty("lastStatus").GetInt32()));
        Assert.All(hooks, h => Assert.NotEqual(JsonValueKind.Null, h.GetProperty("lastAt").ValueKind));
        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM webhook_outbox WHERE sent_at IS NULL"));
        Assert.Equal(urls.Length, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM webhook_outbox WHERE attempts = 1"));
    }

    [Fact]
    public async Task A_failed_delivery_is_retried_three_times_with_delays_then_given_up()
    {
        await SaveAsync(Hook("ok-late", Public, events: ["lowStock"]), Hook("never", Public + "/never", events: ["shiftClosed"]), Hook("off", Public + "/off", enabled: false));
        var worker = server.Services.GetRequiredService<WebhookWorker>();
        server.Webhooks.Calls.Clear();

        // ok-late: 500, network error, 503, then 200.
        foreach (var status in new[] { 500, 0, 503 })
        {
            server.Webhooks.Statuses.Enqueue(status);
        }

        await EnqueueAsync("lowStock");
        var statuses = new List<int?>();
        await worker.RunOnceAsync();
        statuses.Add(await LastStatusAsync("ok-late"));
        await worker.RunOnceAsync(); // not due yet
        Assert.Single(server.Webhooks.Calls);
        foreach (var delay in WebhookWorker.RetryDelays)
        {
            server.Clock.Advance(delay);
            await worker.RunOnceAsync();
            statuses.Add(await LastStatusAsync("ok-late"));
        }

        Assert.Equal([500, 0, 503, 200], statuses);
        Assert.Equal(4, server.Webhooks.Calls.Count);
        var (url, @event, payload) = server.Webhooks.Calls.Last();
        Assert.Equal((Public, "lowStock"), (url.ToString(), @event));
        var body = JsonElement.Parse(payload);
        Contract.AssertMatches("AdminWebhookPayload", body);
        Assert.Equal(("lowStock", "Cola: осталось 2"), (body.GetProperty("event").GetString(), body.GetProperty("text").GetString()));

        // never: four 500s, then no more attempts. off: disabled, dropped without a request.
        server.Webhooks.Calls.Clear();
        for (var i = 0; i < 10; i++)
        {
            server.Webhooks.Statuses.Enqueue(500);
        }

        await EnqueueAsync("shiftClosed");
        await worker.RunOnceAsync();
        foreach (var delay in WebhookWorker.RetryDelays.Append(TimeSpan.FromHours(1)))
        {
            server.Clock.Advance(delay);
            await worker.RunOnceAsync();
        }

        Assert.Equal(4, server.Webhooks.Calls.Count);
        Assert.All(server.Webhooks.Calls, c => Assert.Equal(Public + "/never", c.Url.ToString()));
        Assert.Equal(500, await LastStatusAsync("never"));
        Assert.Null(await LastStatusAsync("off"));
        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM webhook_outbox WHERE sent_at IS NULL"));
        server.Webhooks.Statuses.Clear();
    }

    private async Task SaveAsync(params object[] hooks) =>
        await ExpectAsync(server, 200, HttpMethod.Patch, "/club", await LoginAsync(server, OwnerPin), new { webhooks = hooks });

    private static object Hook(string id, string url, string[]? events = null, bool enabled = true) =>
        new { id, url, events = events ?? ["lowStock", "shiftClosed"], enabled, lastStatus = (int?)null, lastAt = (string?)null };

    private async Task EnqueueAsync(string @event)
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var clubId = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(c, "SELECT id FROM clubs");
        await Webhooks.EnqueueAsync(c, null, clubId, @event, server.Clock.GetUtcNow(), "Cola: осталось 2", new { qty = 2 });
    }

    private Task<int?> LastStatusAsync(string id) =>
        Players.ScalarAsync<int?>(server, "SELECT last_status FROM webhooks WHERE id = @id", new { id });
}
