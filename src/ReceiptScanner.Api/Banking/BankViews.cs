using ReceiptScanner.Api.Receipts;
using ReceiptScanner.Core.Banking;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Api.Banking;

// Response shapes from API.md#reconciliation (banking/serializers.py).
// Amounts are signed as the bank signs them; amount_abs lines up with a
// receipt total.

public sealed record AccountRef(long Id, string Name, string Account);

public sealed record TransactionRead(
    long Id,
    AccountRef Account,
    DateOnly Date,
    DateOnly PostedOn,
    DateOnly? UserDate,
    decimal Amount,
    decimal AmountAbs,
    string Trntype,
    string Memo,
    string Merchant,
    CategoryRef? Category,
    long? ReceiptId,
    string MatchMethod,
    string MatchConfidence,
    string MatchNote,
    DateTime? MatchedAt,
    long? TransferPairId);

public sealed record CandidateRead(TransactionRead Transaction, string Confidence, string Reason, int DaysApart, string ShopMatch);

public sealed record QueueReceiptRead(long Id, string Shop, decimal Total, DateOnly Date, string? ImageUrl, int ItemCount, DateTime CreatedAt);

public sealed record QueueItemRead(QueueReceiptRead Receipt, string Bucket, IReadOnlyList<CandidateRead> Candidates, bool NeedsChoice);

/// <summary>"bank_match" on receipt create and scan approve.</summary>
public sealed record BankMatchRead(long TransactionId, string Memo, string Confidence, string Reason);

public static class BankViews
{
    /// <summary>Needs Account and Category (with Parent) loaded.</summary>
    public static TransactionRead Transaction(BankTransaction t) => new(
        t.Id,
        new AccountRef(t.AccountId, t.Account.ToString(), t.Account.MaskedAccountId),   // never the full number
        Matching.TransactionDate(t.PostedOn, t.UserDate),
        t.PostedOn,
        t.UserDate,
        t.Amount,
        Math.Abs(t.Amount),
        t.Trntype,
        t.Memo,
        Matching.MemoMerchant(t.Memo),
        t.Category is null ? null
            : new CategoryRef(t.Category.Id, t.Category.Name, t.Category.FullName, t.Category.Kind, t.CategorySource),
        t.ReceiptId,
        t.MatchMethod,
        t.MatchConfidence,
        t.MatchNote,
        t.MatchedAt,
        t.TransferPairId);

    public static QueueItemRead QueueItem(QueueItem item, HttpRequest request)
    {
        var r = item.Receipt;
        var candidates = item.Match.Ordered
            .Select(c => new CandidateRead(Transaction(item.Transactions[c.TransactionId]), c.Confidence, c.Note, c.DaysApart, c.ShopMatch))
            .ToList();
        return new QueueItemRead(
            new QueueReceiptRead(r.Id, r.Shop, r.Total, r.PurchasedOn,
                r.Image is null ? null : $"{request.Scheme}://{request.Host}{request.PathBase}/api/receipts/{r.Id}/image/",
                item.ItemCount, r.CreatedAt),
            item.Bucket,
            candidates,
            // "Which of these?" reads differently from "is this right?".
            NeedsChoice: candidates.Count > 1);
    }
}
