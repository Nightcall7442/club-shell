using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Serialization;
using Npgsql;

namespace ClubShell.Server.Infrastructure;

/// <summary>API error thrown from any layer; <see cref="ApiErrorMiddleware"/> turns it into the envelope (DESIGN §7.2).</summary>
public sealed class ApiException(int status, ErrorCode code, string message, object? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public ErrorCode Code { get; } = code;
    public object? Details { get; } = details;
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    /// <summary><c>401</c>; <c>details.reason</c> is mandatory in every 401 (the agent refreshes on all but clockSkew/userToken).</summary>
    public static ApiException Unauthorized(string reason, string message, string? problem = null) =>
        new(StatusCodes.Status401Unauthorized, ErrorCode.Unauthorized, message, new { reason, problem });

    /// <summary><c>400 validation</c> with <c>details.field</c> and <c>details.reason</c> (DESIGN §7.2).</summary>
    public static ApiException Validation(string field, string reason, string? message = null) =>
        new(StatusCodes.Status400BadRequest, ErrorCode.Validation, message ?? $"Invalid {field}: {reason}", new { field, reason });

    /// <summary><c>403 forbidden</c> with <c>details.reason</c>.</summary>
    public static ApiException Forbidden(string reason, string message) =>
        new(StatusCodes.Status403Forbidden, ErrorCode.Forbidden, message, new { reason });

    /// <summary><c>404 notFound</c> with <c>details.what</c>.</summary>
    public static ApiException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, ErrorCode.NotFound, $"{what} not found", new { what });

    public static ApiException NotImplemented(string operationId) =>
        new(StatusCodes.Status501NotImplemented, ErrorCode.NotImplemented, $"Operation {operationId} is not implemented by this server", new { reason = "notImplemented" });
}

public static class ApiErrorWriter
{
    public const string TraceHeader = "X-Trace-Id";
    public const string ServerTimeHeader = "X-Server-Time";

    /// <summary>A valid GUID from the request is echoed as the raw string (not reformatted); otherwise a new one.</summary>
    public static string TraceId(HttpContext context)
    {
        if (context.Items.TryGetValue(TraceHeader, out var existing) && existing is string id)
        {
            return id;
        }

        var header = context.Request.Headers[TraceHeader].ToString();
        id = Guid.TryParse(header, out _) ? header : Guid.NewGuid().ToString();
        context.Items[TraceHeader] = id;
        return id;
    }

    /// <summary>Writes <c>{"error":{code,message,details,traceId}}</c>; <c>details</c> is always present (null when empty).</summary>
    public static Task WriteAsync(HttpContext context, int status, ErrorCode code, string message, object? details)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        JsonElement? element = details is null ? null : JsonSerializer.SerializeToElement(details, ServerJson.Options);
        var envelope = new ServerErrorEnvelope(new ServerError(code, message, element, TraceId(context)));
        return JsonDefaults.SerializeAsync(context.Response.Body, envelope, context.RequestAborted);
    }
}

/// <summary>Trace id, <c>X-Server-Time</c> on every response, exceptions to the error envelope. First in the pipeline.</summary>
public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger, TimeProvider clock)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = ApiErrorWriter.TraceId(context);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ApiErrorWriter.TraceHeader] = traceId;
            context.Response.Headers[ApiErrorWriter.ServerTimeHeader] = ServerJson.FormatTime(clock.GetUtcNow());
            return Task.CompletedTask;
        });

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = traceId });
        try
        {
            await next(context);
        }
        catch (ApiException ex) when (!context.Response.HasStarted)
        {
            foreach (var (name, value) in ex.Headers)
            {
                context.Response.Headers[name] = value;
            }

            await ApiErrorWriter.WriteAsync(context, ex.Status, ex.Code, ex.Message, ex.Details);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            // Minimal-API binding failures (malformed JSON, wrong types, 415 content type) with ThrowOnBadRequest, Kestrel's
            // 413 body limit. Always 400: validation's x-http-status, and the contract declares none of 408/413/415.
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status400BadRequest, ErrorCode.Validation, ex.Message, new { field = "body", reason = "format" });
        }
        catch (JsonException ex) when (!context.Response.HasStarted)
        {
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status400BadRequest, ErrorCode.Validation, "Malformed JSON body", new { field = ex.Path ?? "body", reason = "format" });
        }
        catch (NpgsqlException ex) when (ex.IsTransient && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Database unavailable");
            context.Response.Headers.RetryAfter = "5";
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, ErrorCode.ServerUnavailable, "Database unavailable", new { reason = "database" });
        }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Unhandled error");
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status500InternalServerError, ErrorCode.Internal, "Internal server error", null);
        }
    }
}
