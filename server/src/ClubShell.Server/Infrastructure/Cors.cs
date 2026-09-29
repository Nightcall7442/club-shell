namespace ClubShell.Server.Infrastructure;

/// <summary><c>Cors:*</c> (DESIGN §3.8): origins of the admin console, compared exactly.</summary>
public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>
/// CORS of the admin console, only on <c>/api/v1/admin/*</c> (DESIGN §3.8). First in the pipeline, so the headers are on
/// every answer, errors included (else the console cannot read the envelope). A preflight is <c>204</c> before any
/// authentication; an origin not in <see cref="CorsOptions.AllowedOrigins"/> gets no CORS headers at all.
/// </summary>
public sealed class CorsMiddleware(RequestDelegate next, CorsOptions options)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/admin", StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }

        var headers = context.Response.Headers;
        headers.Vary = "Origin";
        var origin = context.Request.Headers.Origin.ToString();
        var allowed = origin.Length > 0 && options.AllowedOrigins.Contains(origin, StringComparer.Ordinal);
        if (allowed)
        {
            headers.AccessControlAllowOrigin = origin;
            headers.AccessControlExposeHeaders = "ETag, X-Trace-Id, X-Server-Time, Retry-After";
        }

        if (HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            if (allowed)
            {
                headers.AccessControlAllowMethods = "GET, POST, PUT, PATCH, DELETE";
                headers.AccessControlAllowHeaders = "Authorization, Content-Type, Idempotency-Key";
                headers.AccessControlMaxAge = "600";
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
