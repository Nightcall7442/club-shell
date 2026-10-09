using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Sessions;
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
/// server hands to the Sentry transport.
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

    private const string Dsn = "https://0123456789abcdef0123456789abcdef@sentry.invalid/0";

    /// <summary>
    /// The SDK throws inside <c>builder.Build()</c> on a DSN or a <c>Sentry:*</c> value it cannot take, which would stop the
    /// server; such settings only leave Sentry off. (SENTRY_DSN is cleared for this suite by <see cref="SentryFreeSuite"/>.)
    /// </summary>
    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("", null, null, false)]
    [InlineData(" ", null, null, false)]
    [InlineData(Dsn, null, null, true)]
    [InlineData(" " + Dsn + " ", null, null, true)]
    [InlineData("\"" + Dsn + "\"", null, null, false)]
    [InlineData("0123456789abcdef0123456789abcdef", null, null, false)]
    [InlineData("https://sentry.invalid/0", null, null, false)]
    [InlineData(Dsn + "/", null, null, false)]
    [InlineData(Dsn, "SampleRate", "0", false)]
    [InlineData(Dsn, "TracesSampleRate", "100", false)]
    [InlineData(Dsn, "TracesSampleRate", "1.0", true)]
    public void Sentry_is_wired_only_with_settings_the_SDK_accepts(string? dsn, string? option, string? value, bool configured)
    {
        var settings = new Dictionary<string, string?> { ["Sentry:Dsn"] = dsn };
        if (option is not null)
        {
            settings[$"Sentry:{option}"] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.Equal(configured, ErrorReporting.IsConfigured(configuration));
    }

    [Fact]
    public void Configure_wins_over_the_configuration_on_privacy_and_installs_the_sampler()
    {
        var o = new SentryAspNetCoreOptions { SendDefaultPii = true, MaxRequestBodySize = RequestSize.Always, EnableLogs = true };

        ErrorReporting.Configure(o);

        Assert.False(o.SendDefaultPii);
        Assert.Equal(RequestSize.None, o.MaxRequestBodySize);
        Assert.False(o.EnableLogs);
        Assert.Equal(LogLevel.Warning, o.MinimumEventLevel);
        double? Rate(string method, string path) => o.TracesSampler!(new TransactionSamplingContext(
            new TransactionContext("test", "http.server"),
            new Dictionary<string, object?> { ["__HttpMethod"] = method, ["__HttpPath"] = path }));
        Assert.Equal(0, Rate("GET", "/ws/agent"));
        Assert.Equal(0, Rate("POST", "/api/v1/agents/0b0c4c2e-0000-4000-8000-000000000000/heartbeat"));
        Assert.Equal(ErrorReporting.WriteSampleRate, Rate("POST", "/api/v1/admin/wallet/topup"));
    }

    [Theory]
    [InlineData("33b9afa5f4ed341a8d3620b7965322dac280e62a", "clubshell-server@33b9afa5f4ed")]
    [InlineData("33b9afa5f4e", null)]
    [InlineData(null, null)]
    public void The_release_is_the_deployed_commit(string? sha, string? release)
    {
        Assert.Equal(release, ErrorReporting.ReleaseFor(sha));
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

    [Fact]
    public void A_repeated_error_is_sent_once_an_hour_and_the_next_one_says_how_many_were_dropped()
    {
        var clock = new FakeClock();
        var throttle = new RepeatThrottle(clock);
        SentryEvent Tick() => new(new InvalidOperationException("db down"))
        {
            Logger = "ClubShell.Server.Sessions.SessionTickWorker",
            Message = new SentryMessage { Message = "Session tick failed" },
        };

        Assert.True(throttle.Admit(Tick()));
        Assert.False(throttle.Admit(Tick()));
        clock.Advance(RepeatThrottle.Window - TimeSpan.FromSeconds(1));
        Assert.False(throttle.Admit(Tick()));

        clock.Advance(TimeSpan.FromSeconds(1));
        var next = Tick();
        Assert.True(throttle.Admit(next));
        Assert.Equal(2, next.Extra["repeatsDropped"]);
        Assert.False(throttle.Admit(Tick()));
    }

    [Fact]
    public void Other_kinds_pass_the_throttle_at_once()
    {
        var throttle = new RepeatThrottle(new FakeClock());
        SentryEvent Of(Exception exception, string template) => new(exception) { Logger = "ClubShell.Server.X", Message = new SentryMessage { Message = template } };

        Assert.True(throttle.Admit(Of(new InvalidOperationException(), "Session tick failed")));
        Assert.True(throttle.Admit(Of(new InvalidOperationException(), "Health pass failed")));
        Assert.True(throttle.Admit(Of(new ArgumentException(), "Session tick failed")));
        // The same type and template thrown from two places of this server's code: two bugs.
        Assert.True(throttle.Admit(Of(Thrown(ThrowHere), "Unhandled error")));
        Assert.True(throttle.Admit(Of(Thrown(ThrowThere), "Unhandled error")));
        Assert.False(throttle.Admit(Of(Thrown(ThrowHere), "Unhandled error")));
    }

    private static void ThrowHere() => throw new InvalidOperationException("here");

    private static void ThrowThere() => throw new InvalidOperationException("there");

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("did not throw");
    }
}

