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
/// row refers to its session. <paramref name="OwnerOp"/> (D-101): the owner's own operation — the rows join no shift (not even
/// an open one), <c>ShiftRequired</c> does not apply and they are marked <c>owner_op</c>. <paramref name="Account"/> (D-106):
/// the account the line moves, <see cref="Ledger.Main"/> or <see cref="Ledger.Bonus"/>; null — by its type (see
/// <see cref="Ledger"/>).
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
    string? Ref = null,
    bool OwnerOp = false,
    string? Account = null);

/// <summary>
/// The only writer of money (DESIGN §4.3): inside the caller's transaction it locks the wallet row
/// (<c>SELECT … FOR UPDATE</c>, after any session row: lock order §4.4), checks funds, appends the rows with one
/// <c>op_id</c> and the running <c>balance_after</c>, and updates the cached balances, <c>version</c> and
/// <c>lifetime_spent</c> (+|charge|, +|purchase|, −refund). A debit below zero is <c>402 insufficientFunds
/// {required, available}</c> unless <paramref name="allowOverdraft"/> (postpaid settlement, offline replay), which marks the
/// row <c>overdraft</c>. Every row of a club gets its open shift (<c>shift_id</c>, taken <c>FOR SHARE</c> after the wallet —
/// lock order §4.4; closing a shift takes it <c>FOR UPDATE</c>, so no row joins a shift after its Z report was summed). A
/// row racing the close waits for it, no longer finds an open shift and gets <c>shift_id NULL</c>, like a row posted with
/// no shift open: the S5 <c>noShift</c> flag must count those — except a line marked <c>ShiftRequired</c> (a counter top-up),
/// which is refused instead. Pushes go out after the commit, never from here.
/// <para>
/// The bonus account (D-106), without the callers' help: a <c>bonus</c> line moves the bonus account (a credit, or its
/// take-back); a <c>charge</c> — play time, the only thing bonus money buys — takes the bonus first and the main balance for
/// the rest (two rows of one operation, the bonus one described «· бонусы»); a <c>refund</c> of a session's unused time gives
/// the bonus back first, up to what that session took from it, so bonus never turns into real money. The bonus account
/// never goes below zero (an overdraft, a debt stay on the main balance) and nothing else touches it unless a line names it
/// (<see cref="LedgerLine.Account"/>). <c>balance_after</c> is the balance of the row's own account. The returned balance is
/// the main one.
/// </para>
/// </summary>
public static class Ledger
{
    /// <summary>The main balance: real money.</summary>
    public const string Main = "main";

    /// <summary>The bonus account (D-106): play time only, never cash.</summary>
    public const string Bonus = "bonus";

