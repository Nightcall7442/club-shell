using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Shop;

/// <summary>Order lifecycle.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<OrderStatus>))]
public enum OrderStatus
{
    /// <summary>Placed, awaiting staff acceptance; cancellable.</summary>
    Pending,

    /// <summary>Accepted by staff.</summary>
    Accepted,

    /// <summary>Being prepared.</summary>
    Preparing,

    /// <summary>On the way to the seat.</summary>
    Delivering,

    /// <summary>Delivered.</summary>
    Done,

    /// <summary>Cancelled by the user or staff; charged amount refunded.</summary>
    Cancelled,
}

/// <summary>Helpers over <see cref="OrderStatus"/>.</summary>
public static class OrderStatusExtensions
{
    /// <summary><see langword="true"/> while the order is still in progress (not <see cref="OrderStatus.Done"/> / <see cref="OrderStatus.Cancelled"/>).</summary>
    public static bool IsActive(this OrderStatus status) => status is not (OrderStatus.Done or OrderStatus.Cancelled);
}

/// <summary>Line of a placed order; <paramref name="Price"/> is the unit price at order time.</summary>
/// <param name="ProductId">Product id.</param>
/// <param name="Title">Product title at order time.</param>
/// <param name="Qty">Quantity (1–99).</param>
/// <param name="Price">Unit price at order time.</param>
public sealed record OrderItem(
    Guid ProductId,
    string Title,
    int Qty,
    Money Price)
{
    /// <summary><see cref="Price"/> × <see cref="Qty"/>.</summary>
    [JsonIgnore]
    public Money LineTotal => Price * Qty;
}

/// <summary>Line of an order being placed (<c>shop.order</c> / <c>POST /shop/orders</c>).</summary>
/// <param name="ProductId">Product id.</param>
/// <param name="Qty">Quantity (1–99).</param>
public sealed record OrderLineRequest(
    Guid ProductId,
    int Qty)
{
    /// <summary>Maximum quantity per line.</summary>
    public const int MaxQty = 99;

    /// <summary>Maximum lines per order.</summary>
    public const int MaxLines = 20;
}

/// <summary>Shop order (IPC_PROTOCOL.md §6.13).</summary>
/// <param name="Id">Order id.</param>
/// <param name="UserId">Buyer.</param>
/// <param name="PcId">Seat to deliver to.</param>
/// <param name="Items">Lines.</param>
/// <param name="Total">Total charged.</param>
/// <param name="Status">Current status.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last status change.</param>
/// <param name="Note">Free-text note for staff.</param>
public sealed record Order(
    Guid Id,
    Guid UserId,
    Guid PcId,
    IReadOnlyList<OrderItem> Items,
    Money Total,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Note = null);

/// <summary>Body of <c>POST /shop/orders</c> (SERVER_API.md §4.9). Sent with an <c>Idempotency-Key</c>.</summary>
/// <param name="UserId">Buyer.</param>
/// <param name="PcId">Seat to deliver to.</param>
/// <param name="SessionId">Current session, when any.</param>
/// <param name="Items">Lines (1–20).</param>
/// <param name="Note">Free-text note for staff.</param>
public sealed record OrderCreateRequest(
    Guid UserId,
    Guid PcId,
    Guid? SessionId,
    IReadOnlyList<OrderLineRequest> Items,
    string? Note = null);
