using System.Net.Http.Headers;
using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>A fixture with the platform administration on (<c>Platform:AdminKey</c>).</summary>
public sealed class PlatformServerFixture : ServerFixture
{
    public const string AdminKey = "platform-test-key-0123456789abcdef";

    public PlatformServerFixture() => Settings["Platform:AdminKey"] = AdminKey;
}

/// <summary>
/// Platform administration (beyond the contract, DESIGN §11 "Платформа"): several clubs on one server. A club gets a code,
/// an enrollment key and an owner; the console logs in with code + PIN, a club sees only its own data, a forgotten owner
/// PIN is reset by the platform, a disabled club lets nobody in. Each test builds its own clubs; with more than one club
/// a PIN login needs the code, so these tests always send it.
/// </summary>
public sealed class PlatformTests(PlatformServerFixture server) : IClassFixture<PlatformServerFixture>
{
    [Fact]
    public async Task Without_a_configured_key_the_platform_does_not_exist()
    {
        var plain = new ServerFixture();
        await ((IAsyncLifetime)plain).InitializeAsync();
        using var client = plain.CreateDefaultClient();
        using var response = await client.SendAsync(PlatformRequest(HttpMethod.Get, "/clubs", PlatformServerFixture.AdminKey));
        Assert.Equal(404, (int)response.StatusCode);
        await ((IAsyncLifetime)plain).DisposeAsync();
    }

    [Fact]
    public async Task A_wrong_key_is_401_and_the_bootstrap_club_is_listed_with_its_code()
    {
        Assert.Equal(401, (await SendAsync(HttpMethod.Get, "/clubs", key: "wrong-key-wrong-key-wrong-key")).Status);
        Assert.Equal(401, (await SendAsync(HttpMethod.Get, "/clubs", key: null)).Status);

        var (status, body) = await SendAsync(HttpMethod.Get, "/clubs");
        Assert.Equal(200, status);
        var first = body.GetProperty("items")[0];
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", first.GetProperty("code").GetString());
        Assert.True(first.GetProperty("keyFromConfig").GetBoolean());
        Assert.Contains(first.GetProperty("owners").EnumerateArray(), o => o.GetProperty("name").GetString() == "Владелец");
    }

    [Fact]
    public async Task A_new_club_has_its_own_key_owner_and_data_and_the_same_pin_logs_into_the_club_of_the_code()
    {
        var bootstrapCode = await BootstrapCodeAsync();
        // The owner PIN of the new club equals the bootstrap owner's: PINs are unique per club only.
        var (club, enrollmentKey) = await CreateClubAsync("Arena", "ARENA", OwnerPin);
        Assert.Matches("^[A-Za-z0-9]{24}$", enrollmentKey);

        // A PC registers with the new key and lands in the new club.
        using (var register = await TestAgent.RegisterAsync(server, TestAgent.RegisterBody(TestAgent.RandomHwid(), TestAgent.RandomMac()), enrollmentKey))
        {
            var pcId = (await Players.ReadAsync(register, 200)).GetProperty("pcId").GetGuid();
            Assert.Equal(club, await Players.ScalarAsync<Guid>(server, "SELECT club_id FROM pcs WHERE id = @pcId", new { pcId }));
        }

        // Several clubs now: the PIN alone is not enough.
        Contract.AssertError(await LoginBodyAsync(new { pin = OwnerPin }, 400), "validation", "required");

        var arena = await LoginWithCodeAsync("arena", OwnerPin);
        var home = await LoginWithCodeAsync(bootstrapCode, OwnerPin);
        var arenaSeats = (await ExpectAsync(server, 200, HttpMethod.Get, "/overview", arena)).GetProperty("seats").GetArrayLength();
        var homeSeats = (await ExpectAsync(server, 200, HttpMethod.Get, "/overview", home)).GetProperty("seats").GetArrayLength();
        Assert.Equal(1, arenaSeats);
        Assert.NotEqual(arenaSeats, homeSeats);

        // The cashier PIN of the bootstrap club does not open the new one, and an unknown code is a wrong PIN.
        Contract.AssertError(await LoginBodyAsync(new { pin = CashierPin, clubCode = "ARENA" }, 401), "unauthorized", "invalidPin");
        Contract.AssertError(await LoginBodyAsync(new { pin = OwnerPin, clubCode = "NOSUCH" }, 401), "unauthorized", "invalidPin");
    }

