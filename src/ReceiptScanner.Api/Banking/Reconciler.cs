using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Core.Banking;
using ReceiptScanner.Core.Domain;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Api.Banking;

/// <summary>A receipt awaiting a decision, with its candidates and the transactions behind them.</summary>
public sealed record QueueItem(Receipt Receipt, CandidateSet Match, string Bucket,
    IReadOnlyDictionary<long, BankTransaction> Transactions, int ItemCount);

public sealed class AutoLinkResult
{
    public List<(long ReceiptId, Candidate Candidate)> Linked { get; } = [];
    public List<long> Ambiguous { get; } = [];
    public List<long> Weak { get; } = [];
    public List<long> Nothing { get; } = [];
    public int NeedsReview => Ambiguous.Count + Weak.Count;

    public object AsDict() => new Dictionary<string, int>
    {
        ["linked"] = Linked.Count,
        ["ambiguous"] = Ambiguous.Count,
        ["weak"] = Weak.Count,
        ["no_candidate"] = Nothing.Count,
        ["needs_review"] = NeedsReview,
    };
}

/// <summary>
/// Port of banking/reconcile.py: the only place that writes a receipt ↔
/// transaction link. The API and auto-linking both go through here.
/// </summary>
/// <remarks>
/// A link is one conditional UPDATE (<c>… WHERE id = @t AND (receipt_id IS
/// NULL OR receipt_id = @r)</c>), backed by the unique index on receipt_id.
/// That gives the same guarantee as Django's SELECT … FOR UPDATE: an
/// existing link is never stolen, and two devices racing for one receipt or
/// one transaction get 409 match_unavailable instead of last-writer-wins.
/// </remarks>
public sealed class Reconciler(ReceiptScannerDbContext db, TimeProvider clock, ILogger<Reconciler> logger)
{
    public const int DefaultWindow = 3;
    public const int MaxWindow = 14;

    public static readonly string[] Buckets =
        [CandidateSet.Ambiguous, CandidateSet.Auto, CandidateSet.NoCandidate, CandidateSet.Weak];

    // --- finding candidates ------------------------------------------------------------

    /// <summary>Receipts with no transaction and not marked as cash, oldest purchase first.</summary>
    public IQueryable<Receipt> UnmatchedReceipts(DateOnly? since = null, DateOnly? until = null)
    {
        var q = db.Receipts.AsNoTracking().Where(r => !r.NoBankMatch && r.BankTransaction == null);
        if (since is { } s) q = q.Where(r => r.PurchasedOn >= s);
        if (until is { } u) q = q.Where(r => r.PurchasedOn <= u);
        return q.OrderBy(r => r.PurchasedOn).ThenBy(r => r.Id);
    }

