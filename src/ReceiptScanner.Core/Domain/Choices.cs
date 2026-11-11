namespace ReceiptScanner.Core.Domain;

// String values stored in the database by the Django app. Kept as constants
// (not C# enums) because the columns are varchar and the values are part of
// the API contract.

/// <summary>Lifecycle of a <c>receipt_scans</c> row. See receipts/models.py.</summary>
/// <remarks>
/// queued → processing → pending → accepted (approve: Receipt created)
///                 |            \→ rejected (extraction cleared)
///                 \→ failed
/// rejected / failed (or a stale processing job) → rerun → queued.
/// </remarks>
public static class ScanStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Discarded = "discarded";

    public static readonly IReadOnlyList<string> All =
        [Queued, Processing, Pending, Accepted, Rejected, Failed, Discarded];

    /// <summary>States from which extraction may be queued again.</summary>
    public static readonly IReadOnlyList<string> Rerunnable = [Queued, Rejected, Failed];
}

/// <summary><c>extraction_check</c> values. See receipts/extraction.py.</summary>
public static class ExtractionCheck
{
    public const string Ok = "ok";
    public const string Retried = "retried";
    public const string Check = "check";
    public const string Mismatch = "mismatch";
}

/// <summary>Who set a row's category. See categories/models.py.</summary>
public static class CategorySource
{
    public const string User = "user";
    public const string Rule = "rule";
    public const string Match = "match";
    public const string Model = "model";
    public const string Pair = "pair";
}

public static class CategoryKind
{
    public const string Expense = "expense";
    public const string Income = "income";
    public const string Transfer = "transfer";
}

/// <summary>Per-account dedupe strategy. See banking/identity.py.</summary>
public static class DedupeStrategy
{
    public const string Fitid = "fitid";
    public const string Content = "content";
}

public static class MatchMethod
{
    public const string Auto = "auto";
    public const string Manual = "manual";
}

public static class ConflictResolution
{
    public const string KeepStored = "kept_stored";
    public const string ApplyIncoming = "applied_incoming";
}
