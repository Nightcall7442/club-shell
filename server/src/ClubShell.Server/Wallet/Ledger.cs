using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Wallet;

/// <summary>
/// One ledger row to post: signed <paramref name="Amount"/> (tiyin), what it is for and the links reports read.
/// <paramref name="Meta"/> carries the price quote of a charge (DESIGN §5.1). <paramref name="ShiftRequired"/>: money the
/// cashier takes, which must land in the open shift's X/Z (else <c>409 shiftClosed</c>, the whole operation refused).
/// <paramref name="Ref"/>: <c>Transaction.ref</c> when the row is not a session's (a bar sale or its void, D-52); a session's
/// row refers to its session.
/// </summary>
public sealed record LedgerLine(
    string Type,
    long Amount,
    string Description,
    Guid? ClubId = null,
    Guid? SessionId = null,
    Guid? PcId = null,
    string? Method = null,
    Guid? StaffId = null,
    object? Meta = null,
    Guid? Id = null,
    bool ShiftRequired = false,
    string? Ref = null);

/// <summary>
/// The only writer of money (DESIGN §4.3): inside the caller's transaction it locks the wallet row
/// (<c>SELECT … FOR UPDATE</c>, after any session row: lock order §4.4), checks funds, appends the rows with one
/// <c>op_id</c> and the running <c>balance_after</c>, and updates the cached balance, <c>version</c> and
/// <c>lifetime_spent</c> (+|charge|, +|purchase|, −refund). A debit below zero is <c>402 insufficientFunds
/// {required, available}</c> unless <paramref name="allowOverdraft"/> (postpaid settlement, offline replay), which marks the
/// row <c>overdraft</c>. Every row of a club gets its open shift (<c>shift_id</c>, taken <c>FOR SHARE</c> after the wallet —
/// lock order §4.4; closing a shift takes it <c>FOR UPDATE</c>, so no row joins a shift after its Z report was summed). A
/// row racing the close waits for it, no longer finds an open shift and gets <c>shift_id NULL</c>, like a row posted with
/// no shift open: the S5 <c>noShift</c> flag must count those — except a line marked <c>ShiftRequired</c> (a counter top-up),
/// which is refused instead. Pushes go out after the commit, never from here.
/// </summary>
public static class Ledger
{
    public static async Task<long> PostAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, bool allowOverdraft, DateTimeOffset now, params LedgerLine[] lines)
    {
        var wallet = await c.QuerySingleOrDefaultAsync<(long Balance, Guid NetworkId)>(
            "SELECT main_balance, network_id FROM wallets WHERE user_id = @userId FOR UPDATE", new { userId }, tx);
        if (wallet.NetworkId == Guid.Empty)
        {
            throw ApiException.NotFound("user");
        }

        var clubId = lines.FirstOrDefault(l => l.ClubId is not null)?.ClubId;
        var shiftId = clubId is null ? null : await c.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM shifts WHERE club_id = @clubId AND closed_at IS NULL FOR SHARE", new { clubId }, tx);
        if (shiftId is null && lines.Any(l => l.ShiftRequired))
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: shiftClosed", new { reason = "shiftClosed" });
        }

        var balance = wallet.Balance;
        var spent = 0L;
        var opId = Guid.CreateVersion7(now);
        foreach (var line in lines.Where(l => l.Amount != 0))
        {
            if (line.Amount < 0 && balance + line.Amount < 0 && !allowOverdraft)
            {
                throw new ApiException(StatusCodes.Status402PaymentRequired, ErrorCode.InsufficientFunds, "Balance too low",
                    new { required = Money.Uzs(-line.Amount), available = Money.Uzs(balance) });
            }

            balance += line.Amount;
            spent += line.Type is "charge" or "purchase" or "refund" ? -line.Amount : 0;
            await c.ExecuteAsync(
                """
                INSERT INTO ledger_entries (id, op_id, user_id, network_id, club_id, type, amount, balance_after, method, description,
                                            ref, session_id, shift_id, staff_id, pc_id, overdraft, meta, created_at)
                VALUES (@id, @opId, @userId, @networkId, @clubId, @type, @amount, @balance, @method, @description,
                        @reference, @sessionId, @shiftId, @staffId, @pcId, @overdraft, @meta::jsonb, @now)
                """,
                new
                {
                    id = line.Id ?? Guid.CreateVersion7(now), opId, userId, networkId = wallet.NetworkId, clubId = line.ClubId, type = line.Type,
                    shiftId = line.ClubId is null ? null : shiftId,
                    amount = line.Amount, balance, method = line.Method, description = line.Description,
                    reference = line.Ref ?? line.SessionId?.ToString(), sessionId = line.SessionId, staffId = line.StaffId, pcId = line.PcId,
                    overdraft = balance < 0 && line.Amount < 0,
                    meta = line.Meta is null ? null : JsonSerializer.Serialize(line.Meta, ServerJson.Options), now,
                },
                tx);
        }

        await c.ExecuteAsync(
            """
            UPDATE wallets SET main_balance = @balance, lifetime_spent = greatest(0, lifetime_spent + @spent),
                               version = version + 1, updated_at = @now
            WHERE user_id = @userId
            """,
            new { userId, balance, spent, now },
            tx);
        return balance;
    }

    /// <summary>The wire <c>Balance</c> of a user (bonus is always 0 in v1, D-9).</summary>
    public static async Task<Balance?> BalanceAsync(NpgsqlConnection c, Guid userId, NpgsqlTransaction? tx = null)
    {
        var w = await c.QuerySingleOrDefaultAsync<WalletRow>(
            "SELECT main_balance, bonus_balance, updated_at FROM wallets WHERE user_id = @userId", new { userId }, tx);
        return w is null ? null : new Balance(userId, Money.Uzs(w.MainBalance), Money.Uzs(w.BonusBalance), Money.DefaultCurrency, w.UpdatedAt);
    }

    private sealed class WalletRow
    {
        public long MainBalance { get; init; }
        public long BonusBalance { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }
}

/// <summary>A <c>ledger_entries</c> row as the wire <c>Transaction</c> (the player's history and the client card at the counter).</summary>
public sealed class TransactionRow
{
    public const string Columns = "id, user_id, type, amount, balance_after, description, created_at, ref";

    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public string Type { get; init; } = "";
    public long Amount { get; init; }
    public long BalanceAfter { get; init; }
    public string Description { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string? Ref { get; init; }

    public Transaction ToWire() => new(
        Id, UserId, Enum.Parse<TransactionType>(Type, ignoreCase: true), Money.Uzs(Amount), Money.Uzs(BalanceAfter), Description, CreatedAt, Ref);
}
