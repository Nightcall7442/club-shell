using System.Collections.Concurrent;
using System.Text;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace ClubShell.Server.Tests;

/// <summary>
/// Error reporting (D-72): what reaches Sentry and what never does. The rules in isolation, then the bytes the real
/// server hands to the Sentry transport for an unhandled error.
/// </summary>
public sealed partial class ErrorReportingTests
{
    [Fact]
    public void A_request_keeps_only_harmless_headers_and_loses_its_query_body_cookies_and_address()
    {
        var request = new SentryRequest
        {
            Url = "https://club.example/ws/agent?token=agent-token",
            QueryString = "?token=agent-token",
            Cookies = "session=abc",
            Data = "{\"pin\":\"1234\"}",
        };
        request.Headers["Authorization"] = "Bearer staff-token";
        request.Headers["X-Club-Key"] = "enrollment-key";
        request.Headers["X-Signature"] = "hmac";
        request.Headers["X-Real-IP"] = "203.0.113.9";
        request.Headers["User-Agent"] = "ClubShell.Agent/1.0.16";
        request.Headers["X-Trace-Id"] = "5b6c0f8e-0000-4000-8000-000000000000";
        request.Env["REMOTE_ADDR"] = "203.0.113.9";
        request.Env["SERVER_PORT"] = "8080";

        ErrorReporting.Scrub(request);

        Assert.Equal("https://club.example/ws/agent", request.Url);
        Assert.Null(request.QueryString);
        Assert.Null(request.Cookies);
        Assert.Null(request.Data);
        Assert.Equal(["User-Agent", "X-Trace-Id"], request.Headers.Keys.Order(StringComparer.Ordinal));
        Assert.False(request.Env.ContainsKey("REMOTE_ADDR"));
        Assert.Equal("8080", request.Env["SERVER_PORT"]);
    }

    [Theory]
    [InlineData("ClubShell.Server.Auth.StaffTokens", LogLevel.Error, true, false)]
    [InlineData("ClubShell.Server.Auth.StaffTokens", LogLevel.Information, false, false)]
    [InlineData("ClubShell.Server.Platform.PlatformLog", LogLevel.Information, false, false)]
    [InlineData("ClubShell.Server.Auth.AgentAuthMiddleware", LogLevel.Warning, false, false)]
    [InlineData("ClubShell.Server.Sessions.SessionTickWorker", LogLevel.Warning, true, true)]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning, true, false)]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Error, true, true)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information, false, false)]
    [InlineData("System.Net.Http.HttpClient.Default.LogicalHandler", LogLevel.Information, false, false)]
    [InlineData("ClubShell.Server.Infrastructure.ApiErrorMiddleware", LogLevel.Error, true, true)]
    [InlineData("ClubShell.Server.Infrastructure.MaintenanceWorker", LogLevel.Error, false, true)]
    [InlineData("ClubShell.Server.Agents.AgentEndpoints", LogLevel.Information, false, true)]
    public void Log_entries_reach_Sentry_only_by_the_rules(string category, LogLevel level, bool withException, bool reported)
    {
        Assert.Equal(reported, ErrorReporting.Reported(category, level, withException ? new InvalidOperationException() : null));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("https://key@o0.ingest.sentry.io/0", true)]
    public void Sentry_is_wired_only_with_a_DSN(string? dsn, bool configured)
    {
        var settings = new Dictionary<string, string?> { ["Sentry:Dsn"] = dsn };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        // Every other fixture of this suite starts without a DSN: Sentry 6 throws at startup when it is wired without one.
        var fromEnvironment = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SENTRY_DSN"));
        Assert.Equal(configured || fromEnvironment, ErrorReporting.IsConfigured(configuration));
    }

    [Theory]
    [InlineData("GET", "/health", 0)]
    [InlineData("GET", "/ws/agent", 0)]
    [InlineData("POST", "/api/v1/agents/0b0c4c2e-0000-4000-8000-000000000000/heartbeat", 0)]
    [InlineData("GET", "/api/v1/admin/overview", ErrorReporting.ReadSampleRate)]
    [InlineData("POST", "/api/v1/admin/wallet/topup", ErrorReporting.WriteSampleRate)]
    [InlineData(null, null, ErrorReporting.WriteSampleRate)]
    public void Traces_sample_the_console_and_never_the_agents(string? method, string? path, double rate)
    {
        Assert.Equal(rate, ErrorReporting.SampleRate(method, path));
    }
}

