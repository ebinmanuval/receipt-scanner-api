using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Core.Banking;
using ReceiptScanner.Core.Domain;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;
using ReceiptScanner.Infrastructure.Storage;

namespace ReceiptScanner.Api.Banking;

/// <summary>These exact bytes were imported before.</summary>
public sealed class DuplicateStatementException(OfxStatement existing)
    : Exception($"Already imported on {existing.CreatedAt:yyyy-MM-dd} as statement #{existing.Id}.")
{
    public OfxStatement Existing { get; } = existing;
}

public sealed record ConflictInfo(string Fitid, List<string> Fields, Dictionary<string, string> Stored, Dictionary<string, string> Incoming);

public sealed class ImportResult
{
    public BankAccount? Account { get; set; }
    public OfxStatement? Statement { get; set; }
    public ParsedStatement? Parsed { get; set; }
    public int Created { get; set; }
    public int Duplicates { get; set; }
    public int SkippedUnparseable { get; set; }
    public List<ConflictInfo> Conflicts { get; } = [];
    public bool AccountCreated { get; set; }
    public bool DryRun { get; set; }
    /// <summary>The file's FITIDs look like row numbers and the account still keys on them. Advisory.</summary>
    public bool UnstableFitid { get; set; }
    public AutoLinkResult? AutoMatched { get; set; }
    public PairResult? TransfersPaired { get; set; }

    public int TotalInFile => Created + Duplicates + Conflicts.Count + SkippedUnparseable;

    /// <summary>importer.ImportResult.as_dict, in the same key order.</summary>
    public Dictionary<string, object?> AsDict() => new()
    {
        ["statement_id"] = Statement?.Id,
        ["account_id"] = Account?.Id,
        ["account"] = Account?.ToString(),
        ["period"] = new Dictionary<string, string?>
        {
            ["start"] = Parsed?.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["end"] = Parsed?.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        },
        ["transactions_in_file"] = TotalInFile,
        ["created"] = Created,
        ["duplicates_skipped"] = Duplicates,
        ["unparseable_skipped"] = SkippedUnparseable,
        ["conflicts"] = Conflicts.Select(c => new Dictionary<string, object>
        {
            ["fitid"] = c.Fitid, ["fields"] = c.Fields, ["stored"] = c.Stored, ["incoming"] = c.Incoming,
        }).ToList(),
        ["account_created"] = AccountCreated,
        ["dry_run"] = DryRun,
        ["unstable_fitid"] = UnstableFitid,
        ["auto_matched"] = AutoMatched?.AsDict(),
        ["transfers_paired"] = TransfersPaired?.AsDict(),
    };
}

