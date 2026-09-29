using System.Net.Http.Json;
using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Server.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Server.Tests;

/// <summary>Error envelope, trace id and server time (DESIGN §7.2, §7.3); <c>/health</c>; unknown route → 404.</summary>
public sealed class ErrorEnvelopeTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Health_is_ok()
    {
        using var response = await server.Http.GetAsync("/health");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("ok", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_api_route_is_404_with_route_and_never_501()
    {
        using var response = await server.Http.DeleteAsync("/api/v1/no/such/route?x=1");
        var body = await Contract.ReadErrorAsync(response, 404, "notFound");
        Assert.Equal("/api/v1/no/such/route", body.GetProperty("error").GetProperty("details").GetProperty("route").GetString());
    }

    [Fact]
    public async Task Valid_trace_id_is_echoed_as_the_raw_string()
    {
        const string trace = "6F1D2C3A-5B4E-4D7F-9A8B-0C1D2E3F4A5B";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/nope");
        request.Headers.Add(ApiErrorWriter.TraceHeader, trace);
        using var response = await server.Http.SendAsync(request);
        var body = await Contract.ReadErrorAsync(response, 404, "notFound");
        Assert.Equal(trace, response.Headers.GetValues(ApiErrorWriter.TraceHeader).Single());
        Assert.Equal(trace, body.GetProperty("error").GetProperty("traceId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task Missing_or_invalid_trace_id_gets_a_new_guid(string? trace)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/login") { Content = new StringContent("{}") };
        if (trace is not null)
        {
            request.Headers.Add(ApiErrorWriter.TraceHeader, trace);
        }

        using var response = await server.Http.SendAsync(request);
        var body = await Contract.ReadErrorAsync(response, 400, "validation");
        var echoed = response.Headers.GetValues(ApiErrorWriter.TraceHeader).Single();
        Assert.True(Guid.TryParse(echoed, out _), echoed);
        Assert.Equal(echoed, body.GetProperty("error").GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task Server_time_comes_from_the_clock_on_every_response()
    {
        var expected = ServerJson.FormatTime(server.Clock.GetUtcNow());
        foreach (var path in new[] { "/health", "/api/v1/nope" })
        {
            using var response = await server.Http.GetAsync(path);
            Assert.Equal(expected, response.Headers.GetValues(ApiErrorWriter.ServerTimeHeader).Single());
        }
    }

    [Theory]
    [InlineData(413)]
    [InlineData(415)]
    public async Task Bad_request_exception_is_always_400_validation(int status)
    {
        var middleware = new ApiErrorMiddleware(
            _ => throw new BadHttpRequestException("bad body", status), NullLogger<ApiErrorMiddleware>.Instance, server.Clock);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Contract.AssertError(JsonElement.Parse(((MemoryStream)context.Response.Body).ToArray()), "validation");
    }

    [Fact]
    public async Task Details_key_is_present_and_null_when_empty()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await ApiErrorWriter.WriteAsync(context, 500, ErrorCode.Internal, "Internal server error", null);

        var json = System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("\"details\":null", json, StringComparison.Ordinal);
        Contract.AssertError(JsonElement.Parse(json), "internal");
    }
}
