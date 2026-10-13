using System.Text.RegularExpressions;
using ClubShell.Server.Auth;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>A first start as in production: no dev seed, so the staff table is empty; the server's log kept from the start.</summary>
public sealed class OwnerBootstrapFixture : ServerFixture
{
    public OwnerBootstrapFixture() => Settings["Seed:Dev"] = "false";

    public LogRecorder Logs { get; } = new();

    /// <summary>The fixture's data directory (pepper, owner PIN file); the server creates it on its first start.</summary>
    public string Data => DataDir;

    public string OwnerPinFile => Path.Combine(DataDir, "owner-pin.txt");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(Logs));
    }
}

/// <summary>
/// The first owner without the PIN in the log (D-77): a generated PIN goes to a 0600 file in the data directory and only its
/// path to the log; restarts remind without it and drop the file once the PIN changed; a configured <c>Club:OwnerPin</c> is
/// checked like any staff PIN and never echoed; a start that cannot create the owner fails and creates nobody.
/// </summary>
public sealed class OwnerBootstrapTests(OwnerBootstrapFixture server) : IClassFixture<OwnerBootstrapFixture>
{
    [Fact]
    public async Task A_generated_pin_is_written_to_a_private_file_logged_by_path_only_and_the_file_goes_once_the_pin_changed()
    {
        _ = server.Services; // first start
        var pin = (await File.ReadAllTextAsync(server.OwnerPinFile)).Trim();
        Assert.Matches("^[0-9]{6}$", pin);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(server.OwnerPinFile));
        }

        var me = (await ExpectAsync(server, 200, HttpMethod.Get, "/me", await LoginAsync(server, pin))).GetProperty("staff");
        Assert.Equal("owner", me.GetProperty("role").GetString());
        Assert.Contains(server.Logs.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains(server.OwnerPinFile, StringComparison.Ordinal));
        AssertNoLineHas(pin);

        // A restart while the owner still uses it: a reminder, the file stays.
        var staffTokens = server.Services.GetRequiredService<StaffTokens>();
        var logged = server.Logs.Lines.Count;
        await staffTokens.EnsureOwnerAsync(null);
        Assert.True(File.Exists(server.OwnerPinFile));
        Assert.Contains(server.Logs.Lines.Skip(logged), l => l.Level == LogLevel.Warning && l.Message.Contains("still signs in with the generated PIN", StringComparison.Ordinal));

        // The owner picks a PIN of their own: the next start removes the file.
        var owner = await LoginAsync(server, pin);
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{me.GetProperty("id").GetString()}", owner, new { pin = "70417041" });
        await staffTokens.EnsureOwnerAsync(null);
        Assert.False(File.Exists(server.OwnerPinFile));
        await LoginAsync(server, "70417041");

        // A Club:OwnerPin left in the settings: still the owner's PIN — change it; another value — remove it. Never echoed.
        logged = server.Logs.Lines.Count;
        await staffTokens.EnsureOwnerAsync("70417041");
        await staffTokens.EnsureOwnerAsync("8642");
        var reminders = server.Logs.Lines.Skip(logged).Where(l => l.Category == typeof(StaffTokens).FullName).ToList();
        Assert.Contains(reminders, l => l.Level == LogLevel.Warning && l.Message.Contains("still the PIN of an active owner", StringComparison.Ordinal));
        Assert.Contains(reminders, l => l.Level == LogLevel.Information && l.Message.Contains("Club:OwnerPin is not used", StringComparison.Ordinal));
        AssertNoLineHas(pin, "70417041", "8642");
    }

    [Fact]
    public async Task A_start_that_cannot_create_the_owner_fails_without_echoing_the_pin_and_creates_nobody()
    {
        var empty = new OwnerBootstrapFixture();
        await ((IAsyncLifetime)empty).InitializeAsync();
        try
        {
            Directory.CreateDirectory(empty.Data);
            var blocker = Path.Combine(empty.Data, "blocker");
            await File.WriteAllTextAsync(blocker, "a file where a directory is expected");
            foreach (var (pin, file, expected) in new[]
                     {
                         ("12", empty.OwnerPinFile, "Club:OwnerPin"), ("12a4", empty.OwnerPinFile, "Club:OwnerPin"),
                         ("123456789", empty.OwnerPinFile, "Club:OwnerPin"), ("", "", "Auth:OwnerPinPath"),
                         ("", Path.Combine(blocker, "owner-pin.txt"), null),
                     })
            {
                using var start = empty.WithWebHostBuilder(b => b.UseSetting("Club:OwnerPin", pin).UseSetting("Auth:OwnerPinPath", file));
                var error = Assert.ThrowsAny<Exception>(() => start.Services).GetBaseException();
                if (expected is null)
                {
                    Assert.IsAssignableFrom<IOException>(error); // the file could not be written
                }
                else
                {
                    Assert.IsType<InvalidOperationException>(error);
                    Assert.Contains(expected, error.Message, StringComparison.Ordinal);
                    Assert.True(pin.Length == 0 || !error.Message.Contains(pin, StringComparison.Ordinal), error.Message);
                }

                Assert.False(File.Exists(empty.OwnerPinFile));
            }

            // A valid configured PIN starts the server: nobody was created before it, no file is written, the value is not logged.
            using var good = empty.WithWebHostBuilder(b => b.UseSetting("Club:OwnerPin", "482916"));
            _ = good.Services;
            Assert.Equal(1, await StaffCountAsync(good));
            using var client = good.CreateDefaultClient();
            using var login = await client.PostAsync("/api/v1/admin/login", JsonBody(new { pin = "482916" }));
            Assert.Equal("owner", (await Players.ReadAsync(login, 200)).GetProperty("staff").GetProperty("role").GetString());
            Assert.False(File.Exists(empty.OwnerPinFile));
            Assert.DoesNotContain(empty.Logs.Lines, l => Mentions(l, "482916"));
        }
        finally
        {
            await ((IAsyncLifetime)empty).DisposeAsync();
        }
    }

    private static async Task<int> StaffCountAsync(WebApplicationFactory<Program> host)
    {
        await using var c = await host.Services.GetRequiredService<Npgsql.NpgsqlDataSource>().OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>("SELECT count(*)::int FROM staff");
    }

    /// <summary>The PIN as a whole word of the line: not inside a migration number, a timestamp or a GUID.</summary>
    private static bool Mentions(LogLine line, string pin) => Regex.IsMatch(line.Message, $"(?<![0-9A-Za-z]){pin}(?![0-9A-Za-z])");

    private void AssertNoLineHas(params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(server.Logs.Lines, l => Mentions(l, secret));
        }
    }
}
