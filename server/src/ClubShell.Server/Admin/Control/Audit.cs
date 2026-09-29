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
/// </summary>
public static class Audit
{
    public static async Task WriteAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, DateTimeOffset at, string action,
        Guid? userId = null, Guid? pcId = null, long amount = 0, string detail = "", object? meta = null, Guid? shiftId = null)
    {
        await c.ExecuteAsync(
            """
            INSERT INTO audit_entries (id, club_id, at, staff_id, staff_name, shift_id, action, user_id, pc_id, amount, detail, meta)
            VALUES (@id, @ClubId, @at, @StaffId, @Name, coalesce(@shiftId, (SELECT id FROM shifts WHERE club_id = @ClubId AND closed_at IS NULL)),
                    @action, @userId, @pcId, @amount, @detail, @meta::jsonb)
            """,
            new
            {
                id = Guid.CreateVersion7(at), staff.ClubId, at, staff.StaffId, staff.Name, shiftId, action, userId, pcId, amount, detail,
                meta = JsonSerializer.Serialize(meta ?? new { }, ServerJson.Options),
            },
            tx);
    }
}
