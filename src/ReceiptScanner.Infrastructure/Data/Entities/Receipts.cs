using ReceiptScanner.Core.Domain;

namespace ReceiptScanner.Infrastructure.Data.Entities;

// Mapped to the Django tables in receipts/models.py. Column names come from
// the snake_case convention in ReceiptScannerDbContext; table names, types and
// indexes from Configurations/.

/// <summary>Table <c>receipts</c>.</summary>
public class Receipt
{
    public long Id { get; set; }
    public string Shop { get; set; } = "";
    public decimal Total { get; set; }
    public DateOnly PurchasedOn { get; set; }

    /// <summary>Idempotency key from the iOS app. char(32), unique.</summary>
    public Guid? ClientUuid { get; set; }

    /// <summary>Path relative to MEDIA_ROOT, e.g. <c>receipts/2026/08/&lt;hex&gt;.jpg</c>.</summary>
    public string? Image { get; set; }

    public DateTime? SheetSyncedAt { get; set; }
    public bool NeedsReview { get; set; }
    public string ReviewNote { get; set; } = "";
    public bool NoBankMatch { get; set; }

    public long? CategoryId { get; set; }
    public Category? Category { get; set; }
    public string CategorySource { get; set; } = "";
    public long? CategoryRuleId { get; set; }
    public CategoryRule? CategoryRule { get; set; }
    public double? CategoryConfidence { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<ReceiptItem> Items { get; set; } = [];
    public List<ReceiptScan> Scans { get; set; } = [];
    public BankTransaction? BankTransaction { get; set; }
    public List<MatchRejection> MatchRejections { get; set; } = [];

    /// <summary>Sum of the line totals; should reconcile with <see cref="Total"/>. Items must be loaded.</summary>
    public decimal ItemsTotal => Items.Sum(i => i.LineTotal);

    /// <summary>Total − items total. Positive means items are missing.</summary>
    public decimal Discrepancy => decimal.Round(Total - ItemsTotal, 2, MidpointRounding.ToEven);
}

/// <summary>Table <c>receipt_items</c>. <see cref="LineTotal"/> is the whole line, not a unit price.</summary>
public class ReceiptItem
{
    public long Id { get; set; }
    public long ReceiptId { get; set; }
    public Receipt Receipt { get; set; } = null!;
    public string Name { get; set; } = "";
    public decimal Qty { get; set; } = 1m;
    public decimal LineTotal { get; set; }

    public long? CategoryId { get; set; }
    public Category? Category { get; set; }
    public string CategorySource { get; set; } = "";

    /// <summary>Cost per unit, 4 places. Null when qty is zero.</summary>
    public decimal? UnitPrice =>
        Qty == 0 ? null : decimal.Round(LineTotal / Qty, 4, MidpointRounding.ToEven);
}

/// <summary>Table <c>receipt_scans</c>: a photo stored before anyone knows what's on it.</summary>
public class ReceiptScan
{
    public long Id { get; set; }

    /// <summary>The original upload, untouched. Path relative to MEDIA_ROOT.</summary>
    public string Image { get; set; } = "";
    /// <summary>Derived JPEG preview. May be absent.</summary>
    public string? Thumbnail { get; set; }

    /// <summary>sha256 of the uploaded bytes. Indexed, not unique.</summary>
    public string ContentHash { get; set; } = "";
    public Guid? ClientUuid { get; set; }

    public DateTime? CapturedAt { get; set; }
    public DateTime UploadedAt { get; set; }
    public int? UploadedById { get; set; }
    public User? UploadedBy { get; set; }

    public string Status { get; set; } = ScanStatus.Queued;
    public long? ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }

    public string OriginalFilename { get; set; } = "";
    public string MimeType { get; set; } = "";
    public uint ByteSize { get; set; }
    public uint? Width { get; set; }
    public uint? Height { get; set; }
    public string ImageFormat { get; set; } = "";
    public string Note { get; set; } = "";

    // --- Extraction ---------------------------------------------------------
    /// <summary>Proposed receipt as raw JSON (shop, date, total, items). Null until extracted.</summary>
    public string? Extraction { get; set; }
    public string ExtractionCheck { get; set; } = "";
    /// <summary>JSON list of reviewer warnings. Never null; "[]" when empty.</summary>
    public string ExtractionNotes { get; set; } = "[]";
    public string ExtractionText { get; set; } = "";
    public string ExtractionError { get; set; } = "";
    public ushort ExtractionAttempts { get; set; }
    public double? ExtractionSeconds { get; set; }
    public DateTime? ExtractionStartedAt { get; set; }
    public DateTime? ExtractedAt { get; set; }
    public string ExtractionTaskId { get; set; } = "";

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedById { get; set; }
    public User? ReviewedBy { get; set; }

    public double? Megapixels =>
        Width is { } w && Height is { } h && w > 0 && h > 0
            ? Math.Round(w * (double)h / 1_000_000, 1)
            : null;

    /// <summary>A processing job that has run far longer than any extraction (worker died).</summary>
    public bool IsStale(DateTime utcNow, TimeSpan limit) =>
        Status == ScanStatus.Processing
        && ExtractionStartedAt is { } started
        && utcNow - started > limit;

    public bool CanRerun(DateTime utcNow, TimeSpan staleLimit) =>
        ScanStatus.Rerunnable.Contains(Status) || IsStale(utcNow, staleLimit);

    /// <summary>Forget a proposed receipt (not the photo). Doesn't save.</summary>
    public void ClearExtraction()
    {
        Extraction = null;
        ExtractionCheck = "";
        ExtractionNotes = "[]";
        ExtractionText = "";
        ExtractionError = "";
        ExtractionSeconds = null;
        ExtractedAt = null;
    }
}
