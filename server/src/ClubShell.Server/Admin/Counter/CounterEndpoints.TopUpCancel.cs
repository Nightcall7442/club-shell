using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Sessions;
using ClubShell.Server.Wallet;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// A desk top-up taken back (beyond the contract, D-91): «Отменить пополнение» and «Исправить сумму» of the operations
/// feed, for the cashier who typed a wrong amount (or topped up the wrong client). The money goes back by the same
/// method, the tier bonus it gave is taken back with it, and with <c>amount</c> the right top-up is booked in the same
/// transaction. Mirrors the bar void (D-56): one cancel per top-up, the cashier's window, the drawer check.
/// </summary>
public static partial class CounterEndpoints
{
    /// <summary>A cashier takes back only their own top-up, this long after it; the owner any of the open shift (D-91).</summary>
    public static readonly TimeSpan CashierCancelWindow = TimeSpan.FromMinutes(15);

    private static readonly string[] CancelReasons = ["wrongAmount", "wrongClient", "other"];

    /// <summary><c>ledger_entries.meta.kind</c> of the rows that take a top-up back.</summary>
    public const string TopUpCancelKind = "topUpCancel";

    /// <summary>
    /// <c>POST /admin/wallet/topup/cancel {transactionId, reasonCode: wrongAmount|wrongClient|other, note?, amount?}</c>
    /// → <c>200 {cancelled, balance, topUp|null, expectedCash}</c>. Only a plain desk top-up (<c>404 transaction</c>; a
    /// seat's payment, a debt payment or the club API key's — <c>409 notCorrectable</c>), once (<c>409 alreadyCancelled</c>),
    /// only in the shift that took it, still open (<c>409 topUpShiftClosed</c>). A cashier: their own top-up (<c>403
    /// notOwnTopUp</c>) within 15 minutes (<c>403 cancelWindow {minutes}</c>), and with <c>limits.cashOutOwnerOnly</c> no cash
    /// back (<c>403 ownerOnly</c>); the owner: any desk top-up of the open shift. The player must still have the money:
    /// what the balance can spend (an open postpaid session's bill counted) must cover the top-up, less the corrected top-up
    /// (<c>409 topUpSpent {spendable, required}</c>), and the bonus account the bonus it gave (D-106; <c>409 topUpSpent
    /// {account: bonus, spendable, required}</c>) — a top-up booked before the bonus account had its bonus on the main
    /// balance, which then covers it and gives it back. Cash leaves the drawer under the strong shift lock
    /// (<c>409 cashShort {available}</c>). Ledger: a <c>topUp</c> row of −amount in the same method and a <c>bonus</c> row of
    /// −bonus (<c>meta {kind: topUpCancel, cancelOf}</c>, <c>ref</c> = the top-up), so every sum by method — X/Z, the
    /// expected cash, «Принято за смену», the reports, a guest's cash payable — nets it out by itself. Journal
    /// <c>topUpCancel</c>; <c>suspicious</c> as for a bar void. Staff only; <c>Idempotency-Key</c> required, strict. The owner's
/// own top-up (D-101, in no shift): only the owner (<c>403 ownerOnly</c>), at any time, no shift or drawer; its reversal and
/// correction are the owner's own too. The owner's correction of a cashier's top-up stays in that shift.
    /// </summary>
    private static async Task<IResult> TopUpCancelAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, AutomationService automation, ILoggerFactory logs)
    {
        var staff = StaffOnly(context);
        var r = Api.Read<AdminTopUpCancelRequest>(body, "transactionId", "reasonCode");
        var reason = CancelReasons.Contains(r.ReasonCode, StringComparer.Ordinal) ? r.ReasonCode! : throw ApiException.Validation("reasonCode", "enum");
        var note = r.Note?.Trim() is { Length: > 0 } given ? given : null;
        if (note is { Length: > 200 })
        {
            throw ApiException.Validation("note", "max");
        }

        if (reason == "other" && note is not { Length: >= 3 })
        {
            throw ApiException.Validation("note", note is null ? "required" : "min");
        }

        var corrected = r.Amount is { } a ? Payment(a, "cash", "").Amount : (long?)null;
        var transactionId = r.TransactionId!.Value;
        var effects = new SessionEffects();
        TopUpPosted? paid = null;
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: true, body, async (c, tx) =>
        {
            var now = sessions.Clock.GetUtcNow();
            var top = await c.QuerySingleOrDefaultAsync<TopUpRow>(
                """
                SELECT l.id, l.op_id, l.user_id, l.amount, l.method, l.staff_id, l.shift_id, l.created_at, l.owner_op, u.display_name AS who
                FROM ledger_entries l JOIN users u ON u.id = l.user_id
                WHERE l.id = @transactionId AND l.club_id = @ClubId AND l.type = 'topUp'
                """,
                new { transactionId, staff.ClubId }, tx)
                ?? throw ApiException.NotFound("transaction");

            // The wallet first (§4.4): every cancel of this top-up waits here, so the check below cannot race.
            await c.ExecuteAsync("SELECT 1 FROM wallets WHERE user_id = @UserId FOR UPDATE", new { top.UserId }, tx);
            if (await c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM ledger_entries WHERE user_id = @UserId AND type = 'topUp' AND ref = @ref AND meta ->> 'kind' = @kind)",
                    new { top.UserId, @ref = transactionId.ToString(), kind = TopUpCancelKind }, tx))
            {
                throw SessionService.Conflict("alreadyCancelled");
            }

            // Only a top-up the drawer took on its own: a seat's payment went into the session at once, a debt payment
            // would bring the debt back, the API key's money never was in the drawer, and a reversal row is no top-up.
            // The owner's own top-up (D-101) has no shift: only the owner takes it back, at any time, and no drawer is involved.
            var journal = top.Amount <= 0 || top.StaffId is null || (top.ShiftId is null && !top.OwnerOp) ? null : await c.QuerySingleOrDefaultAsync<string?>(
                """
                SELECT meta::text FROM audit_entries
                WHERE club_id = @ClubId AND shift_id IS NOT DISTINCT FROM @ShiftId AND action = 'topUp' AND meta ->> 'transactionId' = @id
                """,
                new { staff.ClubId, top.ShiftId, id = transactionId.ToString() }, tx);
            if (journal is null || Flag(journal, "forSession") || Flag(journal, "debt"))
            {
                throw SessionService.Conflict("notCorrectable");
            }

            if (top.OwnerOp && !staff.IsOwner)
            {
                throw ApiException.Forbidden("ownerOnly", "Only the owner takes back the owner's own top-up");
            }

            if (!staff.IsOwner && top.StaffId != staff.StaffId)
            {
                throw ApiException.Forbidden("notOwnTopUp", "A cashier takes back only their own top-up");
            }

            if (!staff.IsOwner && now - top.CreatedAt > CashierCancelWindow)
            {
                throw new ApiException(StatusCodes.Status403Forbidden, ErrorCode.Forbidden, "A cashier takes a top-up back only within 15 minutes",
                    new { reason = "cancelWindow", minutes = (int)CashierCancelWindow.TotalMinutes });
            }

            var method = top.Method ?? "cash";
            var cashBack = method == "cash" ? Math.Max(0, top.Amount - (corrected ?? 0)) : 0;
            if (cashBack > 0 && !staff.IsOwner && (await SessionService.ClubAsync(c, tx, staff.ClubId)).Pricing.CashOutOwnerOnly)
            {
                throw ApiException.Forbidden("ownerOnly", "Only the owner takes cash out of the drawer");
            }

            // Cash going back takes the strong lock — wallet → shift, nothing after it (§4.4), as a payout; other methods
            // only keep the close out until this commits.
            ShiftEndpoints.OpenShiftLock? shift = null;
            if (!top.OwnerOp)
            {
                shift = await ShiftEndpoints.LockOpenShiftAsync(c, tx, staff.ClubId, strong: cashBack > 0);
                if (shift.Id != top.ShiftId)
                {
                    throw SessionService.Conflict("topUpShiftClosed");
                }

                if (cashBack > 0)
                {
                    await ShiftEndpoints.EnsureDrawerAsync(c, tx, shift, cashBack);
                }
            }

            // The bonus the top-up gave, by the account it went to: the bonus account (D-106), or the main balance for a top-up
            // booked before it.
            var bonuses = (await c.QueryAsync<(string Account, long Amount)>(
                "SELECT account, sum(amount)::bigint FROM ledger_entries WHERE op_id = @OpId AND type = 'bonus' GROUP BY account", new { top.OpId }, tx))
                .ToDictionary(b => b.Account, b => b.Amount);
            var bonusOnMain = bonuses.GetValueOrDefault(Ledger.Main);
            var bonusOnBonus = bonuses.GetValueOrDefault(Ledger.Bonus);
            var bonus = bonusOnMain + bonusOnBonus;
            if (corrected is { } right)
            {
                if (right == top.Amount)
                {
                    throw ApiException.Validation("amount", "same");
                }

                // The right top-up first: the balance never dips below zero between the two, and its bonus is the tier's.
                paid = await PostTopUpAsync(c, tx, staff, effects, top.UserId, right, method, now, correctionOf: transactionId, ownerOp: top.OwnerOp) with { ReachedBefore = top.Amount };
            }

            // Nothing spent is taken back, not even in part (D-91): the main balance must still hold the top-up (and an old
            // top-up's bonus), the bonus account the bonus it gave — what the right top-up brought counts towards both.
            var (balance, bonusLeft) = await c.QuerySingleAsync<(long, long)>(
                "SELECT main_balance, bonus_balance FROM wallets WHERE user_id = @UserId", new { top.UserId }, tx);
            var spendable = await ShopSaleEndpoints.SpendableAsync(c, tx, top.UserId, balance, now, bonusLeft - bonusOnBonus);
            if (spendable < top.Amount + bonusOnMain)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: topUpSpent", new
                {
                    reason = "topUpSpent",
                    spendable = Money.Uzs(Math.Max(0, spendable - (paid?.Amount ?? 0))),
                    required = Money.Uzs(top.Amount + bonusOnMain - (paid?.Amount ?? 0)),
                });
            }

            if (bonusLeft < bonusOnBonus)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: topUpSpent", new
                {
                    reason = "topUpSpent",
                    account = Ledger.Bonus,
                    spendable = Money.Uzs(Math.Max(0, bonusLeft - (paid?.Bonus ?? 0))),
                    required = Money.Uzs(bonusOnBonus - (paid?.Bonus ?? 0)),
                });
            }

            var id = Guid.CreateVersion7(now);
            var meta = new { kind = TopUpCancelKind, cancelOf = transactionId, reasonCode = reason };
            balance = await Ledger.PostAsync(c, tx, top.UserId, allowOverdraft: false, now,
                new LedgerLine("topUp", -top.Amount, "Отмена: " + char.ToLowerInvariant(TopUpDescription(method)[0]) + TopUpDescription(method)[1..],
                    staff.ClubId, Method: method, StaffId: staff.StaffId, Meta: meta, Id: id, ShiftRequired: true, Ref: transactionId.ToString(), OwnerOp: top.OwnerOp),
                new LedgerLine("bonus", -bonusOnBonus, "Отмена бонуса за пополнение", staff.ClubId, StaffId: staff.StaffId, Meta: meta, Ref: transactionId.ToString(),
                    OwnerOp: top.OwnerOp, Account: Ledger.Bonus),
                new LedgerLine("bonus", -bonusOnMain, "Отмена бонуса за пополнение", staff.ClubId, StaffId: staff.StaffId, Meta: meta, Ref: transactionId.ToString(),
                    OwnerOp: top.OwnerOp, Account: Ledger.Main));
            await Audit.WriteAsync(c, tx, staff, now, "topUpCancel", top.UserId, amount: top.Amount, detail: top.Who,
                meta: new
                {
                    method, bonus, transactionId = id, cancelOf = transactionId, topUpAt = ServerJson.FormatTime(top.CreatedAt), reasonCode = reason, note,
                    correctedTo = paid?.Amount, correctedId = paid?.Id,
                },
                shiftId: shift?.Id, ownerOp: top.OwnerOp);
            if (shift is not null)
            {
                await ControlAlerts.TopUpCancelAsync(c, tx, staff, shift.Id, top.Amount, top.Who, now);
            }
            effects.Wallets.Add(top.UserId);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminTopUpCancelResponse(
                new AdminTopUpCancelled(id, now, transactionId, top.CreatedAt, top.Amount, bonus, method, reason, note, staff.Name),
                Money.Uzs(balance),
                paid is null ? null : new AdminTopUpResponse(Money.Uzs(paid.Balance), paid.Transaction, Money.Uzs(paid.Bonus)),
                await ShiftEndpoints.ExpectedCashAsync(c, tx, staff.ClubId))));
        }, strictBody: true);
        await sessions.PublishAsync(effects);
        await AfterTopUpAsync(automation, logs, staff, paid);
        return result;
    }

    private static bool Flag(string json, string key)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    }

    private sealed class TopUpRow
    {
        public Guid Id { get; init; }
        public Guid OpId { get; init; }
        public Guid UserId { get; init; }
        public long Amount { get; init; }
        public string? Method { get; init; }
        public Guid? StaffId { get; init; }
        public Guid? ShiftId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public bool OwnerOp { get; init; }
        public string Who { get; init; } = "";
    }
}
