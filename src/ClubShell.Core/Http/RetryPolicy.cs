using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClubShell.Contracts.Errors;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace ClubShell.Core.Http;

/// <summary>
/// Resilience pipeline of the server <see cref="HttpClient"/> (ARCHITECTURE.md §5.1, SERVER_API.md §1 "Retry"):
/// total timeout → retry (exponential backoff with jitter, <c>Retry-After</c> honoured) → circuit breaker → per-attempt
/// timeout. Only <c>GET</c>/<c>PUT</c>/<c>DELETE</c> and <c>POST</c>s carrying an <c>Idempotency-Key</c> are retried.
/// </summary>
public static class RetryPolicy
{
    /// <summary>Named client used by <see cref="ServerClient"/>.</summary>
    public const string HttpClientName = "ClubShell.Server";

    /// <summary>Named client used for package/theme downloads (Bearer, long timeout, no retry pipeline: resume handles it).</summary>
    public const string DownloadHttpClientName = "ClubShell.Download";

    /// <summary>Host-configuration key (<c>ClubShell:Dev</c>) that relaxes TLS pinning / accepts self-signed certificates (<c>--dev</c>).</summary>
    public const string DevConfigurationKey = "ClubShell:Dev";

    /// <summary>Header that marks a POST as idempotent (retryable).</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private const long MaxRetryDelayMs = 3_600_000;

    /// <summary>
    /// <see langword="true"/> for transient outcomes: connection errors, per-attempt timeouts, HTTP 408/425/429/5xx.
    /// Mirrors <see cref="ErrorCodes.IsRetryable"/> at the HTTP layer.
    /// </summary>
    public static bool IsTransient(HttpResponseMessage? response, Exception? exception)
    {
        if (exception is HttpRequestException or TimeoutRejectedException)
        {
            return true;
        }

        if (response is null)
        {
            return false;
        }

        var status = (int)response.StatusCode;
        return status is 408 or 425 or 429 || status >= 500;
    }

    /// <summary><see langword="true"/> when <paramref name="request"/> may be replayed safely.</summary>
    public static bool IsIdempotent(HttpRequestMessage? request)
    {
        if (request is null)
        {
            return true;
        }

        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete || request.Method == HttpMethod.Head)
        {
            return true;
        }

        return request.Headers.Contains(IdempotencyKeyHeader);
    }

    /// <summary>Adds the strategies to <paramref name="builder"/> from <paramref name="server"/> settings.</summary>
    public static void ConfigurePipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder, ServerSettings server)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(server);

        var attempts = Math.Max(1, server.Retry.MaxAttempts);
        var attemptTimeout = TimeSpan.FromSeconds(Math.Max(1, server.TimeoutSec));
        var baseDelay = TimeSpan.FromMilliseconds(Math.Clamp(server.Retry.BaseDelayMs, 1, MaxRetryDelayMs));
        var maxDelay = TimeSpan.FromMilliseconds(Math.Clamp(Math.Max(server.Retry.MaxDelayMs, server.Retry.BaseDelayMs), 1, MaxRetryDelayMs));
        var totalTimeout = attemptTimeout * attempts + maxDelay * Math.Max(0, attempts - 1);
        var breakDuration = TimeSpan.FromSeconds(Math.Max(1, server.CircuitBreaker.OpenSec));

        builder.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Name = "total-timeout",
            Timeout = totalTimeout,
        });

        if (attempts > 1)
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                Name = "retry",
                MaxRetryAttempts = attempts - 1,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = baseDelay,
                MaxDelay = maxDelay,
                ShouldRetryAfterHeader = true,
                ShouldHandle = args =>
                {
                    _ = args.Context.Properties.TryGetValue(new ResiliencePropertyKey<HttpRequestMessage>("Resilience.Http.RequestMessage"), out var request);
                    return new ValueTask<bool>(IsIdempotent(request) && IsTransient(args.Outcome.Result, args.Outcome.Exception));
                },
            });
        }

        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            Name = "circuit-breaker",
            FailureRatio = 0.8,
            MinimumThroughput = Math.Max(2, server.CircuitBreaker.Failures),
            SamplingDuration = breakDuration,
            BreakDuration = breakDuration,
            ShouldHandle = args => new ValueTask<bool>(IsTransient(args.Outcome.Result, args.Outcome.Exception)),
        });

        builder.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Name = "attempt-timeout",
            Timeout = attemptTimeout,
        });
    }

    /// <summary>
    /// Registers the <see cref="HttpClientName"/> and <see cref="DownloadHttpClientName"/> clients: pooled
    /// <see cref="SocketsHttpHandler"/> (gzip/br, connection lifetime 5 min), optional SPKI pinning
    /// (<c>server.tlsPinSha256</c>), <c>ClubShell:Dev</c> relaxation, and the resilience pipeline built from the
    /// current <see cref="AgentSettings"/>.
    /// </summary>
    public static IHttpClientBuilder ConfigureServerHttpClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var dev = bool.TryParse(configuration[DevConfigurationKey], out var flag) && flag;

        services.AddHttpClient(DownloadHttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider => CreateHandler(provider, dev))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        var builder = services.AddHttpClient(HttpClientName)
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .ConfigurePrimaryHttpMessageHandler(provider => CreateHandler(provider, dev))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        builder.AddResilienceHandler("clubshell-server", (pipeline, context) =>
        {
            var settings = context.ServiceProvider.GetRequiredService<IOptionsMonitor<AgentSettings>>().CurrentValue;
            ConfigurePipeline(pipeline, settings.Server);
        });

        return builder;
    }

    /// <summary>
    /// Certificate validation with optional SPKI pinning: the chain must be valid and, when <paramref name="pins"/> is
    /// non-empty, the base64 SHA-256 of the SubjectPublicKeyInfo of the leaf or any chain certificate must match a pin.
    /// <paramref name="allowInsecure"/> (dev mode) accepts any certificate.
    /// </summary>
    public static bool ValidateServerCertificate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, IReadOnlyCollection<string> pins, bool allowInsecure)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (allowInsecure)
        {
            return true;
        }

        if (errors != SslPolicyErrors.None || certificate is null)
        {
            return false;
        }

        if (pins.Count == 0)
        {
            return true;
        }

        using var leaf = new X509Certificate2(certificate);
        if (MatchesPin(leaf, pins))
        {
            return true;
        }

        if (chain is not null)
        {
            foreach (var element in chain.ChainElements)
            {
                if (MatchesPin(element.Certificate, pins))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Base64 SHA-256 of the certificate's SubjectPublicKeyInfo (the value to put in <c>server.tlsPinSha256</c>).</summary>
    public static string ComputeSpkiPin(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    private static bool MatchesPin(X509Certificate2 certificate, IReadOnlyCollection<string> pins)
    {
        var pin = ComputeSpkiPin(certificate);
        foreach (var candidate in pins)
        {
            if (string.Equals(candidate, pin, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static SocketsHttpHandler CreateHandler(IServiceProvider provider, bool dev)
    {
        var settings = provider.GetRequiredService<IOptionsMonitor<AgentSettings>>();
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            ValidateServerCertificate(certificate, chain, errors, settings.CurrentValue.Server.TlsPinSha256, dev);
        return handler;
    }
}