/// <summary>
/// The real server with a DSN, production's client-IP header, configuration that tries to turn on the user, request bodies
/// and Sentry Logs, every positive trace sample forced to 1, and an in-memory transport.
/// </summary>
[Collection(nameof(SentryCollection))]
public sealed class ErrorReportingServerTests(ErrorReportingTests.SentryServer server) : IClassFixture<ErrorReportingTests.SentryServer>
{
    [Fact]
    public async Task Errors_and_traces_reach_Sentry_without_tokens_keys_pins_bodies_or_addresses()
    {
        // The PIN logs of the staff tokens and a warning without an exception must not ride along, not even as breadcrumbs.
        server.Services.GetRequiredService<ILogger<StaffTokens>>().LogWarning("No staff yet: owner created with PIN {Pin}", "PIN-4821");
        server.Services.GetRequiredService<ILogger<AgentAuthMiddleware>>().LogWarning("Request signature rejected {Marker}", "SIGNATURE-MARKER");
        // A failed worker pass is an event, once an hour however often it repeats.
        for (var tick = 0; tick < 3; tick++)
        {
            server.Services.GetRequiredService<ILogger<SessionTickWorker>>().LogWarning(new InvalidOperationException("tick-for-sentry"), "Session tick failed");
        }

        var client = server.CreateClient();
        using (var health = await client.GetAsync("/health"))
        {
            Assert.True(health.IsSuccessStatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/test/boom?token=QUERY-TOKEN&q=%2B998901234567")
        {
            Content = new StringContent("{\"pin\":\"BODY-PIN-7777\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer AUTH-TOKEN");
        request.Headers.TryAddWithoutValidation("X-Club-Key", "CLUB-KEY");
        request.Headers.TryAddWithoutValidation("Cookie", "s=COOKIE-VALUE");
        request.Headers.TryAddWithoutValidation("X-Real-IP", "203.0.113.9");
        request.Headers.TryAddWithoutValidation("User-Agent", "sentry-test-agent");
        using var response = await client.SendAsync(request);
        Assert.Equal(500, (int)response.StatusCode);

        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
        var sent = await server.Transport.WaitForAsync("boom-for-sentry", "tick-for-sentry", "\"type\":\"transaction\"");

        Assert.Contains("Unhandled error", sent);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sent, "\"value\":\"tick-for-sentry\""));
        Assert.Contains("sentry-test-agent", sent);
        Assert.Contains("/test/boom", sent);
        Assert.DoesNotContain("\"type\":\"log\"", sent);
        Assert.DoesNotContain("GET /health", sent);
        foreach (var secret in new[]
                 {
                     "QUERY-TOKEN", "998901234567", "AUTH-TOKEN", "CLUB-KEY", "COOKIE-VALUE", "203.0.113.9", "PIN-4821", "BODY-PIN-7777",
                     "SIGNATURE-MARKER",
                 })
        {
            Assert.DoesNotContain(secret, sent);
        }
    }
}

/// <summary>
/// Keeps the process-wide Sentry hub and its global exception handlers away from the parallel fixtures: only this
/// collection starts a server with a DSN.
/// </summary>
[CollectionDefinition(nameof(SentryCollection), DisableParallelization = true)]
public sealed class SentryCollection;

/// <summary>A DSN in the shell that runs the tests would turn every fixture into a real Sentry client.</summary>
internal static class SentryFreeSuite
{
    [ModuleInitializer]
    internal static void ClearDsn()
    {
        Environment.SetEnvironmentVariable("SENTRY_DSN", null);
        Environment.SetEnvironmentVariable("Sentry__Dsn", null);
    }
}

public sealed partial class ErrorReportingTests
{
    /// <summary><see cref="ServerFixture"/> with a fake DSN, the transport below and a route that throws.</summary>
    public sealed class SentryServer : ServerFixture
    {
        public SentryServer()
        {
            Settings["Sentry:Dsn"] = Dsn;
            Settings["Proxy:ClientIpHeader"] = "X-Real-IP";
            Settings["Sentry:SendDefaultPii"] = "true";
            Settings["Sentry:MaxRequestBodySize"] = "Always";
            Settings["Sentry:EnableLogs"] = "true";
        }

        public MemoryTransport Transport { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<SentryAspNetCoreOptions>(o =>
                {
                    o.Transport = Transport;
                    var sampler = o.TracesSampler!;
                    o.TracesSampler = c => sampler(c) > 0 ? 1.0 : 0.0;
                });
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

        /// <summary>Everything sent so far, once every one of <paramref name="markers"/> has arrived.</summary>
        public async Task<string> WaitForAsync(params string[] markers)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var sent = string.Join('\n', _sent);
                if (markers.All(m => sent.Contains(m, StringComparison.Ordinal)))
                {
                    return sent;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException($"Not all of {string.Join(", ", markers)} were sent; sent: {string.Join('\n', _sent)}");
        }
    }
}