    /// <summary>
    /// find_candidates for many receipts with two queries instead of one per
    /// receipt: every unlinked transaction of a matching amount in the date
    /// span, and every rejection for these receipts.
    /// </summary>
    public async Task<(Dictionary<long, CandidateSet> Sets, Dictionary<long, BankTransaction> Transactions)> CandidatesAsync(
        IReadOnlyList<Receipt> receipts, int window, CancellationToken ct)
    {
        var sets = receipts.ToDictionary(r => r.Id, _ => new CandidateSet([]));
        var eligible = receipts.Where(r => !r.NoBankMatch).ToList();
        if (eligible.Count == 0) return (sets, []);

        var amounts = eligible.Select(r => -r.Total).Distinct().ToList();
        var from = eligible.Min(r => r.PurchasedOn).AddDays(-window - 7);
        var to = eligible.Max(r => r.PurchasedOn).AddDays(window + 7);

        var transactions = await db.BankTransactions.AsNoTracking()
            .Include(t => t.Account)
            .Include(t => t.Category!).ThenInclude(c => c.Parent)
            .Where(t => t.ReceiptId == null && amounts.Contains(t.Amount) && t.PostedOn >= from && t.PostedOn <= to)
            .OrderByDescending(t => t.PostedOn).ThenByDescending(t => t.Id)   // the model's ordering: ties go to the newest
            .ToListAsync(ct);

        var receiptIds = eligible.Select(r => r.Id).ToList();
        var rejected = (await db.MatchRejections.AsNoTracking()
                .Where(m => receiptIds.Contains(m.ReceiptId))
                .Select(m => new { m.ReceiptId, m.TransactionId })
                .ToListAsync(ct))
            .ToLookup(m => m.ReceiptId, m => m.TransactionId);

        foreach (var receipt in eligible)
        {
            var low = receipt.PurchasedOn.AddDays(-window);
            var high = receipt.PurchasedOn.AddDays(window);
            var no = rejected[receipt.Id].ToHashSet();

            sets[receipt.Id] = new CandidateSet(transactions
                .Where(t => t.Amount == -receipt.Total
                            && t.PostedOn >= low.AddDays(-7) && t.PostedOn <= high.AddDays(7)
                            && !no.Contains(t.Id)
                            && Matching.IsPurchase(t.Memo))
                .Select(t => (t, days: Math.Abs(Matching.TransactionDate(t.PostedOn, t.UserDate).DayNumber - receipt.PurchasedOn.DayNumber)))
                .Where(x => x.days <= window)
                .Select(x => new Candidate(x.t.Id, x.days, Matching.MerchantMatch(receipt.Shop, x.t.Memo))));
        }
        return (sets, transactions.ToDictionary(t => t.Id));
    }

    // --- queue and summary -----------------------------------------------------------------

    /// <summary>Receipts a person needs to look at, computed fresh each time (a stored suggestion goes stale).</summary>
    public async Task<List<QueueItem>> QueueAsync(int window, DateOnly? since, DateOnly? until, IReadOnlySet<string> buckets, CancellationToken ct)
    {
        var receipts = await UnmatchedReceipts(since, until).ToListAsync(ct);
        var (sets, transactions) = await CandidatesAsync(receipts, window, ct);

        var wanted = receipts.Where(r => buckets.Contains(sets[r.Id].Bucket)).ToList();
        var ids = wanted.Select(r => r.Id).ToList();
        var itemCounts = await db.ReceiptItems.AsNoTracking()
            .Where(i => ids.Contains(i.ReceiptId))
            .GroupBy(i => i.ReceiptId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.N, ct);

        return wanted
            .Select(r => new QueueItem(r, sets[r.Id], sets[r.Id].Bucket, transactions, itemCounts.GetValueOrDefault(r.Id)))
            .ToList();
    }

    /// <summary>Counts per bucket, for a badge.</summary>
    public async Task<Dictionary<string, object>> SummaryAsync(int window, CancellationToken ct)
    {
        var receipts = await UnmatchedReceipts().ToListAsync(ct);
        var (sets, _) = await CandidatesAsync(receipts, window, ct);
        var counts = Buckets.ToDictionary(b => b, _ => 0);
        foreach (var r in receipts) counts[sets[r.Id].Bucket]++;

        return new Dictionary<string, object>
        {
            ["status"] = "success",
            ["auto"] = counts[CandidateSet.Auto],
            ["ambiguous"] = counts[CandidateSet.Ambiguous],
            ["weak"] = counts[CandidateSet.Weak],
            ["none"] = counts[CandidateSet.NoCandidate],
            ["needs_review"] = counts[CandidateSet.Ambiguous] + counts[CandidateSet.Weak],
            ["matched"] = await db.BankTransactions.CountAsync(t => t.ReceiptId != null, ct),
            ["marked_no_match"] = await db.Receipts.CountAsync(r => r.NoBankMatch, ct),
        };
    }

    // --- writing ---------------------------------------------------------------------------

