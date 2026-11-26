using System.Text.Json;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The staff journal (DESIGN §3.7, <c>/admin/control</c> reads it in S5): one append-only row per cashier action, written
/// after validation and in the action's own transaction, so a refused or rolled-back action leaves no entry (the mock
/// writes before validating). The row carries the open shift unless <c>shiftId</c> names one (the shift just closed).
/// <c>ownerOp</c> (D-101): the owner's own money operation — the entry joins no shift (only <c>shiftId</c> when given) and is
/// marked <c>owner_op</c>, so no cashier's feed shows it. The owner's other entries join the open shift as anyone's.
/// </summary>
public static class Audit
{
    public static async Task WriteAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, DateTimeOffset at, string action,
        Guid? userId = null, Guid? pcId = null, long amount = 0, string detail = "", object? meta = null, Guid? shiftId = null, bool ownerOp = false)
    {
        var owner = ownerOp;
        await c.ExecuteAsync(
            $"""
            INSERT INTO audit_entries (id, club_id, at, staff_id, staff_name, shift_id, action, user_id, pc_id, amount, detail, meta, owner_op)
            VALUES (@id, @ClubId, @at, @StaffId, @Name,
                    {(owner ? "@shiftId" : "coalesce(@shiftId, (SELECT id FROM shifts WHERE club_id = @ClubId AND closed_at IS NULL))")},
                    @action, @userId, @pcId, @amount, @detail, @meta::jsonb, @owner)
            """,
            new
            {
                id = Guid.CreateVersion7(at), staff.ClubId, at, staff.StaffId, staff.Name, shiftId, action, userId, pcId, amount, detail, owner,
                meta = JsonSerializer.Serialize(meta ?? new { }, ServerJson.Options),
            },
            tx);
    }
}
