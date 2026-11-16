using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// PC service sign-in (beyond the contract, SERVER_API.md §4.1): <c>POST /agents/{pcId}/staff/verify</c> checks a staff
/// member's PIN typed on a club PC before the Agent lets them into their own Windows account — an owner always, a cashier
/// only with the owner's <c>pcService</c> mark; the sign-in is in the staff journal; wrong PINs are limited per PC like the
/// cash desk's and do not lock out the desks.
/// </summary>
public sealed class StaffPcServiceTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task An_owner_signs_in_on_a_pc_and_the_journal_says_who_where()
    {
        var agent = await TestAgent.CreateAsync(server);
        var (status, body) = await VerifyAsync(agent, OwnerPin);
        Assert.True(status == 200, $"expected 200, got {status}: {body}");
        Assert.Equal("owner", body.GetProperty("role").GetString());
        var staffId = body.GetProperty("staffId").GetGuid();
        Assert.Equal(staffId.ToString(), (await ExpectAsync(server, 200, HttpMethod.Get, "/me", await LoginAsync(server, OwnerPin))).GetProperty("staff").GetProperty("id").GetString());

        var entry = await Players.ScalarAsync<string>(server,
            "SELECT action || '|' || detail || '|' || (meta ->> 'kind') FROM audit_entries WHERE pc_id = @PcId AND staff_id = @staffId",
            new { agent.PcId, staffId });
        Assert.EndsWith("· serviceSignIn|serviceSignIn", entry);
        Assert.StartsWith("pcCommand|", entry);
    }

    [Fact]
    public async Task A_cashier_needs_the_owners_mark_which_the_staff_list_and_me_show()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var agent = await TestAgent.CreateAsync(server);
        var cashierId = (await ExpectAsync(server, 200, HttpMethod.Post, "/staff", owner, new { name = "Техник Ильхом", role = "cashier", pin = "2468" }))
            .GetProperty("id").GetString()!;

        var (refused, refusedBody) = await VerifyAsync(agent, "2468");
        Assert.Equal((403, "forbidden", "pcService"), (refused, Code(refusedBody), Reason(refusedBody)));
        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM audit_entries WHERE detail LIKE '%serviceSignIn' AND staff_id = @id",
            new { id = Guid.Parse(cashierId) }));
        Assert.False(PcService(await StaffAsync(owner, cashierId)));
        Assert.True(PcService(await StaffAsync(owner, (await ExpectAsync(server, 200, HttpMethod.Get, "/me", owner)).GetProperty("staff").GetProperty("id").GetString()!)));

        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{cashierId}", owner, new { pcService = true });
        Assert.True(PcService(await StaffAsync(owner, cashierId)));
        Assert.True((await ExpectAsync(server, 200, HttpMethod.Get, "/me", await LoginAsync(server, "2468"))).GetProperty("staff").GetProperty("pcService").GetBoolean());
        var (allowed, allowedBody) = await VerifyAsync(agent, "2468");
        Assert.Equal((200, "cashier", "Техник Ильхом"), (allowed, allowedBody.GetProperty("role").GetString(), allowedBody.GetProperty("name").GetString()));

        // Other fields leave the mark alone; taking it away refuses again.
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{cashierId}", owner, new { name = "Техник Ильхом Р." });
        Assert.True(PcService(await StaffAsync(owner, cashierId)));
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{cashierId}", owner, new { pcService = false });
        Assert.Equal(403, (await VerifyAsync(agent, "2468")).Status);

        // Created with the mark; a disabled member is a wrong PIN.
        var markedId = (await ExpectAsync(server, 200, HttpMethod.Post, "/staff", owner, new { name = "Техник Сардор", role = "cashier", pin = "13579", pcService = true }))
            .GetProperty("id").GetString()!;
        Assert.True(PcService(await StaffAsync(owner, markedId)));
        Assert.Equal(200, (await VerifyAsync(agent, "13579")).Status);
        await ExpectAsync(server, 200, HttpMethod.Patch, $"/staff/{markedId}", owner, new { active = false });
        var (disabled, disabledBody) = await VerifyAsync(agent, "13579");
        Assert.Equal((401, "invalidPin"), (disabled, Reason(disabledBody)));
    }

    [Fact]
    public async Task Wrong_pins_are_limited_per_pc_and_leave_the_desks_and_other_pcs_open()
    {
        var agent = await TestAgent.CreateAsync(server);
        var other = await TestAgent.CreateAsync(server);

        var (format, formatBody) = await VerifyAsync(agent, "12a4");
        Assert.Equal((400, "pin", "digits4to8"), (format, Details(formatBody).GetProperty("field").GetString(), Reason(formatBody)));

        for (var i = 0; i < 5; i++)
        {
            var (wrong, wrongBody) = await VerifyAsync(agent, "8642");
            Assert.Equal((401, "unauthorized", "invalidPin"), (wrong, Code(wrongBody), Reason(wrongBody)));
        }

        // Past the limit even the right PIN waits; a desk and another PC are not this PC.
        var (limited, limitedBody) = await VerifyAsync(agent, OwnerPin);
        Assert.Equal((429, "rateLimited"), (limited, Code(limitedBody)));
        Assert.True(Details(limitedBody).GetProperty("retryAfterSec").GetInt32() > 0);
        Assert.Equal(200, (await VerifyAsync(other, OwnerPin)).Status);
        await LoginAsync(server, OwnerPin);
    }

    private static Task<(int Status, JsonElement Body)> VerifyAsync(TestAgent agent, string pin) =>
        agent.RawPostAsync(agent.Path("staff/verify"), new { pin });

    private async Task<JsonElement> StaffAsync(string owner, string id) =>
        (await ExpectAsync(server, 200, HttpMethod.Get, "/staff", owner)).GetProperty("items").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);

    private static bool PcService(JsonElement member) => member.GetProperty("pcService").GetBoolean();

    private static JsonElement Details(JsonElement body) => body.GetProperty("error").GetProperty("details");

    private static string? Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString();

    private static string? Reason(JsonElement body) => Details(body).GetProperty("reason").GetString();
}