    [Fact]
    public async Task A_forgotten_owner_pin_is_reset_and_the_old_tokens_stop()
    {
        var (club, _) = await CreateClubAsync("Reset Club", "RESET", "5555");
        var token = await LoginWithCodeAsync("RESET", "5555");
        var owner = await OwnerOfAsync(club);

        Assert.Equal(200, (await SendAsync(HttpMethod.Post, $"/clubs/{club}/owners/{owner}/pin", new { pin = "7777" })).Status);
        Assert.Equal(401, (await Staff.SendAsync(server, HttpMethod.Get, "/me", token)).Status);
        Contract.AssertError(await LoginBodyAsync(new { pin = "5555", clubCode = "RESET" }, 401), "unauthorized", "invalidPin");
        await LoginWithCodeAsync("RESET", "7777");

        Assert.Equal(400, (await SendAsync(HttpMethod.Post, $"/clubs/{club}/owners/{owner}/pin", new { pin = "12ab" })).Status);
        Assert.Equal(404, (await SendAsync(HttpMethod.Post, $"/clubs/{club}/owners/{Guid.NewGuid()}/pin", new { pin = "8888" })).Status);

        // A second owner; a PIN taken in this club is refused.
        Assert.Equal(201, (await SendAsync(HttpMethod.Post, $"/clubs/{club}/owners", new { name = "Совладелец", pin = "4444" })).Status);
        Assert.Equal(400, (await SendAsync(HttpMethod.Post, $"/clubs/{club}/owners", new { name = "Ещё", pin = "4444" })).Status);
    }

    [Fact]
    public async Task A_disabled_club_lets_nobody_in_and_comes_back_when_enabled()
    {
        var (club, enrollmentKey) = await CreateClubAsync("Off Club", "OFFCLUB", "6060");
        var token = await LoginWithCodeAsync("OFFCLUB", "6060");

        Assert.Equal(200, (await SendAsync(HttpMethod.Patch, $"/clubs/{club}", new { disabled = true })).Status);
        Assert.Equal(401, (await Staff.SendAsync(server, HttpMethod.Get, "/me", token)).Status);
        Contract.AssertError(await LoginBodyAsync(new { pin = "6060", clubCode = "OFFCLUB" }, 401), "unauthorized", "invalidPin");
        using (var register = await TestAgent.RegisterAsync(server, TestAgent.RegisterBody(TestAgent.RandomHwid(), TestAgent.RandomMac()), enrollmentKey))
        {
            Assert.Equal(403, (int)register.StatusCode);
        }

        Assert.Equal(200, (await SendAsync(HttpMethod.Patch, $"/clubs/{club}", new { disabled = false })).Status);
        await LoginWithCodeAsync("OFFCLUB", "6060");
    }

    [Fact]
    public async Task A_rotated_key_keeps_the_previous_one_and_the_config_key_is_rotated_in_config()
    {
        var (club, oldKey) = await CreateClubAsync("Keys Club", "KEYS", "7171");
        var (status, body) = await SendAsync(HttpMethod.Post, $"/clubs/{club}/enrollment-key");
        Assert.Equal(200, status);
        var newKey = body.GetProperty("enrollmentKey").GetString()!;
        Assert.NotEqual(oldKey, newKey);
        foreach (var key in new[] { newKey, oldKey })
        {
            using var register = await TestAgent.RegisterAsync(server, TestAgent.RegisterBody(TestAgent.RandomHwid(), TestAgent.RandomMac()), key);
            Assert.Equal(200, (int)register.StatusCode);
        }

        var bootstrap = (await SendAsync(HttpMethod.Get, "/clubs")).Body.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(409, (await SendAsync(HttpMethod.Post, $"/clubs/{bootstrap}/enrollment-key")).Status);
    }

