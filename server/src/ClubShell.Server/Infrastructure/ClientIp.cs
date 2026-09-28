using System.Net;

namespace ClubShell.Server.Infrastructure;

public sealed class ProxyOptions
{
    /// <summary>Proxy CIDRs trusted for <c>X-Forwarded-For</c>/<c>-Proto</c> (<c>ForwardedHeaders</c>).</summary>
    public string[] Trusted { get; set; } = [];

    /// <summary>
    /// Header the proxy sets to the client IP (Railway: <c>X-Real-IP</c>, which publishes no proxy address list). Only for
    /// a server reachable exclusively through that proxy: anyone else could spoof it. Takes precedence over <see cref="Trusted"/>.
    /// </summary>
    public string ClientIpHeader { get; set; } = "";
}

/// <summary>Replaces <c>RemoteIpAddress</c> with <see cref="ProxyOptions.ClientIpHeader"/> (DESIGN §2.3 step 1); rate limits and audit read it.</summary>
public sealed class ClientIpMiddleware(RequestDelegate next, ProxyOptions options)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (options.ClientIpHeader.Length > 0 && IPAddress.TryParse(context.Request.Headers[options.ClientIpHeader].ToString().Trim(), out var ip))
        {
            context.Connection.RemoteIpAddress = ip;
        }

        return next(context);
    }
}
