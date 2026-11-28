using System.Text.Json;
using ClubShell.Server.Admin;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The birthday gift (D-107, <c>settings.birthdayGift</c>): off with an example amount until the owner turns it on; then a
/// member whose birthday is within the window gets the amount on the bonus account (D-106) and a message on the PC, at a
/// session opened or by the club minute for one already running — once per birthday year, across restarts.
/// </summary>
public sealed class BirthdayGiftTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task The_gift_is_an_example_until_the_owner_turns_it_on_and_is_checked()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var gift = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("birthdayGift");
        Assert.Equal((false, 2_000_000L, 3, 0), (gift.GetProperty("enabled").GetBoolean(), gift.GetProperty("amount").GetInt64(),
            gift.GetProperty("windowDays").GetInt32(), gift.GetProperty("fired").GetInt32()));
        foreach (var (body, field, reason) in new (object, string, string)[]
                 {
                     (new { enabled = true, amount = -1 }, "birthdayGift.amount", "min"),
                     (new { enabled = true, amount = 100, windowDays = 31 }, "birthdayGift.windowDays", "max"),
                     (new { enabled = "yes" }, "birthdayGift.enabled", "format"),
                 })
        {
            Assert.Equal((field, reason), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner, new { birthdayGift = body })));
        }

        Assert.Equal(("limits.minorCurfewEnd", "format"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner,
            new { limits = new { minorAge = 18, minorCurfew = "22:00", minorCurfewEnd = "6am" } })));
        // The agents enforce the curfew offline from their config (offline.minorCurfew): a change reaches them.
        var version = await Players.ScalarAsync<int>(Server, "SELECT config_version FROM clubs");
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 16, minorCurfew = "23:00", minorCurfewEnd = "07:00", minorCurfewEnabled = false } });
        Assert.Equal(version + 1, await Players.ScalarAsync<int>(Server, "SELECT config_version FROM clubs"));
        var limits = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("limits");
        Assert.Equal((16, "23:00", "07:00", false), (limits.GetProperty("minorAge").GetInt32(), limits.GetProperty("minorCurfew").GetString(),
            limits.GetProperty("minorCurfewEnd").GetString(), limits.GetProperty("minorCurfewEnabled").GetBoolean()));
        await Players.ExecuteAsync(Server, "UPDATE clubs SET settings = settings - 'limits'");
        var defaults = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("limits");
        Assert.Equal(("22:00", "06:00", true), (defaults.GetProperty("minorCurfew").GetString(), defaults.GetProperty("minorCurfewEnd").GetString(),
            defaults.GetProperty("minorCurfewEnabled").GetBoolean()));
    }

    [Fact]
    public async Task A_birthday_gives_the_bonus_once_a_year_at_a_session_or_by_the_club_minute()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        await OpenShiftAsync(Server, owner);
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { birthdayGift = new { enabled = true, amount = 3_000_000, windowDays = 0, text = "" } });
        try
        {
            var today = DateOnly.FromDateTime(Infrastructure.ClubTime.Local(Server.Clock.GetUtcNow(), "Asia/Tashkent"));
            var agent = await TestAgent.CreateAsync(Server);
            var player = await Players.CreateAsync(Server, birthDate: Format(today.AddYears(-25)));

            // A session opened on the birthday: the bonus account, the message on the PC.
            await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 30 });
            Assert.Equal(3_000_000, await Players.BonusAsync(Server, player.Id));
            Assert.Equal("bonus/bonus/Подарок на день рождения", await Players.ScalarAsync<string>(Server,
                "SELECT type || '/' || account || '/' || description FROM ledger_entries WHERE user_id = @Id AND type = 'bonus'", new { player.Id }));
            var message = JsonElement.Parse(await Players.ScalarAsync<string>(Server,
                "SELECT payload::text FROM agent_commands WHERE pc_id = @PcId AND name = 'message'", new { agent.PcId }));
            Assert.Equal("С днём рождения! +30 000 бонусов", message.GetProperty("text").GetString());

            // Again the same day, the club minute, a fresh worker (a restart): nothing more.
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId });
            await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = agent.PcId, userId = player.Id, tariffId = Players.Standard, minutes = 30 });
            await Server.Services.GetRequiredService<ClubTickWorker>().RunOnceAsync();
            await ActivatorUtilities.CreateInstance<ClubTickWorker>(Server.Services, ActivatorUtilities.CreateInstance<AutomationService>(Server.Services)).RunOnceAsync();
            Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM ledger_entries WHERE user_id = @Id AND type = 'bonus'", new { player.Id }));
            Assert.Equal(1, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM rule_firings WHERE rule_id = 'birthdayGift' AND target_key = @key",
                new { key = $"birthday:{player.Id}:{today.Year}" }));
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = agent.PcId });

            // Two days ago is outside a window of 0; with ±3 days the club minute gives it to the session already running.
            var other = await TestAgent.CreateAsync(Server);
            var late = await Players.CreateAsync(Server, birthDate: Format(today.AddDays(-2).AddYears(-30)));
            await ExpectAsync(Server, 201, HttpMethod.Post, "/sessions", owner, new { pcId = other.PcId, userId = late.Id, tariffId = Players.Standard, minutes = 30 });
            Assert.Equal(0, await Players.BonusAsync(Server, late.Id));
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { birthdayGift = new { enabled = true, amount = 1_000_000, windowDays = 3, text = "С праздником! +{bonus}" } });
            await Server.Services.GetRequiredService<ClubTickWorker>().RunOnceAsync();
            Assert.Equal(1_000_000, await Players.BonusAsync(Server, late.Id));
            Assert.Equal("С праздником! +10 000", JsonElement.Parse(await Players.ScalarAsync<string>(Server,
                "SELECT payload::text FROM agent_commands WHERE pc_id = @PcId AND name = 'message'", new { other.PcId })).GetProperty("text").GetString());
            await ExpectAsync(Server, 200, HttpMethod.Post, "/sessions/end", owner, new { pcId = other.PcId });

            // The console counts what was given this year.
            var gift = (await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("birthdayGift");
            Assert.Equal((true, 2), (gift.GetProperty("enabled").GetBoolean(), gift.GetProperty("fired").GetInt32()));
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { birthdayGift = new { enabled = false } });
        }
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
