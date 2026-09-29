using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ClubShell.Server.Tests;

/// <summary>Staff of the <c>Seed:Dev</c> club (owner 0000, cashier "Кассир Азиз" 1111) and signed admin requests (slice S4).</summary>
public static class Staff
{
    public const string OwnerPin = "0000";
    public const string CashierPin = "1111";

    /// <summary><c>POST /admin/login</c> (200 expected); the staff token.</summary>
    public static async Task<string> LoginAsync(ServerFixture server, string pin)
    {
        using var response = await server.Http.PostAsync("/api/v1/admin/login", JsonBody(new { pin }));
        var body = await Players.ReadAsync(response, 200);
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>An admin request with <c>Authorization: Bearer</c>, a JSON body when given and an <c>Idempotency-Key</c> when given.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null, Guid? key = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1/admin" + path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonBody(body);
        }

        if (key is { } k)
        {
            request.Headers.Add("Idempotency-Key", k.ToString());
        }

        return request;
    }

    /// <summary>Sends an admin request through the contract-validating client; the status and the JSON body.</summary>
    public static async Task<(int Status, JsonElement Body)> SendAsync(
        ServerFixture server, HttpMethod method, string path, string? token, object? body = null, Guid? key = null)
    {
        using var request = Request(method, path, token, body, key);
        using var response = await server.Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    /// <summary>Like <see cref="SendAsync"/>, asserting <paramref name="status"/>.</summary>
    public static async Task<JsonElement> ExpectAsync(
        ServerFixture server, int status, HttpMethod method, string path, string? token, object? body = null, Guid? key = null)
    {
        using var request = Request(method, path, token, body, key);
        using var response = await server.Http.SendAsync(request);
        return await Players.ReadAsync(response, status);
    }

    public static StringContent JsonBody(object body) => new(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