/// <summary>
/// Port of banking/importer.py. Two levels of de-duplication: the file
/// (sha256 of the bytes) and the transaction ((account, dedupe_key), see
/// <see cref="Identity"/>). A key already present is an overlap and skipped;
/// if the incoming copy disagrees with what's stored, that's recorded as a
/// conflict and the stored row is left untouched. Everything runs in one
/// transaction, including auto-linking and repayment pairing, so a failure
/// rolls the whole import back and a dry run simply rolls back at the end.
/// </summary>
public sealed class OfxImporter(
    ReceiptScannerDbContext db,
    IMediaStorage media,
    Reconciler reconciler,
    TransferPairing pairing,
    ILogger<OfxImporter> logger)
{
    private const int Chunk = 500;

    /// <exception cref="DuplicateStatementException">These bytes were imported before (unless <paramref name="allowDuplicateFile"/>).</exception>
    /// <exception cref="OfxException">The file can't be parsed.</exception>
    public async Task<ImportResult> ImportAsync(byte[] data, string filename, bool dryRun, bool allowDuplicateFile,
        string nickname, CancellationToken ct, bool storeFile = true)
    {
        var digest = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!allowDuplicateFile
            && await db.OfxStatements.AsNoTracking().FirstOrDefaultAsync(s => s.FileHash == digest, ct) is { } existing)
            throw new DuplicateStatementException(existing);

        var parsed = OfxParser.Parse(data);
        var result = new ImportResult { Parsed = parsed, DryRun = dryRun, SkippedUnparseable = parsed.Skipped };

        string? storedFile = null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.BankId == parsed.BankId && a.AccountId == parsed.AccountId, ct);
            if (account is null)
            {
                account = new BankAccount
                {
                    BankId = Cut(parsed.BankId, 32),
                    AccountId = Cut(parsed.AccountId, 64),
                    AccountType = Cut(parsed.AccountType, 32),
                    IsCreditCard = parsed.IsCreditCard,
                    Currency = Cut(parsed.Currency, 3),
                    Nickname = Cut(nickname, 100),
                };
                db.BankAccounts.Add(account);
                result.AccountCreated = true;
            }
            // Accounts learn they're a card from their next card statement; never cleared automatically.
            if (parsed.IsCreditCard && !account.IsCreditCard) account.IsCreditCard = true;
            if (nickname.Length > 0 && account.Nickname.Length == 0) account.Nickname = Cut(nickname, 100);
            await db.SaveChangesAsync(ct);
            result.Account = account;

            var statement = new OfxStatement
            {
                AccountId = account.Id,
                StartDate = parsed.StartDate,
                EndDate = parsed.EndDate,
                ServerDate = parsed.ServerDate,
                LedgerBalance = parsed.LedgerBalance,
                LedgerBalanceAt = parsed.LedgerBalanceAt,
                AvailableBalance = parsed.AvailableBalance,
                OriginalFilename = Cut(filename, 255),
                FileHash = digest,
                TransactionsInFile = (uint)(parsed.Transactions.Count + parsed.Skipped),
            };
            db.OfxStatements.Add(statement);
            await db.SaveChangesAsync(ct);

            // Not on a dry run: storage isn't part of the transaction.
            if (storeFile && data.Length > 0 && !dryRun)
            {
                var name = MediaPaths.OfxFile(parsed.EndDate, string.IsNullOrEmpty(filename) ? "statement.ofx" : filename);
                try
                {
                    await media.SaveAsync(name, data, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Could not store an OFX file. Check Media:Root permissions.");
                    throw new ApiException(StatusCodes.Status503ServiceUnavailable, "storage_unavailable",
                        "The server could not store the statement file. This is a server configuration problem, not a problem with your file.");
                }
                storedFile = name;
                statement.File = name;
            }
            if (!dryRun) result.Statement = statement;

            var keyed = await AssignKeysAsync(parsed.Transactions, account, ct);

            if (account.DedupeStrategy == DedupeStrategy.Fitid && Identity.LooksLikeOrdinalFitid(parsed.Transactions))
                result.UnstableFitid = true;   // say so, but never switch the strategy on a heuristic

            var existingByKey = await LoadByKeysAsync(account.Id, keyed.Select(k => k.Key).Distinct().ToList(), ct);
            var addedKeys = new HashSet<string>();
            var toCreate = new List<BankTransaction>();

            foreach (var (key, entry) in keyed)
            {
                if (!existingByKey.TryGetValue(key, out var stored))
                {
                    // A FITID repeated within one file is one transaction, not a crash on the unique index.
                    if (!addedKeys.Add(key)) { result.Duplicates++; continue; }
                    toCreate.Add(new BankTransaction
                    {
                        AccountId = account.Id,
                        StatementId = statement.Id,
                        DedupeKey = key,
                        Fitid = entry.Fitid,
                        PostedOn = entry.PostedOn,
                        UserDate = entry.UserDate,
                        Amount = entry.Amount,
                        Trntype = entry.Trntype,
                        Memo = entry.Memo,
                        Name = entry.Name,
                        Checknum = entry.Checknum,
                    });
                    continue;
                }

                var changed = DiffersFrom(stored, entry);
                if (changed.Count == 0)
                {
                    result.Duplicates++;
                    continue;
                }

                var storedValues = Snapshot(stored, changed);
                var incomingValues = Snapshot(entry, changed);
                result.Conflicts.Add(new ConflictInfo(entry.Fitid, changed, storedValues, incomingValues));
                await RecordConflictAsync(stored.Id, statement.Id, changed, storedValues, incomingValues, ct);
            }

            db.BankTransactions.AddRange(toCreate);
            result.Created = toCreate.Count;

            statement.TransactionsCreated = (uint)result.Created;
            statement.TransactionsDuplicate = (uint)result.Duplicates;
            statement.TransactionsConflicted = (uint)result.Conflicts.Count;
            await db.SaveChangesAsync(ct);

            if (dryRun)
            {
                await tx.RollbackAsync(ct);
                return result;
            }

            // The other half of auto-matching: receipts usually arrive before
            // the statement. Then card repayments over this statement's dates.
            if (result.Created > 0)
            {
                result.AutoMatched = await reconciler.AutoLinkForAccountAsync(account.Id, ct);
                var days = pairing.WindowDays;
                result.TransfersPaired = await pairing.PairAsync(
                    parsed.StartDate?.AddDays(-days), parsed.EndDate?.AddDays(days), dryRun: false, ct);
            }

            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            if (storedFile is not null)
            {
                try { media.Delete(storedFile); } catch (Exception ex) { logger.LogWarning(ex, "Could not remove {File}", storedFile); }
            }
            throw;
        }
    }

    /// <summary>
    /// Each parsed transaction with its dedupe key, in file order. A FITID,
    /// or a content key (signature#occurrence) under the content strategy or
    /// when the row has no FITID. Content keys are count-aware: a second
    /// genuine identical payment gets #2, not swallowed as a duplicate, and
    /// occupied slots are matched first so re-imports are recognised.
    /// </summary>
    private async Task<List<(string Key, ParsedTransaction Entry)>> AssignKeysAsync(
        IReadOnlyList<ParsedTransaction> transactions, BankAccount account, CancellationToken ct)
    {
        var contentOnly = account.DedupeStrategy == DedupeStrategy.Content;
        var keys = new string[transactions.Count];
        var bySignature = new Dictionary<string, List<int>>();

        for (var i = 0; i < transactions.Count; i++)
        {
            var t = transactions[i];
            if (t.Fitid.Length > 0 && !contentOnly) keys[i] = t.Fitid;
            else
            {
                var signature = Identity.Signature(t.PostedOn, t.Amount, t.Memo);
                if (!bySignature.TryGetValue(signature, out var list)) bySignature[signature] = list = [];
                list.Add(i);
            }
        }

        if (bySignature.Count > 0)
        {
            var candidates = bySignature
                .SelectMany(g => Enumerable.Range(1, g.Value.Count).Select(n => $"{g.Key}#{n}"))
                .ToList();
            var taken = (await LoadByKeysAsync(account.Id, candidates, ct)).Keys.ToHashSet();

            foreach (var (signature, indices) in bySignature)
            {
                var slots = Enumerable.Range(1, indices.Count).Select(n => $"{signature}#{n}").ToList();
                var ordered = slots.Where(taken.Contains).Concat(slots.Where(s => !taken.Contains(s))).ToList();
                for (var j = 0; j < indices.Count; j++) keys[indices[j]] = ordered[j];
            }
        }

        return transactions.Select((t, i) => (keys[i], t)).ToList();
    }

    private async Task<Dictionary<string, BankTransaction>> LoadByKeysAsync(long accountId, List<string> keys, CancellationToken ct)
    {
        var found = new Dictionary<string, BankTransaction>();
        foreach (var chunk in keys.Chunk(Chunk))
        {
            var list = chunk.ToList();
            foreach (var t in await db.BankTransactions.AsNoTracking()
                         .Where(t => t.AccountId == accountId && list.Contains(t.DedupeKey)).ToListAsync(ct))
                found[t.DedupeKey] = t;
        }
        return found;
    }

    /// <summary>One open conflict per transaction: re-importing the same disagreeing file updates it.</summary>
    private async Task RecordConflictAsync(long transactionId, long statementId, List<string> fields,
        Dictionary<string, string> stored, Dictionary<string, string> incoming, CancellationToken ct)
    {
        var open = await db.TransactionConflicts.FirstOrDefaultAsync(c => c.TransactionId == transactionId && c.ResolvedAt == null, ct);
        if (open is null)
        {
            open = new TransactionConflict { TransactionId = transactionId };
            db.TransactionConflicts.Add(open);
        }
        open.StatementId = statementId;
        open.Fields = JsonSerializer.Serialize(fields);
        open.Stored = JsonSerializer.Serialize(stored);
        open.Incoming = JsonSerializer.Serialize(incoming);
    }

    /// <summary>BankTransaction.differs_from: fields where an incoming duplicate disagrees. Memo padding isn't a change.</summary>
    internal static List<string> DiffersFrom(BankTransaction stored, ParsedTransaction incoming)
    {
        var changed = new List<string>();
        if (stored.PostedOn != incoming.PostedOn) changed.Add("posted_on");
        if (stored.Amount != incoming.Amount) changed.Add("amount");
        if (Identity.NormaliseMemo(stored.Memo) != Identity.NormaliseMemo(incoming.Memo)) changed.Add("memo");
        if (stored.Trntype != incoming.Trntype) changed.Add("trntype");
        if (stored.UserDate != incoming.UserDate) changed.Add("user_date");
        return changed;
    }

    private static Dictionary<string, string> Snapshot(BankTransaction t, List<string> fields) =>
        fields.ToDictionary(f => f, f => f switch
        {
            "posted_on" => Iso(t.PostedOn),
            "amount" => Money(t.Amount),
            "memo" => t.Memo,
            "trntype" => t.Trntype,
            _ => t.UserDate is { } d ? Iso(d) : "None",
        });

    private static Dictionary<string, string> Snapshot(ParsedTransaction t, List<string> fields) =>
        fields.ToDictionary(f => f, f => f switch
        {
            "posted_on" => Iso(t.PostedOn),
            "amount" => Money(t.Amount),
            "memo" => t.Memo,
            "trntype" => t.Trntype,
            _ => t.UserDate is { } d ? Iso(d) : "None",   // Python's str(None)
        });

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Money(decimal m) => decimal.Round(m, 2).ToString("F2", CultureInfo.InvariantCulture);
    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}
