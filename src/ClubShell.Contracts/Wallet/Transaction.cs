using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;

namespace ClubShell.Contracts.Wallet;

/// <summary>Kind of wallet transaction.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<TransactionType>))]
public enum TransactionType
{
    /// <summary>Balance top-up via a payment provider or cash.</summary>
    TopUp,

    /// <summary>Session time charge.</summary>
    Charge,

    /// <summary>Refund of unused prepaid time or a cancelled order.</summary>
    Refund,

    /// <summary>Promotional bonus credit.</summary>
    Bonus,

    /// <summary>Shop purchase.</summary>
    Purchase,

    /// <summary>Manual admin adjustment.</summary>
    Adjustment,
}

/// <summary>Top-up payment provider.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<TopupProvider>))]
public enum TopupProvider
{
    /// <summary>Payme (QR / deep link).</summary>
    Payme,

    /// <summary>Click (QR / deep link).</summary>
    Click,

    /// <summary>Uzum Bank.</summary>
    Uzum,

    /// <summary>Cash at the desk; creates an admin ticket.</summary>
    Cash,
}

/// <summary>Lifecycle of a <see cref="TopupIntent"/>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<TopupStatus>))]
public enum TopupStatus
{
    /// <summary>Awaiting payment.</summary>
    Pending,

    /// <summary>Paid; balance updated (<c>wallet.updated</c> follows).</summary>
    Paid,

    /// <summary>Expired unpaid.</summary>
    Expired,

    /// <summary>Cancelled by the user or an admin.</summary>
    Cancelled,
}

/// <summary>Wallet ledger entry (IPC_PROTOCOL.md §6.12).</summary>
/// <param name="Id">Transaction id.</param>
/// <param name="UserId">Wallet owner.</param>
/// <param name="Type">Kind.</param>
/// <param name="Amount">Signed amount: negative for <see cref="TransactionType.Charge"/> and <see cref="TransactionType.Purchase"/>.</param>
/// <param name="BalanceAfter">Main balance after this entry.</param>
/// <param name="Description">Human-readable description (localized per <c>Accept-Language</c>).</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Ref">External reference: order id, session id or payment id.</param>
public sealed record Transaction(
    Guid Id,
    Guid UserId,
    TransactionType Type,
    Money Amount,
    Money BalanceAfter,
    string Description,
    DateTimeOffset CreatedAt,
    string? Ref = null);

/// <summary>Pending or settled top-up (IPC_PROTOCOL.md §6.12). Payload of <c>wallet.topupIntent</c>.</summary>
/// <param name="Id">Intent id.</param>
/// <param name="Provider">Payment provider.</param>
/// <param name="Amount">Requested amount.</param>
/// <param name="Status">Current status.</param>
/// <param name="QrUrl">QR image URL to render, when the provider supports QR payment.</param>
/// <param name="DeepLink">Mobile app deep link, when available.</param>
/// <param name="PaymentUrl">Web payment page, when available.</param>
/// <param name="ExpiresAt">When the intent expires unpaid.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record TopupIntent(
    Guid Id,
    TopupProvider Provider,
    Money Amount,
    TopupStatus Status,
    string? QrUrl,
    string? DeepLink,
    string? PaymentUrl,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

/// <summary>Body of <c>POST /wallet/{userId}/topup-intent</c> (SERVER_API.md §4.8).</summary>
/// <param name="Amount">Requested amount; at least 1 000 UZS (100 000 minor units).</param>
/// <param name="Provider">Payment provider.</param>
/// <param name="PcId">PC the request originates from.</param>
public sealed record TopupIntentCreateRequest(
    Money Amount,
    TopupProvider Provider,
    Guid PcId)
{
    /// <summary>Minimum top-up amount in minor units (1 000 UZS).</summary>
    public const long MinAmountMinor = 100_000;
}
