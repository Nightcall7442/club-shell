using FluentMigrator;

namespace ClubShell.Server.Db.Migrations;

/// <summary>
/// The bonus account (D-106): every bonus credit (top-up bonus, the x2 promotion, a promo code, an automation reward) goes to
/// <c>wallets.bonus_balance</c> (M0002, 0 until now) instead of the main balance, and pays only for play time. A ledger row
/// names the account it moves (<c>account</c>: <c>main</c> | <c>bonus</c>; every earlier row is <c>main</c>), and its
/// <c>balance_after</c> is that account's balance. A bonus row is never cash: no payment method, only a bonus credit or its
/// take-back, a charge for time, a refund of time or an adjustment (the write-off of a deleted client). Existing main
/// balances are left as they are: the bonus money already in them cannot be told apart.
/// </summary>
[Migration(2026112701, "bonus account")]
public sealed class M0030_BonusAccount : Migration
{
    public override void Up() => Execute.Sql(
        """
        ALTER TABLE ledger_entries ADD COLUMN account text NOT NULL DEFAULT 'main'
            CONSTRAINT ledger_entries_account CHECK (account IN ('main', 'bonus')),
            ADD CONSTRAINT ledger_entries_bonus_no_cash
            CHECK (account = 'main' OR (method IS NULL AND type IN ('bonus', 'charge', 'refund', 'adjustment')));
        """);

    public override void Down() => Execute.Sql(
        "ALTER TABLE ledger_entries DROP CONSTRAINT ledger_entries_bonus_no_cash, DROP COLUMN account;");
}
