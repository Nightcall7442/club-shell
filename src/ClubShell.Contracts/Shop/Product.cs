using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Shop;

/// <summary>Shop product category.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ProductCategory>))]
public enum ProductCategory
{
    /// <summary>Hot food.</summary>
    Food,

    /// <summary>Drinks.</summary>
    Drink,

    /// <summary>Snacks.</summary>
    Snack,

    /// <summary>Services (e.g. headset rental).</summary>
    Service,

    /// <summary>Merchandise.</summary>
    Merch,

    /// <summary>Time packages sold through the shop.</summary>
    Time,
}

/// <summary>Shop product (IPC_PROTOCOL.md §6.13).</summary>
/// <param name="Id">Product id.</param>
/// <param name="Title">Localized title.</param>
/// <param name="Category">Category.</param>
/// <param name="Price">Unit price.</param>
/// <param name="ImageUrl">Image URL.</param>
/// <param name="InStock">Availability; when served from cache offline this is unknown and shown as available.</param>
/// <param name="StockQty">Remaining quantity, when tracked.</param>
/// <param name="Tags">Free-form tags.</param>
public sealed record Product(
    Guid Id,
    string Title,
    ProductCategory Category,
    Money Price,
    string ImageUrl,
    bool InStock,
    int? StockQty,
    IReadOnlyList<string> Tags);
