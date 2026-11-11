using System.Text.Json;
using ReceiptScanner.Core.Domain;
using ReceiptScanner.Core.Receipts;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Api.Scans;

// Port of scan_views.describe_scan. One shape for every scan in every endpoint.

public sealed record StoredRead(string Format, uint? Width, uint? Height, double? Megapixels, uint Bytes, bool Thumbnail);

public record ExtractionRead(
    JsonElement? Proposed,
    ProposedTotals.Totals? Totals,
    string? Check,
    JsonElement Notes,
    string? Error,
    int Attempts,
    double? Seconds,
    DateTime? StartedAt,
    DateTime? ExtractedAt);

/// <summary>The detail endpoint adds the OCR text the model read.</summary>
public sealed record ExtractionDetailRead(
    JsonElement? Proposed,
    ProposedTotals.Totals? Totals,
    string? Check,
    JsonElement Notes,
    string? Error,
    int Attempts,
    double? Seconds,
    DateTime? StartedAt,
    DateTime? ExtractedAt,
    string? OcrText);

public sealed record ActionsRead(bool Approve, bool Reject, bool Rerun);

public sealed record ScanRead(
    long ScanId,
    string ScanStatus,
    Guid? ClientUuid,
    DateTime? CapturedAt,
    DateTime UploadedAt,
    StoredRead Stored,
    string? ImageUrl,
    string? ThumbnailUrl,
    long? ReceiptId,
    object Extraction,          // ExtractionRead or ExtractionDetailRead, serialised as its runtime type
    DateTime? ReviewedAt,
    ActionsRead Actions);

public static class ScanViews
{
    private static readonly JsonElement EmptyList = JsonDocument.Parse("[]").RootElement.Clone();

    public static ScanRead Describe(ReceiptScan s, HttpRequest request, DateTime utcNow, TimeSpan staleAfter, bool includeText = false)
    {
        var proposed = ParseJson(s.Extraction);
        var totals = proposed is { ValueKind: JsonValueKind.Object } p ? ProposedTotals.From(p) : null;
        var notes = ParseJson(s.ExtractionNotes) is { ValueKind: JsonValueKind.Array } n ? n : EmptyList;
        var check = string.IsNullOrEmpty(s.ExtractionCheck) ? null : s.ExtractionCheck;
        var error = string.IsNullOrEmpty(s.ExtractionError) ? null : s.ExtractionError;

        object extraction = includeText
            ? new ExtractionDetailRead(proposed, totals, check, notes, error, s.ExtractionAttempts, s.ExtractionSeconds,
                s.ExtractionStartedAt, s.ExtractedAt, string.IsNullOrEmpty(s.ExtractionText) ? null : s.ExtractionText)
            : new ExtractionRead(proposed, totals, check, notes, error, s.ExtractionAttempts, s.ExtractionSeconds,
                s.ExtractionStartedAt, s.ExtractedAt);

        var basePath = $"{request.PathBase}/api/scans/{s.Id}";
        return new ScanRead(
            s.Id,
            s.Status,
            s.ClientUuid,
            s.CapturedAt,
            s.UploadedAt,
            new StoredRead(s.ImageFormat, s.Width, s.Height, s.Megapixels, s.ByteSize, !string.IsNullOrEmpty(s.Thumbnail)),
            // Relative, as Django's reverse() gives them. Both need the token.
            string.IsNullOrEmpty(s.Image) ? null : $"{basePath}/image/",
            string.IsNullOrEmpty(s.Thumbnail) ? null : $"{basePath}/thumbnail/",
            s.ReceiptId,
            extraction,
            s.ReviewedAt,
            new ActionsRead(
                Approve: s.Status == ScanStatus.Pending,
                Reject: s.Status == ScanStatus.Pending,
                Rerun: s.CanRerun(utcNow, staleAfter)));
    }

    /// <summary>A stored JSON column as an element, or null when empty or unreadable.</summary>
    public static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Null ? null : doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