/// <summary>
/// The real server with a DSN and an in-memory transport. Alone in its collection: the Sentry SDK is process-wide, and a
/// fixture starting without a DSN would swap it out mid-test.
/// </summary>
[Collection(nameof(SentryCollection))]
public sealed class ErrorReportingServerTests(ErrorReportingTests.SentryServer server) : IClassFixture<ErrorReportingTests.SentryServer>
{
    [Fact]
    public async Task An_unhandled_error_reaches_Sentry_without_tokens_keys_pins_or_addresses()
    {
        // A PIN logged by the staff tokens must not ride along, not even as a breadcrumb.
        server.Services.GetRequiredService<ILogger<StaffTokens>>().LogError("Owner created with PIN {Pin}", "PIN-4821");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/test/boom?token=QUERY-TOKEN&q=%2B998901234567");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer AUTH-TOKEN");
        request.Headers.TryAddWithoutValidation("X-Club-Key", "CLUB-KEY");
        request.Headers.TryAddWithoutValidation("Cookie", "s=COOKIE-VALUE");
        request.Headers.TryAddWithoutValidation("X-Real-IP", "203.0.113.9");
        request.Headers.TryAddWithoutValidation("User-Agent", "sentry-test-agent");
        using var response = await server.CreateClient().SendAsync(request);
        Assert.Equal(500, (int)response.StatusCode);

        var sent = await server.Transport.WaitForAsync("boom-for-sentry");

        Assert.Contains("Unhandled error", sent);
        Assert.Contains("sentry-test-agent", sent);
        Assert.Contains("/test/boom", sent);
        foreach (var secret in new[] { "QUERY-TOKEN", "998901234567", "AUTH-TOKEN", "CLUB-KEY", "COOKIE-VALUE", "203.0.113.9", "PIN-4821" })
        {
            Assert.DoesNotContain(secret, sent);
        }
    }
}

[CollectionDefinition(nameof(SentryCollection), DisableParallelization = true)]
public sealed class SentryCollection;

public sealed partial class ErrorReportingTests
{
    /// <summary><see cref="ServerFixture"/> with a fake DSN, the transport below and a route that throws.</summary>
    public sealed class SentryServer : ServerFixture
    {
        public SentryServer()
        {
            Settings["Sentry:Dsn"] = "https://0123456789abcdef0123456789abcdef@o0.ingest.sentry.io/0";
        }

        public MemoryTransport Transport { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<SentryAspNetCoreOptions>(o => o.Transport = Transport);
                services.AddTransient<IStartupFilter, ThrowingRoute>();
            });
        }
    }

    /// <summary>After the whole pipeline: <c>/test/boom</c> throws an unexpected exception into it.</summary>
    private sealed class ThrowingRoute : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(context => context.Request.Path.StartsWithSegments("/test/boom")
                ? throw new InvalidOperationException("boom-for-sentry")
                : Task.CompletedTask);
        };
    }

    /// <summary>Keeps every envelope as the JSON the SDK would send.</summary>
    public sealed class MemoryTransport : ITransport
    {
        private readonly ConcurrentQueue<string> _sent = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            _sent.Enqueue(Encoding.UTF8.GetString(stream.ToArray()));
        }

        /// <summary>Everything sent so far, once an envelope containing <paramref name="marker"/> has arrived.</summary>
        public async Task<string> WaitForAsync(string marker)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (_sent.Any(e => e.Contains(marker, StringComparison.Ordinal)))
                {
                    return string.Join('\n', _sent);
                }

                await Task.Delay(100);
            }

            throw new TimeoutException($"No envelope with {marker}; sent: {string.Join('\n', _sent)}");
        }
    }
}
