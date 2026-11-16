using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The hall as it stands (SHELL_CHANGES 94): a new PC takes a free cell (never one held by another PC), two PCs cannot be
/// put in one cell (<c>409 cellTaken</c>, for one PC and for a batch), a group is moved in one save
/// (<c>PATCH /admin/pcs/layout</c>, swaps allowed) with its rotation, and the club's plan (objects, zone areas) is stored
/// and shown on the hall and the map (<c>place</c> of every seat).
/// </summary>
public sealed class HallLayoutTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task A_new_pc_takes_a_free_cell_and_two_pcs_never_share_one()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var a = await TestAgent.CreateAsync(server);
        var b = await TestAgent.CreateAsync(server);
        var hall = await HallAsync(owner);
        foreach (var pc in new[] { a.PcId, b.PcId })
        {
            var (x, y) = Cell(hall, pc);
            Assert.DoesNotContain(hall, p => p.GetProperty("id").GetGuid() != pc && Cell(p) == (x, y));
        }

        // One PC onto another's cell, a new seat onto it: refused, naming the PC there.
        var (bx, by) = Cell(hall, b.PcId);
        var taken = await RawExpectAsync(server, 409, HttpMethod.Patch, $"/pcs/{a.PcId}", owner, new { x = bx, y = by });
        Assert.Equal(("cellTaken", b.PcId), Reason(taken));
        taken = await RawExpectAsync(server, 409, HttpMethod.Post, "/pcs", owner, new { zone = "VIP", number = 7101, x = bx, y = by }, Guid.NewGuid());
        Assert.Equal(("cellTaken", b.PcId), Reason(taken));

        // A seat without a cell takes a free one; its own cell is no obstacle to a PATCH.
        var seat = (await RawExpectAsync(server, 200, HttpMethod.Post, "/pcs", owner, new { zone = "VIP", number = 7102 }, Guid.NewGuid())).GetProperty("pc").GetProperty("id").GetGuid();
        hall = await HallAsync(owner);
        Assert.DoesNotContain(hall, p => p.GetProperty("id").GetGuid() != seat && Cell(p) == Cell(hall, seat));
        var (sx, sy) = Cell(hall, seat);
        await RawExpectAsync(server, 200, HttpMethod.Patch, $"/pcs/{seat}", owner, new { x = sx, y = sy, rotation = 90 });
        Assert.Equal(400, (await RawAsync(server, HttpMethod.Patch, $"/pcs/{seat}", owner, new { rotation = 45 })).Status);
        Assert.Equal(90, Find(await HallAsync(owner), seat).GetProperty("rotation").GetInt32());
    }

    [Fact]
    public async Task A_group_moves_in_one_save_and_a_batch_onto_a_taken_cell_changes_nothing()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var a = await TestAgent.CreateAsync(server);
        var b = await TestAgent.CreateAsync(server);
        var c = await TestAgent.CreateAsync(server);
        var hall = await HallAsync(owner);
        var (ax, ay) = Cell(hall, a.PcId);
        var (bx, by) = Cell(hall, b.PcId);
        var (cx, cy) = Cell(hall, c.PcId);

        // A swap within the batch is fine; the rotation comes along.
        await RawExpectAsync(server, 200, HttpMethod.Patch, "/pcs/layout", owner, new
        {
            items = new object[] { new { id = a.PcId, x = bx, y = by, rotation = 180 }, new { id = b.PcId, x = ax, y = ay } },
        });
        hall = await HallAsync(owner);
        Assert.Equal((bx, by), Cell(hall, a.PcId));
        Assert.Equal((ax, ay), Cell(hall, b.PcId));
        Assert.Equal(180, Find(hall, a.PcId).GetProperty("rotation").GetInt32());

        // Two of the batch in one cell; one onto a PC outside the batch: refused, nothing written.
        var twice = await RawExpectAsync(server, 409, HttpMethod.Patch, "/pcs/layout", owner, new
        {
            items = new object[] { new { id = a.PcId, x = 150, y = 150 }, new { id = b.PcId, x = 150, y = 150 } },
        });
        Assert.Equal(("cellTaken", a.PcId), Reason(twice));
        var outside = await RawExpectAsync(server, 409, HttpMethod.Patch, "/pcs/layout", owner, new
        {
            items = new object[] { new { id = a.PcId, x = 151, y = 151 }, new { id = b.PcId, x = cx, y = cy } },
        });
        Assert.Equal(("cellTaken", c.PcId), Reason(outside));
        Assert.Equal((bx, by), Cell(await HallAsync(owner), a.PcId));

        // Out of range, unknown PC, cashier.
        Assert.Equal(400, (await RawAsync(server, HttpMethod.Patch, "/pcs/layout", owner, new { items = new[] { new { id = a.PcId, x = 201, y = 0 } } })).Status);
        Assert.Equal(404, (await RawAsync(server, HttpMethod.Patch, "/pcs/layout", owner, new { items = new[] { new { id = Guid.NewGuid(), x = 160, y = 0 } } })).Status);
        var cashier = await LoginAsync(server, CashierPin);
        Assert.Equal(403, (await RawAsync(server, HttpMethod.Patch, "/pcs/layout", cashier, new { items = new[] { new { id = a.PcId, x = 160, y = 0 } } })).Status);
    }

    [Fact]
    public async Task The_plan_is_kept_and_the_map_shows_every_pc_at_its_place()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var plan = new
        {
            width = 24,
            height = 14,
            objects = new object[]
            {
                new { id = "bar", kind = "bar", x = 0, y = 12, w = 4, h = 2, text = "Бар" },
                new { id = "door", kind = "entrance", x = 10, y = 13, w = 2, h = 1 },
            },
            areas = new object[] { new { id = "vip", zone = "VIP", x = 0, y = 0, w = 6, h = 4 } },
        };
        Assert.Equal(400, (await RawAsync(server, HttpMethod.Put, "/pcs/plan", owner, new { width = 24, height = 14, objects = new[] { new { id = "x", kind = "sofa", x = 0, y = 0, w = 1, h = 1 } } })).Status);
        Assert.Equal(400, (await RawAsync(server, HttpMethod.Put, "/pcs/plan", owner, new { width = 0, height = 14 })).Status);
        Assert.Equal(403, (await RawAsync(server, HttpMethod.Put, "/pcs/plan", await LoginAsync(server, CashierPin), plan)).Status);
        await RawExpectAsync(server, 200, HttpMethod.Put, "/pcs/plan", owner, plan);

        var list = await RawExpectAsync(server, 200, HttpMethod.Get, "/pcs", owner);
        Assert.Equal(24, list.GetProperty("plan").GetProperty("width").GetInt32());
        Assert.Equal("Бар", list.GetProperty("plan").GetProperty("objects")[0].GetProperty("text").GetString());
        Assert.Equal("VIP", list.GetProperty("plan").GetProperty("areas")[0].GetProperty("zone").GetString());

        var overview = await RawExpectAsync(server, 200, HttpMethod.Get, "/overview", owner);
        Assert.Equal(14, overview.GetProperty("hallPlan").GetProperty("height").GetInt32());
        var hall = list.GetProperty("items").EnumerateArray().ToList();
        foreach (var seat in overview.GetProperty("seats").EnumerateArray())
        {
            var place = seat.GetProperty("place");
            var pc = Find(hall, seat.GetProperty("pc").GetProperty("id").GetGuid());
            Assert.Equal(Cell(pc), (place.GetProperty("x").GetInt32(), place.GetProperty("y").GetInt32()));
            Assert.Equal(pc.GetProperty("rotation").GetInt32(), place.GetProperty("rotation").GetInt32());
        }

        await RawExpectAsync(server, 200, HttpMethod.Delete, "/pcs/plan", owner);
        Assert.Equal(JsonValueKind.Null, (await RawExpectAsync(server, 200, HttpMethod.Get, "/pcs", owner)).GetProperty("plan").ValueKind);
    }

    private async Task<List<JsonElement>> HallAsync(string owner) =>
        (await ExpectAsync(server, 200, HttpMethod.Get, "/pcs", owner)).GetProperty("items").EnumerateArray().ToList();

    private static JsonElement Find(List<JsonElement> hall, Guid id) => hall.Single(p => p.GetProperty("id").GetGuid() == id);

    private static (int X, int Y) Cell(JsonElement pc) => (pc.GetProperty("x").GetInt32(), pc.GetProperty("y").GetInt32());

    private static (int X, int Y) Cell(List<JsonElement> hall, Guid id) => Cell(Find(hall, id));

    private static (string? Reason, Guid PcId) Reason(JsonElement error)
    {
        var details = error.GetProperty("error").GetProperty("details");
        return (details.GetProperty("reason").GetString(), details.GetProperty("pcId").GetGuid());
    }
}
