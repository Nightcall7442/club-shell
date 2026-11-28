using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Users;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// Registration without the desk's strict form (D-92): the desk needs only the name and the server makes a unique Latin
/// login (Cyrillic transliterated, collisions numbered, a phone as its digits); a player creates their own account on a
/// PC (<c>POST /auth/register</c>) only where the owner turned <c>limits.selfSignup</c> on, is signed in like a password
/// login, starts as a member with zero balances, and one PC makes at most five accounts an hour.
/// </summary>
public sealed class SelfSignupTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    private const string Register = "/api/v1/auth/register";

    [Theory]
    [InlineData("Иван Петров", "ivan.petrov")]
    [InlineData("  Алексей  ", "aleksey")]
    [InlineData("Щукин Юрий Ёлкин", "shchukin.yuriy")]
    [InlineData("Ўткир Қодиров", "otkir.qodirov")]
    [InlineData("Gʻayrat Oʻtkirov", "gayrat.otkirov")]
    [InlineData("José Müller", "jose.muller")]
    [InlineData("李小龙", "player")]
    [InlineData("Константинопольский Александр", "konstantinopolskiy.aleks")]
    public void A_name_becomes_a_readable_latin_login(string name, string slug) => Assert.Equal(slug, Logins.Slug(name));

    [Fact]
    public async Task The_desk_registers_by_name_alone_and_gets_a_unique_login()
    {
        var cashier = await LoginAsync(Server, CashierPin);

        // No username at all, null, or blanks: generated from the name, numbered on a collision.
        var first = await AddAsync(cashier, new { displayName = "Турсунов Бобур" });
        var second = await AddAsync(cashier, new { displayName = "Турсунов Бобур", username = (string?)null });
        var third = await AddAsync(cashier, new { displayName = "турсунов  бобур", username = "   " });
        Assert.Equal(["tursunov.bobur", "tursunov.bobur2", "tursunov.bobur3"], new[] { first, second, third }.Select(c => c.GetProperty("username").GetString()));
        Assert.Equal(("member", 0L), (first.GetProperty("role").GetString(), first.GetProperty("balance").GetProperty("amount").GetInt64()));

        // A phone given: its digits, while free; then the name.
        var phone = $"+998 91 {Random.Shared.Next(100, 999)}-{Random.Shared.Next(10, 99)}-{Random.Shared.Next(10, 99)}";
        var digits = string.Concat(phone.Where(char.IsAsciiDigit));
        var byPhone = await AddAsync(cashier, new { displayName = "Ли", phone, password = Players.Password });
        Assert.Equal((digits, phone), (byPhone.GetProperty("username").GetString(), byPhone.GetProperty("phone").GetString()));
        var again = await AddAsync(cashier, new { displayName = "Ли", phone });
        Assert.StartsWith("li", again.GetProperty("username").GetString());
        Assert.Matches("^li[0-9]+$", again.GetProperty("username").GetString());

        // The player signs in with the phone however it is typed.
        var agent = await TestAgent.CreateAsync(Server);
        var typed = $"+{digits[..3]} ({digits[3..5]}) {digits[5..8]} {digits[8..]}";
        Assert.Equal(byPhone.GetProperty("id").GetGuid(),
            (await agent.LoginAsync(new TestPlayer(byPhone.GetProperty("id").GetGuid(), typed))).GetProperty("user").GetProperty("id").GetGuid());

        // A typed login keeps its rules.
        foreach (var (username, reason) in new[] { ("ab", "min"), (new string('x', 33), "max"), ("tursunov.bobur", "taken") })
        {
            Assert.Equal(("username", reason), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Post, "/clients", cashier,
                new { gender = "male", birthDate = Players.AdultBirthDate, username, displayName = "Кто-то" })));
        }
    }

    [Fact]
    public async Task Self_signup_is_off_until_the_owner_turns_it_on_and_reaches_the_pc()
    {
        await SetAsync(selfSignup: null, rules: null);
        var owner = await LoginAsync(Server, OwnerPin);
        var agent = await TestAgent.CreateAsync(Server);
        Assert.False((await ExpectAsync(Server, 200, HttpMethod.Get, "/club", owner)).GetProperty("limits").GetProperty("selfSignup").GetBoolean());
        Assert.False(await ShellFlagAsync(agent));
        Assert.Equal("selfSignupDisabled", Reason(Expect(await agent.RawPostAsync(Register, Body(agent, "Нельзя")), 403)));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM users WHERE display_name = 'Нельзя'"));

        Assert.Equal(("limits.selfSignup", "format"), StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner,
            new { limits = new { minorAge = 18, minorCurfew = "22:00", selfSignup = "yes" } })));
        var version = await Players.ScalarAsync<int>(Server, "SELECT config_version FROM clubs");
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", selfSignup = true } });
        Assert.Equal(version + 1, await Players.ScalarAsync<int>(Server, "SELECT config_version FROM clubs"));
        Assert.True(await ShellFlagAsync(agent));

        // Another limit changed, the switch kept: the PCs' config does not move (the curfew's limits would, D-108).
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00", selfSignup = true, guestPostpaid = false } });
        Assert.Equal(version + 1, await Players.ScalarAsync<int>(Server, "SELECT config_version FROM clubs"));
        await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { limits = new { minorAge = 18, minorCurfew = "22:00" } });
        Assert.Equal(version + 2, await Players.ScalarAsync<int>(Server, "SELECT config_version FROM clubs"));
        Assert.False(await ShellFlagAsync(agent));
    }

    [Fact]
    public async Task A_player_registers_on_the_pc_and_is_signed_in_as_a_plain_member()
    {
        await SetAsync(selfSignup: true, rules: null);
        var agent = await TestAgent.CreateAsync(Server);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var auth = Expect(await agent.RawPostAsync(Register, Body(agent, "Самир", $"Samir-{tag}")), 200);
        var user = auth.GetProperty("user");
        Assert.Equal(($"samir-{tag}", "Самир", "member", 0L), (user.GetProperty("username").GetString(), user.GetProperty("displayName").GetString(),
            user.GetProperty("role").GetString(), user.GetProperty("balance").GetProperty("amount").GetInt64()));
        Assert.Equal("0/0", await Players.ScalarAsync<string>(Server,
            "SELECT main_balance || '/' || bonus_balance FROM wallets WHERE user_id = @id", new { id = user.GetProperty("id").GetGuid() }));
        Contract.AssertMatches("AuthResponse", auth);
        Assert.False(string.IsNullOrEmpty(auth.GetProperty("accessToken").GetString()));
        Assert.StartsWith("pbkdf2$", auth.GetProperty("offlineHash").GetString());
        var id = user.GetProperty("id").GetGuid();
        Assert.Equal(agent.PcId, await Players.ScalarAsync<Guid>(Server, "SELECT signup_pc_id FROM users WHERE id = @id", new { id }));
        Assert.Equal(1, await Players.ScalarAsync<int>(Server,
            "SELECT count(*)::int FROM audit_entries WHERE action = 'clientSelfSignup' AND user_id = @id AND pc_id = @PcId AND staff_id IS NULL", new { id, agent.PcId }));

        Assert.Equal("female", await Players.ScalarAsync<string>(Server, "SELECT gender FROM users WHERE id = @id", new { id }));
        Assert.Equal(Players.AdultBirthDate, await Players.ScalarAsync<string>(Server, "SELECT to_char(birth_date, 'YYYY-MM-DD') FROM users WHERE id = @id", new { id }));

        // The token is a player's: the account is theirs on this PC; the password signs in again on another PC.
        agent.UserToken = auth.GetProperty("accessToken").GetString();
        using (var me = await agent.SendAsync(HttpMethod.Get, $"/api/v1/users/{id}"))
        {
            Assert.Equal(id, (await Players.ReadAsync(me, 200)).GetProperty("id").GetGuid());
        }

        var other = await TestAgent.CreateAsync(Server);
        await other.LoginAsync(new TestPlayer(id, $"SAMIR-{tag}"));

        // Refusals: the fields, a taken login, a phone as the login.
        foreach (var (body, field, reason) in new (object, string, string)[]
                 {
                     (Body(agent, " ", $"x-{tag}"), "displayName", "required"),
                     (Body(agent, "Кто", "ab"), "username", "min"),
                     (Body(agent, "Кто", $"samir-{tag}"), "username", "taken"),
                     (Body(agent, "Кто", $"y-{tag}", password: "abc"), "password", "min"),
                     (Body(agent, "Кто", $"y-{tag}", password: ""), "password", "required"),
                     // D-104: the gender is required, male or female.
                     (Body(agent, "Кто", $"y-{tag}", gender: null), "gender", "required"),
                     (Body(agent, "Кто", $"y-{tag}", gender: "other"), "gender", "enum"),
                     // D-107: the date of birth too, an age of 5–100 on the club's date.
                     (Body(agent, "Кто", $"y-{tag}", birthDate: null), "birthDate", "required"),
                     (Body(agent, "Кто", $"y-{tag}", birthDate: "17.05.1990"), "birthDate", "format"),
                     (Body(agent, "Кто", $"y-{tag}", birthDate: "2999-01-01"), "birthDate", "future"),
                     (Body(agent, "Кто", $"y-{tag}", birthDate: Server.Clock.GetUtcNow().AddYears(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), "birthDate", "min"),
                     (Body(agent, "Кто", $"y-{tag}", birthDate: "1900-01-01"), "birthDate", "max"),
                 })
        {
            Assert.Equal((field, reason), Details(Expect(await agent.RawPostAsync(Register, body), 400)));
        }

        var digits = "99890" + Random.Shared.Next(1_000_000, 9_999_999);
        var byPhone = Expect(await agent.RawPostAsync(Register, Body(agent, "Нодира", $"+{digits[..3]} {digits[3..5]} {digits[5..]}")), 200).GetProperty("user");
        Assert.Equal(digits, byPhone.GetProperty("username").GetString());
        Assert.Equal(digits, string.Concat((await Players.ScalarAsync<string>(Server,
            "SELECT phone FROM client_profiles WHERE user_id = @id", new { id = byPhone.GetProperty("id").GetGuid() })).Where(char.IsAsciiDigit)));

        // No login typed (a future app): one is made from the name.
        Assert.Matches("^aziz[0-9]*$", Expect(await agent.RawPostAsync(Register, Body(agent, "Азиз", null)), 200).GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task The_club_rules_are_accepted_first_and_a_pc_makes_five_accounts_an_hour()
    {
        await SetAsync(selfSignup: true, rules: "Не есть у ПК");
        var agent = await TestAgent.CreateAsync(Server);
        var tag = Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(("acceptRules", "required"), Details(Expect(await agent.RawPostAsync(Register, Body(agent, "Правила", $"r0-{tag}", acceptRules: false)), 400)));
        for (var i = 1; i <= SelfSignup.MaxPerPc; i++)
        {
            Expect(await agent.RawPostAsync(Register, Body(agent, "Игрок", $"r{i}-{tag}")), 200);
        }

        var limited = Expect(await agent.RawPostAsync(Register, Body(agent, "Игрок", $"r9-{tag}")), 429);
        Assert.Equal("signupLimit", Reason(limited));
        Assert.InRange(limited.GetProperty("error").GetProperty("details").GetProperty("retryAfterSec").GetInt32(), 1, 3600);

        // Another PC is not held back; an hour later this one may again.
        var next = await TestAgent.CreateAsync(Server);
        Expect(await next.RawPostAsync(Register, Body(next, "Игрок", $"n1-{tag}")), 200);
        await Players.ExecuteAsync(Server, "UPDATE users SET created_at = created_at - interval '61 minutes' WHERE signup_pc_id = @PcId", new { agent.PcId });
        Expect(await agent.RawPostAsync(Register, Body(agent, "Игрок", $"r9-{tag}")), 200);
        await SetAsync(selfSignup: null, rules: null);
    }

    private static object Body(
        TestAgent agent, string name, string? username = "auto", string password = Players.Password, bool acceptRules = true, string? gender = "female",
        string? birthDate = Players.AdultBirthDate) => new
    {
        pcId = agent.PcId, hwid = agent.Hwid, displayName = name,
        username = username == "auto" ? $"u-{Guid.NewGuid():N}"[..20] : username,
        password, acceptRules, gender, birthDate,
    };

    private async Task<JsonElement> AddAsync(string cashier, object body) =>
        (await ExpectAsync(Server, 200, HttpMethod.Post, "/clients", cashier, Players.WithGender(body))).GetProperty("client");

    /// <summary><c>limits.selfSignup</c> and the Russian rules straight in the settings (the console's save is tested above).</summary>
    private Task SetAsync(bool? selfSignup, string? rules) => Players.ExecuteAsync(Server,
        """
        UPDATE clubs SET settings = settings
            || jsonb_build_object('limits', (coalesce(settings -> 'limits', '{"minorAge": 18, "minorCurfew": "22:00"}') - 'selfSignup')
                || CASE WHEN @selfSignup::boolean IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('selfSignup', @selfSignup::boolean) END)
            || jsonb_build_object('rulesText', jsonb_build_object('ru', coalesce(@rules::text, ''), 'uz', '', 'en', ''))
        """,
        new { selfSignup, rules });

    private static async Task<bool> ShellFlagAsync(TestAgent agent) =>
        (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("config")), 200))
            .GetProperty("shell").GetProperty("club").GetProperty("selfSignup").GetBoolean();

    private static JsonElement Expect((int Status, JsonElement Body) answer, int status)
    {
        Assert.True(answer.Status == status, $"expected {status}, got {answer.Status}: {answer.Body}");
        return answer.Body;
    }

    private static string? Reason(JsonElement error) => error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString();

    private static (string?, string?) Details(JsonElement error)
    {
        var details = error.GetProperty("error").GetProperty("details");
        return (details.GetProperty("field").GetString(), details.GetProperty("reason").GetString());
    }
}