    public static async Task<long> PostAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, bool allowOverdraft, DateTimeOffset now, params LedgerLine[] lines)
    {
        var wallet = await c.QuerySingleOrDefaultAsync<(long Balance, long Bonus, Guid NetworkId)>(
            "SELECT main_balance, bonus_balance, network_id FROM wallets WHERE user_id = @userId FOR UPDATE", new { userId }, tx);
        if (wallet.NetworkId == Guid.Empty)
        {
            throw ApiException.NotFound("user");
        }

        var clubId = lines.FirstOrDefault(l => l.ClubId is not null)?.ClubId;
        var ownerOp = clubId is not null && lines.Any(l => l.OwnerOp);
        var shiftId = clubId is null || ownerOp ? null : await c.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM shifts WHERE club_id = @clubId AND closed_at IS NULL FOR SHARE", new { clubId }, tx);
        if (shiftId is null && !ownerOp && lines.Any(l => l.ShiftRequired))
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: shiftClosed", new { reason = "shiftClosed" });
        }

        var balance = wallet.Balance;
        var bonus = wallet.Bonus;
        var spent = 0L;
        var opId = Guid.CreateVersion7(now);
        foreach (var line in lines.Where(l => l.Amount != 0))
        {
            var fromBonus = await BonusPartAsync(c, tx, line, bonus);
            var fromMain = line.Amount - fromBonus;
            if (bonus + fromBonus < 0)
            {
                throw new ApiException(StatusCodes.Status402PaymentRequired, ErrorCode.InsufficientFunds, "Bonus balance too low",
                    new { required = Money.Uzs(-fromBonus), available = Money.Uzs(bonus), account = Bonus });
            }

            if (fromMain < 0 && balance + fromMain < 0 && !allowOverdraft)
            {
                throw new ApiException(StatusCodes.Status402PaymentRequired, ErrorCode.InsufficientFunds, "Balance too low",
                    new { required = Money.Uzs(-line.Amount), available = Money.Uzs(balance - fromBonus) });
            }

            spent += line.Type is "charge" or "purchase" or "refund" ? -line.Amount : 0;
            if (fromBonus != 0)
            {
                bonus += fromBonus;
                // The line's own id goes to its main row, or to its only row.
                var id = line.Id is { } own && fromMain == 0 ? own : Guid.CreateVersion7(now);
                var description = line.Type is "charge" or "refund" ? line.Description + " · бонусы" : line.Description;
                await InsertAsync(c, tx, line, id, opId, userId, wallet.NetworkId, Bonus, fromBonus, bonus, description, shiftId, overdraft: false, ownerOp, now);
            }

            if (fromMain != 0)
            {
                balance += fromMain;
                await InsertAsync(c, tx, line, line.Id ?? Guid.CreateVersion7(now), opId, userId, wallet.NetworkId, Main, fromMain, balance,
                    line.Description, shiftId, overdraft: balance < 0 && fromMain < 0, ownerOp, now);
            }
        }

        await c.ExecuteAsync(
            """
            UPDATE wallets SET main_balance = @balance, bonus_balance = @bonus, lifetime_spent = greatest(0, lifetime_spent + @spent),
                               version = version + 1, updated_at = @now
            WHERE user_id = @userId
            """,
            new { userId, balance, bonus, spent, now },
            tx);
        return balance;
    }

    /// <summary>
    /// The signed part of <paramref name="line"/> the bonus account takes: all of a bonus line (or one that names the bonus
    /// account), the bonus-first part of a time charge, the bonus a time refund gives back; 0 for everything else.
    /// </summary>
    private static async Task<long> BonusPartAsync(NpgsqlConnection c, NpgsqlTransaction tx, LedgerLine line, long bonus)
    {
        if (line.Account is { } account)
        {
            return account == Bonus ? line.Amount : 0;
        }

        return line.Type switch
        {
            "bonus" => line.Amount,
            "charge" when line.Amount < 0 => -Math.Min(bonus, -line.Amount),
            "refund" when line.Amount > 0 && line.SessionId is { } sessionId => Math.Min(line.Amount, await BonusTakenAsync(c, tx, sessionId)),
            _ => 0,
        };
    }

    /// <summary>What a session took from the bonus account and has not given back yet (its bonus charges less bonus refunds).</summary>
    public static Task<long> BonusTakenAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId) =>
        c.ExecuteScalarAsync<long>(
            """
            SELECT greatest(0, -coalesce(sum(amount), 0))::bigint FROM ledger_entries
            WHERE session_id = @sessionId AND account = 'bonus' AND type IN ('charge', 'refund')
            """,
            new { sessionId }, tx);

    /// <summary>The bonus balance of a user (0 for an unknown one).</summary>
    public static Task<long> BonusOfAsync(NpgsqlConnection c, Guid userId, NpgsqlTransaction? tx = null) =>
        c.ExecuteScalarAsync<long>("SELECT coalesce((SELECT bonus_balance FROM wallets WHERE user_id = @userId), 0)::bigint", new { userId }, tx);

    private static Task InsertAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, LedgerLine line, Guid id, Guid opId, Guid userId, Guid networkId, string account, long amount,
        long balanceAfter, string description, Guid? shiftId, bool overdraft, bool ownerOp, DateTimeOffset now) =>
        c.ExecuteAsync(
            """
            INSERT INTO ledger_entries (id, op_id, user_id, network_id, club_id, type, amount, balance_after, method, description,
                                        ref, session_id, shift_id, staff_id, pc_id, overdraft, meta, created_at, owner_op, account)
            VALUES (@id, @opId, @userId, @networkId, @clubId, @type, @amount, @balanceAfter, @method, @description,
                    @reference, @sessionId, @shiftId, @staffId, @pcId, @overdraft, @meta::jsonb, @now, @ownerOp, @account)
            """,
            new
            {
                id, opId, userId, networkId, clubId = line.ClubId, type = line.Type,
                shiftId = line.ClubId is null ? null : shiftId,
                amount, balanceAfter, method = account == Bonus ? null : line.Method, description,
                reference = line.Ref ?? line.SessionId?.ToString(), sessionId = line.SessionId, staffId = line.StaffId, pcId = line.PcId,
                overdraft,
                meta = line.Meta is null ? null : JsonSerializer.Serialize(line.Meta, ServerJson.Options), now,
                ownerOp = ownerOp && line.ClubId is not null, account,
            },
            tx);

    /// <summary>The wire <c>Balance</c> of a user: the main balance and the bonus account (D-106).</summary>
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
    public const string Columns = "id, user_id, type, amount, balance_after, description, created_at, ref, account";

    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public string Type { get; init; } = "";
    public long Amount { get; init; }
    public long BalanceAfter { get; init; }
    public string Description { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string? Ref { get; init; }
    public string Account { get; init; } = Ledger.Main;

    /// <summary><c>bonus</c> (beyond the contract, D-106) only on a row of the bonus account.</summary>
    public Transaction ToWire() => new(
        Id, UserId, Enum.Parse<TransactionType>(Type, ignoreCase: true), Money.Uzs(Amount), Money.Uzs(BalanceAfter), Description, CreatedAt, Ref,
        Account == Ledger.Bonus ? true : null);
}
