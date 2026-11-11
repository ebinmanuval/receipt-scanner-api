using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReceiptScanner.Core.Domain;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Api.Banking;

public sealed class BankingOptions
{
    public const string Section = "Banking";

    /// <summary>How far apart the two sides of a card repayment may post (TRANSFER_PAIR_DAYS).</summary>
    public int TransferPairDays { get; set; } = 5;
}

public sealed class PairResult(bool dryRun)
{
    public List<(long Debit, long Credit)> Paired { get; } = [];
    public List<long> Ambiguous { get; } = [];
    public bool DryRun { get; } = dryRun;

    public object AsDict() => new Dictionary<string, object>
    {
        ["paired"] = Paired.Count,
        ["ambiguous"] = Ambiguous.Count,
        ["dry_run"] = DryRun,
    };
}

/// <summary>
/// Port of banking/transfers.py: credit-card repayments, seen from both accounts.
/// </summary>
/// <remarks>
/// Paying the card off appears twice: a debit on the everyday account and a
/// credit on the card. Neither is income or spending. They're recognised by
/// pairing, not wording: a credit on a card account and a debit of exactly
/// the same amount on another account, within the window. Only uncategorised
/// or transfer-category rows take part; a debit linked to a receipt was a
/// purchase. Ambiguity is left alone.
/// <para>
/// The category rules are out of scope here, but if a "Credit Card
/// Repayment" (or "Between Accounts") transfer category exists, paired rows
/// are filed under it with source "pair", as in Django.
/// </para>
/// </remarks>
public sealed class TransferPairing(ReceiptScannerDbContext db, TimeProvider clock, IOptions<BankingOptions> options)
{
    public const string Repayment = "Credit Card Repayment";
    public const string Fallback = "Between Accounts";

    public int WindowDays => options.Value.TransferPairDays;

    /// <summary>Find and link every unambiguous repayment whose card credit posted in the range.</summary>
    public async Task<PairResult> PairAsync(DateOnly? since, DateOnly? until, bool dryRun, CancellationToken ct)
    {
        var result = new PairResult(dryRun);

        var cards = await db.BankAccounts.Where(a => a.IsCreditCard).Select(a => a.Id).ToListAsync(ct);
        if (cards.Count == 0) return result;

        var kinds = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Kind, ct);
        bool Eligible(BankTransaction t) => t.CategoryId is not { } id || kinds.GetValueOrDefault(id) == CategoryKind.Transfer;

        var creditQuery = db.BankTransactions.AsNoTracking()
            .Where(t => cards.Contains(t.AccountId) && t.Amount > 0 && t.TransferPairId == null);
        if (since is { } s) creditQuery = creditQuery.Where(t => t.PostedOn >= s);
        if (until is { } u) creditQuery = creditQuery.Where(t => t.PostedOn <= u);
        var credits = (await creditQuery.OrderByDescending(t => t.PostedOn).ThenByDescending(t => t.Id).ToListAsync(ct))
            .Where(Eligible).ToList();
        if (credits.Count == 0) return result;

        var first = credits.Min(c => c.PostedOn).AddDays(-WindowDays);
        var last = credits.Max(c => c.PostedOn).AddDays(WindowDays);
        var amounts = credits.Select(c => -c.Amount).Distinct().ToList();
        var debits = (await db.BankTransactions.AsNoTracking()
                .Where(t => t.Amount < 0 && t.TransferPairId == null && t.ReceiptId == null
                            && t.PostedOn >= first && t.PostedOn <= last && amounts.Contains(t.Amount))
                .ToListAsync(ct))
            .Where(Eligible)
            .ToLookup(d => -d.Amount);

        var options = new Dictionary<long, List<BankTransaction>>();
        var claimed = new Dictionary<long, int>();   // debit id → credits wanting it
        foreach (var credit in credits)
        {
            options[credit.Id] = debits[credit.Amount]
                .Where(d => d.AccountId != credit.AccountId
                            && Math.Abs(d.PostedOn.DayNumber - credit.PostedOn.DayNumber) <= WindowDays)
                .ToList();
            foreach (var d in options[credit.Id]) claimed[d.Id] = claimed.GetValueOrDefault(d.Id) + 1;
        }

        foreach (var credit in credits)
        {
            var found = options[credit.Id];
            if (found.Count == 1 && claimed[found[0].Id] == 1) result.Paired.Add((found[0].Id, credit.Id));
            else if (found.Count > 0) result.Ambiguous.Add(credit.Id);
        }

        if (dryRun || result.Paired.Count == 0) return result;

        var categoryId = await RepaymentCategoryAsync(ct);
        foreach (var (debit, credit) in result.Paired)
        {
            await LinkAsync(debit, credit, categoryId, ct);
            await LinkAsync(credit, debit, categoryId, ct);
        }
        return result;
    }

    /// <summary>The category paired rows get, if one exists: by name, falling back to the generic one.</summary>
    private async Task<long?> RepaymentCategoryAsync(CancellationToken ct)
    {
        foreach (var name in new[] { Repayment, Fallback })
        {
            var id = await db.Categories
                .Where(c => c.Name == name && c.Kind == CategoryKind.Transfer && c.IsActive)
                .Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);
            if (id is not null) return id;
        }
        return null;
    }

    private Task<int> LinkAsync(long row, long other, long? categoryId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var target = db.BankTransactions.Where(t => t.Id == row);
        return categoryId is { } cid
            ? target.ExecuteUpdateAsync(u => u
                .SetProperty(t => t.TransferPairId, other)
                .SetProperty(t => t.PairedAt, now)
                .SetProperty(t => t.CategoryId, cid)
                .SetProperty(t => t.CategorySource, CategorySource.Pair)
                .SetProperty(t => t.CategoryRuleId, (long?)null)
                .SetProperty(t => t.CategoryConfidence, (double?)null), ct)
            : target.ExecuteUpdateAsync(u => u
                .SetProperty(t => t.TransferPairId, other)
                .SetProperty(t => t.PairedAt, now), ct);
    }
}