    /// <summary>Record the link. The only writer of the five match fields.</summary>
    public async Task<BankTransaction> LinkAsync(Receipt receipt, long transactionId, string method, string confidence, string note, CancellationToken ct)
    {
        // One transaction per receipt is only a query, so check it here; one
        // receipt per transaction is the unique index.
        var other = await db.BankTransactions
            .Where(t => t.ReceiptId == receipt.Id && t.Id != transactionId)
            .Select(t => (long?)t.Id).FirstOrDefaultAsync(ct);
        if (other is { } otherId)
            throw AlreadyLinked($"Receipt #{receipt.Id} is already linked to transaction #{otherId}.", otherId, receipt.Id);

        var now = clock.GetUtcNow().UtcDateTime;
        var trimmedNote = note.Length > 255 ? note[..255] : note;
        int updated;
        try
        {
            updated = await db.BankTransactions
                .Where(t => t.Id == transactionId && (t.ReceiptId == null || t.ReceiptId == receipt.Id))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.ReceiptId, receipt.Id)
                    .SetProperty(t => t.MatchedAt, now)
                    .SetProperty(t => t.MatchMethod, method)
                    .SetProperty(t => t.MatchConfidence, confidence)
                    .SetProperty(t => t.MatchNote, trimmedNote), ct);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            // The unique index fired: the receipt was claimed between the check above and this write.
            throw AlreadyLinked($"Receipt #{receipt.Id} was linked to another transaction just now.", null, receipt.Id);
        }

        if (updated == 0)
        {
            var current = await db.BankTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transactionId, ct)
                ?? throw NotFound($"No transaction #{transactionId}.", "transaction_id", transactionId);
            throw AlreadyLinked($"Transaction #{transactionId} is already linked to receipt #{current.ReceiptId}.", transactionId, current.ReceiptId);
        }

        return await LoadTransactionAsync(transactionId, ct);
    }

    /// <summary>
    /// Accept a pairing. Any unlinked transaction is allowed, not only a
    /// suggested one; an unsuggested pick is recorded as "manual".
    /// </summary>
    public async Task<BankTransaction> ConfirmAsync(long receiptId, long transactionId, CancellationToken ct)
    {
        var receipt = await GetReceiptAsync(receiptId, ct);
        await GetTransactionAsync(transactionId, ct);

        var (sets, _) = await CandidatesAsync([receipt], DefaultWindow, ct);
        var suggested = sets[receipt.Id].Candidates.FirstOrDefault(c => c.TransactionId == transactionId);
        var (confidence, note) = suggested is null
            ? ("manual", "chosen by hand, not a suggested match")
            : (suggested.Confidence, suggested.Note);

        var linked = await LinkAsync(receipt, transactionId, MatchMethod.Manual, confidence, note, ct);

        // Confirming settles it: a stale rejection of this pair shouldn't linger.
        await db.MatchRejections.Where(m => m.ReceiptId == receiptId && m.TransactionId == transactionId).ExecuteDeleteAsync(ct);
        if (receipt.NoBankMatch)
            await db.Receipts.Where(r => r.Id == receiptId).ExecuteUpdateAsync(u => u.SetProperty(r => r.NoBankMatch, false), ct);

        return linked;
    }

    /// <summary>
    /// Remember that this pair is wrong. Idempotent. Rejecting what's linked
    /// unlinks it. Ruling one out often leaves a confident survivor, which is
    /// linked straight away.
    /// </summary>
    public async Task RejectAsync(long receiptId, long transactionId, string note, CancellationToken ct)
    {
        var receipt = await GetReceiptAsync(receiptId, ct);
        var txn = await GetTransactionAsync(transactionId, ct);

        if (txn.ReceiptId == receiptId) await UnmatchAsync(receiptId, ct);

        if (!await db.MatchRejections.AnyAsync(m => m.ReceiptId == receiptId && m.TransactionId == transactionId, ct))
        {
            db.MatchRejections.Add(new MatchRejection
            {
                ReceiptId = receiptId,
                TransactionId = transactionId,
                Note = note.Length > 255 ? note[..255] : note,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A retry beat us to it (unique pair). Same outcome.
                db.ChangeTracker.Clear();
            }
        }

        await AutoLinkReceiptAsync(await GetReceiptAsync(receiptId, ct), ct);
    }

    /// <summary>Forget a rejection. Returns whether there was one. May relink straight away.</summary>
    public async Task<bool> UnrejectAsync(long receiptId, long transactionId, CancellationToken ct)
    {
        var receipt = await GetReceiptAsync(receiptId, ct);
        await GetTransactionAsync(transactionId, ct);

        var removed = await db.MatchRejections
            .Where(m => m.ReceiptId == receiptId && m.TransactionId == transactionId)
            .ExecuteDeleteAsync(ct);
        if (removed > 0) await AutoLinkReceiptAsync(receipt, ct);
        return removed > 0;
    }

    /// <summary>Cash, or a card that isn't imported: out of the queue for good.</summary>
    public async Task MarkNoMatchAsync(long receiptId, string note, CancellationToken ct)
    {
        await GetReceiptAsync(receiptId, ct);
        if (await db.BankTransactions.AnyAsync(t => t.ReceiptId == receiptId, ct))
            throw AlreadyLinked($"Receipt #{receiptId} is linked to a transaction. Unmatch it first.", null, receiptId);

        var trimmed = note.Length > 255 ? note[..255] : note;
        if (trimmed.Length > 0)
            await db.Receipts.Where(r => r.Id == receiptId).ExecuteUpdateAsync(u => u
                .SetProperty(r => r.NoBankMatch, true).SetProperty(r => r.ReviewNote, trimmed), ct);
        else
            await db.Receipts.Where(r => r.Id == receiptId).ExecuteUpdateAsync(u => u.SetProperty(r => r.NoBankMatch, true), ct);
    }

    public async Task ClearNoMatchAsync(long receiptId, CancellationToken ct)
    {
        await GetReceiptAsync(receiptId, ct);
        await db.Receipts.Where(r => r.Id == receiptId).ExecuteUpdateAsync(u => u.SetProperty(r => r.NoBankMatch, false), ct);
    }

    /// <summary>Undo a link, however it was made. Returns the transaction id.</summary>
    public async Task<long> UnmatchAsync(long receiptId, CancellationToken ct)
    {
        await GetReceiptAsync(receiptId, ct);
        var txnId = await db.BankTransactions.Where(t => t.ReceiptId == receiptId).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct)
            ?? throw new ApiException(StatusCodes.Status409Conflict, "not_linked", $"Receipt #{receiptId} isn't linked to anything.",
                extra: new Dictionary<string, object?> { ["receipt_id"] = receiptId });

        await db.BankTransactions.Where(t => t.Id == txnId).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.ReceiptId, (long?)null)
            .SetProperty(t => t.MatchedAt, (DateTime?)null)
            .SetProperty(t => t.MatchMethod, "")
            .SetProperty(t => t.MatchConfidence, "")
            .SetProperty(t => t.MatchNote, ""), ct);
        return txnId;
    }

    // --- auto-linking ------------------------------------------------------------------------

    /// <summary>
    /// Link every receipt with one confident candidate; report the rest. Runs
    /// on receipt upload and again after a statement import, since the
    /// receipt usually arrives first.
    /// </summary>
    public async Task<AutoLinkResult> AutoLinkAsync(IReadOnlyList<Receipt>? receipts, int window, DateOnly? since, DateOnly? until, bool dryRun, CancellationToken ct)
    {
        receipts ??= await UnmatchedReceipts(since, until).ToListAsync(ct);
        var result = new AutoLinkResult();
        if (receipts.Count == 0) return result;

        var (sets, _) = await CandidatesAsync(receipts, window, ct);

        // Two receipts of the same amount on the same day can both see one
        // transaction. Whichever comes first takes it.
        var claimed = new HashSet<long>();
        foreach (var receipt in receipts)
        {
            var match = sets[receipt.Id].Without(claimed);
            switch (match.Bucket)
            {
                case CandidateSet.NoCandidate: result.Nothing.Add(receipt.Id); continue;
                case CandidateSet.Ambiguous: result.Ambiguous.Add(receipt.Id); continue;
                case CandidateSet.Weak: result.Weak.Add(receipt.Id); continue;
            }

            var best = match.Best!;
            claimed.Add(best.TransactionId);
            if (!dryRun)
            {
                try
                {
                    await LinkAsync(receipt, best.TransactionId, MatchMethod.Auto, best.Confidence, best.Note, ct);
                }
                catch (ApiException ex) when (ex.Code == "match_unavailable")
                {
                    // Claimed elsewhere mid-run: it needs a look next time.
                    result.Ambiguous.Add(receipt.Id);
                    continue;
                }
            }
            result.Linked.Add((receipt.Id, best));
        }
        return result;
    }

    public Task<AutoLinkResult> AutoLinkReceiptAsync(Receipt receipt, CancellationToken ct) =>
        receipt.NoBankMatch
            ? Task.FromResult(new AutoLinkResult())
            : AutoLinkAsync([receipt], DefaultWindow, null, null, dryRun: false, ct);

    /// <summary>After an import: receipts that could belong to the account's date span, padded by the window.</summary>
    public async Task<AutoLinkResult> AutoLinkForAccountAsync(long accountId, CancellationToken ct)
    {
        var rows = db.BankTransactions.Where(t => t.AccountId == accountId);
        var first = await rows.OrderBy(t => t.PostedOn).Select(t => (DateOnly?)t.PostedOn).FirstOrDefaultAsync(ct);
        var last = await rows.OrderByDescending(t => t.PostedOn).Select(t => (DateOnly?)t.PostedOn).FirstOrDefaultAsync(ct);
        if (first is null || last is null) return new AutoLinkResult();

        return await AutoLinkAsync(null, DefaultWindow,
            first.Value.AddDays(-DefaultWindow), last.Value.AddDays(DefaultWindow), dryRun: false, ct);
    }

    /// <summary>
    /// "bank_match" for a receipt that was just stored. Never fails the
    /// upload: the receipt is already saved, and anything uncertain is left
    /// for the queue.
    /// </summary>
    public async Task<BankMatchRead?> TryAutoMatchAsync(Receipt receipt, CancellationToken ct)
    {
        try
        {
            var result = await AutoLinkReceiptAsync(receipt, ct);
            if (result.Linked.Count == 0) return null;
            var (_, candidate) = result.Linked[0];
            var memo = await db.BankTransactions.Where(t => t.Id == candidate.TransactionId).Select(t => t.Memo).FirstAsync(ct);
            return new BankMatchRead(candidate.TransactionId, memo, candidate.Confidence, candidate.Note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Auto-match failed for receipt {Id}; the receipt itself was stored.", receipt.Id);
            return null;
        }
    }

    // --- helpers -------------------------------------------------------------------------------

    public async Task<BankTransaction> LoadTransactionAsync(long id, CancellationToken ct) =>
        await db.BankTransactions.AsNoTracking()
            .Include(t => t.Account)
            .Include(t => t.Category!).ThenInclude(c => c.Parent)
            .FirstAsync(t => t.Id == id, ct);

    private async Task<Receipt> GetReceiptAsync(long id, CancellationToken ct) =>
        await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw NotFound($"No receipt #{id}.", "receipt_id", id);

    private async Task<BankTransaction> GetTransactionAsync(long id, CancellationToken ct) =>
        await db.BankTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
        ?? throw NotFound($"No transaction #{id}.", "transaction_id", id);

    private static ApiException NotFound(string message, string key, long id) =>
        new(StatusCodes.Status404NotFound, "not_found", message, extra: new Dictionary<string, object?> { [key] = id });

    /// <summary>409 match_unavailable, with the ids so the client can refresh those rows.</summary>
    private static ApiException AlreadyLinked(string message, long? transactionId, long? receiptId)
    {
        var extra = new Dictionary<string, object?>();
        if (transactionId is not null) extra["transaction_id"] = transactionId;
        if (receiptId is not null) extra["receipt_id"] = receiptId;
        return ApiException.Conflict("match_unavailable", message, extra);
    }
}