    [Fact]
    public async Task The_platform_page_on_the_console_origin_passes_cors_and_another_origin_does_not()
    {
        using var client = server.CreateDefaultClient();
        foreach (var (origin, allowed) in new[] { (ServerFixture.AdminOrigin, true), ("https://evil.example", false) })
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/platform/clubs");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            using var response = await client.SendAsync(preflight);
            Assert.Equal(204, (int)response.StatusCode);
            Assert.Equal(allowed, response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) && values.Single() == origin);
        }
    }

    [Fact]
    public async Task Club_input_is_validated()
    {
        await CreateClubAsync("Taken Code", "TAKEN", "8181");
        Assert.Equal(400, (await CreateRawAsync(new { name = "Dup", code = "taken", ownerName = "О", ownerPin = "1212" })).Status);
        Assert.Equal(400, (await CreateRawAsync(new { name = "Bad", code = "a-b", ownerName = "О", ownerPin = "1212" })).Status);
        Assert.Equal(400, (await CreateRawAsync(new { name = "Zone", timeZone = "Mars/Olympus", ownerName = "О", ownerPin = "1212" })).Status);
        Assert.Equal(400, (await CreateRawAsync(new { name = "Pin", ownerName = "О", ownerPin = "12" })).Status);
        Assert.Equal(400, (await CreateRawAsync(new { ownerName = "О", ownerPin = "1212" })).Status);
        Assert.Equal(404, (await SendAsync(HttpMethod.Patch, $"/clubs/{Guid.NewGuid()}", new { name = "X" })).Status);
    }

    private async Task<(Guid Club, string EnrollmentKey)> CreateClubAsync(string name, string code, string ownerPin)
    {
        // The fixture's clock stands still; a later created_at keeps the bootstrap club the first one, as on a real server.
        server.Clock.Advance(TimeSpan.FromSeconds(1));
        var (status, body) = await CreateRawAsync(new { name, code, timeZone = "Asia/Tashkent", ownerName = "Владелец " + name, ownerPin });
        Assert.True(status == 201, $"create club -> {status} {body}");
        return (body.GetProperty("club").GetProperty("id").GetGuid(), body.GetProperty("enrollmentKey").GetString()!);
    }

    private Task<(int Status, JsonElement Body)> CreateRawAsync(object body) => SendAsync(HttpMethod.Post, "/clubs", body);

    private async Task<string> BootstrapCodeAsync() =>
        (await SendAsync(HttpMethod.Get, "/clubs")).Body.GetProperty("items")[0].GetProperty("code").GetString()!;

    private Task<Guid> OwnerOfAsync(Guid club) =>
        Players.ScalarAsync<Guid>(server, "SELECT id FROM staff WHERE club_id = @club AND role = 'owner' ORDER BY created_at LIMIT 1", new { club });

    private async Task<string> LoginWithCodeAsync(string code, string pin)
    {
        var body = await LoginBodyAsync(new { pin, clubCode = code }, 200);
        return body.GetProperty("token").GetString()!;
    }

    private async Task<JsonElement> LoginBodyAsync(object body, int status)
    {
        using var response = await server.Http.PostAsync("/api/v1/admin/login", JsonBody(body));
        return await Players.ReadAsync(response, status);
    }

    private async Task<(int Status, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body = null, string? key = PlatformServerFixture.AdminKey)
    {
        // Beyond the contract: a client without the response validator.
        using var client = server.CreateDefaultClient();
        using var request = PlatformRequest(method, path, key, body);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static HttpRequestMessage PlatformRequest(HttpMethod method, string path, string? key, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1/platform" + path);
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        if (body is not null)
        {
            request.Content = JsonBody(body);
        }

        return request;
    }
}
