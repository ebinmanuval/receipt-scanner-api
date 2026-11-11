using ReceiptScanner.Api.Json;
using ReceiptScanner.Core.Receipts;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Api.Receipts;

// Response shapes from API.md. Property names become snake_case on the wire;
// decimals are strings (2 places unless marked).

public sealed record CategoryRef(long Id, string Name, string FullName, string Kind, string Source);

public sealed record ItemRead(
    long Id,
    string Item,
    [property: DecimalString(3)] decimal Qty,
    decimal Price,
    [property: DecimalString(4)] decimal? UnitPrice);

/// <summary>ReceiptReadSerializer: list entries, detail, and the "receipt" in create/approve responses.</summary>
public sealed record ReceiptRead(
    long Id,
    string Shop,
    decimal Total,
    DateOnly Date,
    Guid? ClientUuid,
    string? ImageUrl,
    bool NeedsReview,
    string ReviewNote,
    CategoryRef? Category,
    IReadOnlyList<ItemRead> Items,
    DateTime CreatedAt);

/// <summary>Do the line items add up? (create and approve responses)</summary>
public sealed record TotalsRead(string Stated, string ItemsSum, string Discrepancy, bool Reconciles, string? Note);

public sealed record ReceiptCreated(
    string Status,
    long ReceiptId,
    int ReceiptRows,
    int ItemRows,
    bool ImageSaved,
    string? ImageNote,
    TotalsRead Totals,
    object? BankMatch,
    ReceiptRead Receipt);

public static class ReceiptViews
{
    /// <summary>Needs Items and Category (with Parent) loaded.</summary>
    public static ReceiptRead Read(Receipt r, HttpRequest request) => new(
        r.Id,
        r.Shop,
        r.Total,
        r.PurchasedOn,
        r.ClientUuid,
        // The authenticated endpoint, absolute, as DRF's build_absolute_uri gives it.
        r.Image is null ? null : $"{request.Scheme}://{request.Host}{request.PathBase}/api/receipts/{r.Id}/image/",
        r.NeedsReview,
        r.ReviewNote,
        r.Category is null ? null
            : new CategoryRef(r.Category.Id, r.Category.Name, r.Category.FullName, r.Category.Kind, r.CategorySource),
        r.Items.OrderBy(i => i.Id)
            .Select(i => new ItemRead(i.Id, i.Name, i.Qty, i.LineTotal, i.UnitPrice))
            .ToList(),
        r.CreatedAt);

    public static TotalsRead Totals(Receipt r) => new(
        ReceiptParsing.Money(r.Total),
        ReceiptParsing.Money(r.ItemsTotal),
        ReceiptParsing.Money(r.Discrepancy),
        !r.NeedsReview,
        string.IsNullOrEmpty(r.ReviewNote) ? null : r.ReviewNote);
}
