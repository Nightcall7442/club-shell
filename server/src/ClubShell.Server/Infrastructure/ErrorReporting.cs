using System.Diagnostics;
using Npgsql;
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
/// webhook or maintenance pass, a lost socket write); one of each kind an hour (<see cref="RepeatThrottle"/>).</item>
/// <item>Nothing that signs in or identifies a person leaves the server: request headers are cut to <see cref="KeptHeaders"/>
/// (no <c>Authorization</c>, <c>X-Club-Key</c>, signatures, client IPs), the query string is dropped (the agent socket
/// carries its token in <c>?token=</c>, client search its phone or name), no body, no cookies, no user, no Sentry Logs, and
/// the staff PIN logs (<see cref="Auth.StaffTokens"/>, <see cref="Platform.PlatformLog"/>) are never read.</item>
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

    private static readonly RepeatThrottle Throttle = new(TimeProvider.System);

    /// <summary>
    /// Whether to wire Sentry: a DSN where the SDK takes it from (<c>Sentry:Dsn</c> unless empty, else <c>SENTRY_DSN</c>) that
    /// the SDK will accept, and <c>Sentry:*</c> values it will accept. The SDK throws on either inside
    /// <c>builder.Build()</c>, so a typo in a Railway variable would stop the server; here it only turns Sentry off, with
    /// one line on stderr that never repeats the DSN.
    /// </summary>
    public static bool IsConfigured(IConfiguration configuration)
    {
        var dsn = configuration["Sentry:Dsn"] is { Length: > 0 } fromConfig ? fromConfig : Environment.GetEnvironmentVariable("SENTRY_DSN");
        if (string.IsNullOrWhiteSpace(dsn))
        {
            return false;
        }

        if (DsnProblem(dsn) is { } problem)
        {
            Console.Error.WriteLine($"Sentry is off: the DSN is not valid ({problem}); expected https://<key>@<host>/<project>");
            return false;
        }

        try
        {
            configuration.GetSection("Sentry").Bind(new SentryAspNetCoreOptions());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sentry is off: a Sentry:* setting is not valid ({ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message})");
            return false;
        }

        return true;
    }

    /// <summary>Why the SDK's <c>Dsn.Parse</c> (6.12.0) would throw on <paramref name="dsn"/>, or null.</summary>
    public static string? DsnProblem(string dsn)
    {
        if (!Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return "not an http(s) address";
        }

        if (string.IsNullOrWhiteSpace(uri.UserInfo.Split(':')[0]))
        {
            return "no public key";
        }

        return string.IsNullOrWhiteSpace(uri.AbsoluteUri[(uri.AbsoluteUri.LastIndexOf('/') + 1)..]) ? "no project id" : null;
    }

    /// <summary>The <c>UseSentry</c> options; the <c>Sentry</c> configuration section is bound before this runs.</summary>
    public static void Configure(SentryAspNetCoreOptions o)
    {
        // Set here, after the configuration, so a variable cannot turn them on. Sentry Logs would skip the log filter below.
        o.SendDefaultPii = false;
        o.MaxRequestBodySize = RequestSize.None;
        o.EnableLogs = false;
        o.MinimumEventLevel = LogLevel.Warning;
        o.AddLogEntryFilter((category, level, _, exception) => !Reported(category, level, exception));
        o.SetBeforeSend(e =>
        {
            if (!Throttle.Admit(e))
            {
                return null;
            }

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
        o.Release ??= ReleaseFor(Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA"));
    }

    /// <summary><c>clubshell-server@&lt;12 hex&gt;</c> of a commit, or null.</summary>
    public static string? ReleaseFor(string? sha) => sha is { Length: >= 12 } ? $"clubshell-server@{sha[..12]}" : null;

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

/// <summary>
/// One Sentry event of a kind per <see cref="Window"/> (D-72). A worker failing every second, a dead database answering
/// every poll and heartbeat with «Database unavailable», or a bug on the polled overview would otherwise send thousands of
/// copies of one error: the free plan's month (5 000 errors) would be gone within the hour, and later errors refused. The
/// next event of a kind after the window carries <c>repeatsDropped</c>; Railway's log still has every copy.
/// </summary>
public sealed class RepeatThrottle(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Kinds remembered at most; past it the memory starts over (a flood of distinct kinds is not throttled).</summary>
    private const int MaxKinds = 1000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Sent, int Dropped)> _kinds = new(StringComparer.Ordinal);

    /// <summary>Whether to send <paramref name="e"/>; when it is sent after dropped repeats, it says how many.</summary>
    public bool Admit(SentryEvent e)
    {
        var kind = KindOf(e);
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            var known = _kinds.TryGetValue(kind, out var seen);
            if (known && now - seen.Sent < Window)
            {
                _kinds[kind] = (seen.Sent, seen.Dropped + 1);
                return false;
            }

            if (!known && _kinds.Count >= MaxKinds)
            {
                _kinds.Clear();
            }

            _kinds[kind] = (now, 0);
            if (seen.Dropped > 0)
            {
                e.SetExtra("repeatsDropped", seen.Dropped);
            }

            return true;
        }
    }

    /// <summary>
    /// The logger and its message template, the exception type, where in this server's code it was thrown, and the
    /// PostgreSQL error code: two bugs failing inside the same library call are still two kinds.
    /// </summary>
    public static string KindOf(SentryEvent e)
    {
        var exception = e.Exception;
        var thrownAt = exception is null
            ? null
            : new StackTrace(exception, false).GetFrames()
                .Select(f => f.GetMethod())
                .FirstOrDefault(m => m?.DeclaringType?.FullName?.StartsWith("ClubShell.", StringComparison.Ordinal) == true);
        var sqlState = (exception as PostgresException ?? exception?.InnerException as PostgresException)?.SqlState;
        return string.Join('|', e.Logger, e.Message?.Message ?? e.Message?.Formatted, exception?.GetType().FullName,
            thrownAt is null ? null : $"{thrownAt.DeclaringType!.FullName}.{thrownAt.Name}", sqlState);
    }
}
