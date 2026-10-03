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

    /// <summary>
    /// The same request twice under one <c>Idempotency-Key</c>: the second answer is the first replayed (same body,
    /// <c>Idempotent-Replayed: true</c>); the first answer.
    /// </summary>
    public static async Task<JsonElement> ReplayedAsync(ServerFixture server, int status, HttpMethod method, string path, string token, object body)
    {
        var key = Guid.NewGuid();
        var first = await ExpectAsync(server, status, method, path, token, body, key);
        using var again = await server.Http.SendAsync(Request(method, path, token, body, key));
        Assert.True(JsonElement.DeepEquals(first, await Players.ReadAsync(again, status)), $"{path}: the replay differs from {first}");
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        return first;
    }

    /// <summary>
    /// The counter takes money only in an open shift (<c>409 shiftClosed</c>): opens one with an empty drawer, unless the club
    /// has one open already (<c>409 shiftOpen</c>).
    /// </summary>
    public static async Task OpenShiftAsync(ServerFixture server, string token)
    {
        var (status, body) = await SendAsync(server, HttpMethod.Post, "/shift/open", token, new { openingCash = 0 });
        if (status != 200)
        {
            Assert.True(status == 409, $"/shift/open: {status} {body}");
            Contract.AssertError(body, "conflict", "shiftOpen");
        }
    }

    /// <summary>A fresh club API key <c>ck_…</c>: the owner rotates it (<c>adminRotateApiKey</c>).</summary>
    public static async Task<string> ApiKeyAsync(ServerFixture server) =>
        (await ExpectAsync(server, 200, HttpMethod.Post, "/club/api-key", await LoginAsync(server, OwnerPin), new { }))
            .GetProperty("apiKey").GetString()!;

    /// <summary>Removes the club API key (no <c>ck_</c> is accepted until the owner reads or rotates one).</summary>
    public static Task ClearApiKeyAsync(ServerFixture server) =>
        Players.ExecuteAsync(server, "UPDATE clubs SET api_key_hash = NULL, api_key_sealed = NULL");

    public static StringContent JsonBody(object body) => new(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
