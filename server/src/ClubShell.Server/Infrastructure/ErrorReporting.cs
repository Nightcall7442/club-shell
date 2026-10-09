using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Extensions.Logging;

namespace ClubShell.Server.Infrastructure;

/// <summary>
/// Error reporting to Sentry (D-72). Not wired at all unless <see cref="IsConfigured"/> (Railway: the variable
/// <c>Sentry__Dsn</c>; the repository is public, so the DSN is never in a file).
/// <list type="bullet">
/// <item>Events: every <c>Error</c>/<c>Critical</c> log (the «Unhandled error» of <see cref="ApiErrorMiddleware"/>, a wallet
/// that differs from its ledger) and every <c>Warning</c> of this server that carries an exception (a failed tick, health,
/// webhook or maintenance pass, a lost socket write).</item>
/// <item>Nothing that signs in or identifies a person leaves the server: request headers are cut to <see cref="KeptHeaders"/>
/// (no <c>Authorization</c>, <c>X-Club-Key</c>, signatures, client IPs), the query string is dropped (the agent socket
/// carries its token in <c>?token=</c>, client search its phone or name), no body, no cookies, no user, and the staff PIN
/// logs (<see cref="Auth.StaffTokens"/>, <see cref="Platform.PlatformLog"/>) are never read.</item>
/// <item>Traces: a sample of the console and player requests; none of <c>/health</c>, the agent socket or the agent API
/// (a heartbeat every 30 s per PC).</item>
/// </list>
/// </summary>
public static class ErrorReporting
{
    /// <summary>The only request headers an event or a trace keeps.</summary>
    public static readonly IReadOnlySet<string> KeptHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Content-Length", "Content-Type", "Idempotency-Key", "User-Agent", ApiErrorWriter.TraceHeader,
    };

    /// <summary>Log categories never sent, not even as breadcrumbs: they log PINs and who typed a wrong one.</summary>
    private static readonly string[] SilentCategories = [typeof(Auth.StaffTokens).FullName!, typeof(Platform.PlatformLog).FullName!];

    /// <summary>Share of traced requests: reads are polled by the console every few seconds, writes are the money paths.</summary>
    public const double ReadSampleRate = 0.02;

    public const double WriteSampleRate = 0.2;

    /// <summary>A DSN in <c>Sentry:Dsn</c> or in <c>SENTRY_DSN</c>, the two places the SDK reads it from.</summary>
    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Sentry:Dsn"]) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SENTRY_DSN"));

    /// <summary>The <c>UseSentry</c> options; the <c>Sentry</c> configuration section is bound before this runs.</summary>
    public static void Configure(SentryAspNetCoreOptions o)
    {
        // Set here, after the configuration, so a variable cannot turn them on.
        o.SendDefaultPii = false;
        o.MaxRequestBodySize = RequestSize.None;
        o.MinimumEventLevel = LogLevel.Warning;
        o.AddLogEntryFilter((category, level, _, exception) => !Reported(category, level, exception));
        o.SetBeforeSend(e =>
        {
            Scrub(e.Request);
            return e;
        });
        o.SetBeforeSendTransaction(t =>
        {
            Scrub(t.Request);
            return t;
        });
        o.TracesSampler = c => SampleRate(c.TryGetHttpMethod(), c.TryGetHttpPath());
        // Railway sets the commit of the deploy; without it the SDK takes the assembly version.
        if (o.Release is null && Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA") is { Length: >= 12 } sha)
        {
            o.Release = $"clubshell-server@{sha[..12]}";
        }
    }

    /// <summary>
    /// Whether a log entry may reach Sentry (as an event or a breadcrumb):
    /// <list type="bullet">
    /// <item>never from <see cref="SilentCategories"/>;</item>
    /// <item>from the framework and libraries only <c>Error</c> and above: their information logs carry whole URLs with
    /// the query (Hosting's «Request starting … ?token=…»);</item>
    /// <item>from this server everything, but a <c>Warning</c> only with an exception (a wrong PIN, a refused signature
    /// are not errors).</item>
    /// </list>
    /// </summary>
    public static bool Reported(string category, LogLevel level, Exception? exception)
    {
        if (SilentCategories.Contains(category, StringComparer.Ordinal))
        {
            return false;
        }

        if (!category.StartsWith("ClubShell.", StringComparison.Ordinal))
        {
            return level >= LogLevel.Error;
        }

        return level != LogLevel.Warning || exception is not null;
    }

    /// <summary>Only <see cref="KeptHeaders"/>; no query string, body, cookies or client address.</summary>
    public static void Scrub(SentryRequest request)
    {
        request.QueryString = null;
        if (request.Url?.IndexOf('?') is >= 0 and var query)
        {
            request.Url = request.Url[..query];
        }

        request.Cookies = null;
        request.Data = null;
        foreach (var name in request.Headers.Keys.Where(name => !KeptHeaders.Contains(name)).ToList())
        {
            request.Headers.Remove(name);
        }

        request.Env.Remove("REMOTE_ADDR");
    }

    /// <summary>The trace sample rate of a request.</summary>
    public static double SampleRate(string? method, string? path)
    {
        path ??= "";
        if (path == "/health" || path.StartsWith("/ws/", StringComparison.Ordinal) || path.StartsWith("/api/v1/agents", StringComparison.Ordinal))
        {
            return 0;
        }

        return HttpMethods.IsGet(method ?? "") || HttpMethods.IsHead(method ?? "") ? ReadSampleRate : WriteSampleRate;
    }
}
