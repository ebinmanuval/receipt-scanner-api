using ReceiptScanner.Core.Domain;

namespace ReceiptScanner.Infrastructure.Data.Entities;

// Mapped to the Django tables in banking/models.py.

/// <summary>Table <c>bank_accounts</c>. Unique on (bank_id, account_id).</summary>
public class BankAccount
{
    public long Id { get; set; }
    /// <summary>OFX BANKID — the BSB or routing number.</summary>
    public string BankId { get; set; } = "";
    /// <summary>OFX ACCTID. Never render in full; use <see cref="MaskedAccountId"/>.</summary>
    public string AccountId { get; set; } = "";
    public string AccountType { get; set; } = "";
    public string Currency { get; set; } = "AUD";
    public string Nickname { get; set; } = "";
    public bool IsCreditCard { get; set; }
    public string DedupeStrategy { get; set; } = Core.Domain.DedupeStrategy.Fitid;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<OfxStatement> Statements { get; set; } = [];
    public List<BankTransaction> Transactions { get; set; } = [];

    public string MaskedAccountId
    {
        get
        {
            var value = AccountId ?? "";
            if (value.Length <= 4) return new string('*', value.Length);
            return value[..2] + new string('*', value.Length - 4) + value[^2..];
        }
    }

    public override string ToString() =>
        string.IsNullOrEmpty(Nickname) ? $"{BankId}/{MaskedAccountId}" : Nickname;
}

/// <summary>Table <c>ofx_statements</c>: one uploaded OFX file, kept as an audit record.</summary>
public class OfxStatement
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public BankAccount Account { get; set; } = null!;

    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTime? ServerDate { get; set; }

    public decimal? LedgerBalance { get; set; }
    public DateTime? LedgerBalanceAt { get; set; }
    public decimal? AvailableBalance { get; set; }

    /// <summary>Path relative to MEDIA_ROOT, e.g. <c>ofx/2026/09/&lt;hex&gt;.ofx</c>.</summary>
    public string? File { get; set; }
    public string OriginalFilename { get; set; } = "";
    /// <summary>sha256 of the raw bytes. Indexed, deliberately not unique.</summary>
    public string FileHash { get; set; } = "";

    public uint TransactionsInFile { get; set; }
    public uint TransactionsCreated { get; set; }
    public uint TransactionsDuplicate { get; set; }
    public uint TransactionsConflicted { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<BankTransaction> Transactions { get; set; } = [];
    public List<TransactionConflict> Conflicts { get; set; } = [];
}

/// <summary>Table <c>bank_transactions</c>: one STMTTRN, unique per account by dedupe_key.</summary>
public class BankTransaction
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public BankAccount Account { get; set; } = null!;
    public long? StatementId { get; set; }
    public OfxStatement? Statement { get; set; }

    public string Fitid { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public string Trntype { get; set; } = "";
    public DateOnly PostedOn { get; set; }
    public DateOnly? UserDate { get; set; }
    /// <summary>Negative for money out.</summary>
    public decimal Amount { get; set; }
    public string Memo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Checknum { get; set; } = "";

    // --- reconciliation (one-to-one with a receipt) -----------------------
    public long? ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }
    public DateTime? MatchedAt { get; set; }
    public string MatchMethod { get; set; } = "";
    public string MatchConfidence { get; set; } = "";
    public string MatchNote { get; set; } = "";

    // --- transfers between your own accounts ------------------------------
    public long? TransferPairId { get; set; }
    public BankTransaction? TransferPair { get; set; }
    public DateTime? PairedAt { get; set; }

    // --- category -----------------------------------------------------------
    public long? CategoryId { get; set; }
    public Category? Category { get; set; }
    public string CategorySource { get; set; } = "";
    public long? CategoryRuleId { get; set; }
    public CategoryRule? CategoryRule { get; set; }
    public double? CategoryConfidence { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<MatchRejection> MatchRejections { get; set; } = [];
    public List<TransactionConflict> Conflicts { get; set; } = [];

    public bool IsDebit => Amount < 0;
}

/// <summary>Table <c>receipt_match_rejections</c>: "this receipt is not that transaction".</summary>
public class MatchRejection
{
    public long Id { get; set; }
    public long ReceiptId { get; set; }
    public Receipt Receipt { get; set; } = null!;
    public long TransactionId { get; set; }
    public BankTransaction Transaction { get; set; } = null!;
    public DateTime RejectedAt { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Table <c>bank_transaction_conflicts</c>: a re-import disagreed with a stored row.</summary>
public class TransactionConflict
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public BankTransaction Transaction { get; set; } = null!;
    public long? StatementId { get; set; }
    public OfxStatement? Statement { get; set; }

    /// <summary>JSON list of the field names that disagreed.</summary>
    public string Fields { get; set; } = "[]";
    /// <summary>JSON object of the stored values.</summary>
    public string Stored { get; set; } = "{}";
    /// <summary>JSON object of the incoming values.</summary>
    public string Incoming { get; set; } = "{}";

    public DateTime DetectedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string Resolution { get; set; } = "";
    public string Note { get; set; } = "";

    public bool IsOpen => ResolvedAt is null;
}
