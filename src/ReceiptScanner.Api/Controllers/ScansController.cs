using ReceiptScanner.Api.Banking;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReceiptScanner.Api.Auth;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Api.Http;
using ReceiptScanner.Api.Pagination;
using ReceiptScanner.Api.Receipts;
using ReceiptScanner.Api.Scans;
using ReceiptScanner.Core.Domain;
using ReceiptScanner.Core.Receipts;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;
using ReceiptScanner.Infrastructure.Extraction;
using ReceiptScanner.Infrastructure.Images;
using ReceiptScanner.Infrastructure.Storage;

namespace ReceiptScanner.Api.Controllers;

/// <summary>
/// Receipt photos: upload → background extraction → review. Port of
/// receipts/scan_views.py.
/// </summary>
/// <remarks>
/// Extraction itself is the existing Python Celery worker: an upload stores the
/// photo, queues <c>receipts.tasks.extract_scan(id)</c> on Redis, and the
/// worker writes the proposed receipt straight onto the scan row (status
/// queued → processing → pending | failed). State changes here are
/// conditional UPDATEs on the current status, so two requests (or a request
/// and the worker) can never both move the same scan.
/// </remarks>
[ApiController]
[Route("api/scans")]
public sealed partial class ScansController(
    ReceiptScannerDbContext db,
    ReceiptWriter writer,
    IImageProcessor images,
    IMediaStorage media,
    IExtractionQueue queue,
    Reconciler reconciler,
    TimeProvider clock,
    IConfiguration config,
    IOptions<ImageOptions> imageOptions,
    IOptions<ExtractionOptions> extractionOptions,
    IOptions<JsonOptions> jsonOptions,
    ILogger<ScansController> logger) : ControllerBase
{
    private static readonly string[] Editable = ["shop", "date", "total", "items"];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private TimeSpan StaleAfter => extractionOptions.Value.StaleAfter;

    // --- upload and list ------------------------------------------------------------

    /// <summary>
    /// POST /api/scans/ — store a photo exactly as sent and queue extraction.
    /// multipart: image (required), client_uuid, captured_at, note.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        if (!Request.HasFormContentType)
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type",
                "Send the photo as multipart/form-data.");

        var form = await Request.ReadFormAsync(ct);
        var upload = Uploads.Find(form.Files)
            ?? throw ApiException.BadRequest("no_file", "No image uploaded. Send it as an 'image' form field.");

        var limit = imageOptions.Value.MaxUploadBytes;
        if (upload.Length > limit)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "image_too_large",
                $"Image is {upload.Length} bytes; the limit is {limit}.");
        if (upload.Length == 0)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "validation_error", "That file is empty.");

        // Idempotency first, before touching the file: a timed-out upload is
        // retried, and the retry must not store a second copy.
        Guid? clientUuid = null;
        var uuidText = form["client_uuid"].ToString().Trim();
        if (uuidText.Length > 0)
        {
            if (!Guid.TryParse(uuidText, out var g))
                throw ApiException.Validation(new Dictionary<string, string[]> { ["client_uuid"] = ["Must be a valid UUID."] });
            clientUuid = g;

            var existing = await db.ReceiptScans.AsNoTracking().FirstOrDefaultAsync(s => s.ClientUuid == g, ct);
            if (existing is not null)
                return Ok(Envelope(Describe(existing), ("status", "success"), ("already_uploaded", true)));
        }

        var bytes = await Uploads.ReadAllAsync(upload, ct);
        var info = images.Measure(bytes)
            ?? throw new ApiException(StatusCodes.Status422UnprocessableEntity, "invalid_image",
                "That file isn't an image we can read. JPEG, PNG, HEIC and WEBP all work.");

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        // A flag, not a refusal: re-picking the same photo is plausible.
        var duplicateOf = await db.ReceiptScans.AsNoTracking()
            .Where(s => s.ContentHash == hash && s.Status != ScanStatus.Discarded)
            .OrderBy(s => s.Id).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);

        var now = Now;
        var capturedAt = ParseCapturedAt(form["captured_at"].ToString(), now, LocalTimeZone());
        var scan = new ReceiptScan
        {
            ContentHash = hash,
            ClientUuid = clientUuid,
            CapturedAt = capturedAt,
            UploadedAt = now,
            UploadedById = HttpContext.CurrentUser()?.Id,
            Status = ScanStatus.Queued,
            OriginalFilename = Truncate(upload.FileName, 255),
            MimeType = Truncate(upload.ContentType, 100),
            ByteSize = (uint)bytes.Length,
            Width = (uint)info.Width,
            Height = (uint)info.Height,
            ImageFormat = info.Format,
            Note = Truncate(form["note"].ToString(), 255),
            Image = MediaPaths.ScanImage(capturedAt, upload.FileName, now),
        };

        try
        {
            await media.SaveAsync(scan.Image, bytes, ct);
            if (images.Thumbnail(bytes) is { } thumb)
            {
                var thumbName = MediaPaths.ScanThumbnail(capturedAt, now);
                await media.SaveAsync(thumbName, thumb, ct);
                scan.Thumbnail = thumbName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A scan *is* the photo: without it there's nothing worth keeping.
            logger.LogError(ex, "Could not store a scan. Check Media:Root permissions.");
            TryDelete(scan.Image);
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "storage_unavailable",
                "The server could not store the image. This is a server configuration problem, not a problem with your upload.");
        }

        db.ReceiptScans.Add(scan);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Stored scan {Id} ({Bytes} bytes, {Format} {W}x{H}, thumbnail={Thumb})",
            scan.Id, scan.ByteSize, info.Format, info.Width, info.Height, scan.Thumbnail is not null);

        // Returns now; the worker fills in the proposal later. Poll GET /api/scans/{id}/.
        if (extractionOptions.Value.AutoExtract)
            await QueueAsync(scan, ct);

        var body = Envelope(Describe(scan), ("status", "success"), ("already_uploaded", false));
        if (duplicateOf is { } dup)
        {
            body["possible_duplicate_of"] = dup;
            body["detail"] = "An identical image is already stored. Kept anyway — discard whichever you don't want.";
        }
        return StatusCode(StatusCodes.Status201Created, body);
    }

    /// <summary>GET /api/scans/?status=pending,failed — newest first, paginated.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct)
    {
        IQueryable<ReceiptScan> scans = db.ReceiptScans.AsNoTracking();

        var wanted = (status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length > 0)
        {
            var unknown = wanted.Where(w => !ScanStatus.All.Contains(w)).Distinct().Order(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
                throw ApiException.Validation(
                    new Dictionary<string, string[]> { ["status"] = [$"Unknown: {string.Join(", ", unknown)}"] },
                    $"Unknown status {string.Join(", ", unknown)}. Use any of: {string.Join(", ", ScanStatus.All.Order(StringComparer.Ordinal))}.");
            scans = scans.Where(s => wanted.Contains(s.Status));
        }

        scans = scans
            .OrderByDescending(s => s.CapturedAt).ThenByDescending(s => s.UploadedAt).ThenByDescending(s => s.Id);
        return Ok(await Paginator.PaginateAsync(scans, s => Describe(s), Request, ct));
    }

    /// <summary>GET /api/scans/pending/ — counts for a badge. Every status key is always present.</summary>
    [HttpGet("pending")]
    public async Task<IActionResult> Pending(CancellationToken ct)
    {
        var rows = await db.ReceiptScans.AsNoTracking()
            .GroupBy(s => s.Status).Select(g => new { Status = g.Key, N = g.Count() })
            .ToListAsync(ct);

        var counts = new JsonObject();
        foreach (var key in ScanStatus.All)
            counts[key] = rows.Where(r => r.Status == key).Sum(r => r.N);

        var oldest = await db.ReceiptScans.AsNoTracking()
            .Where(s => s.Status == ScanStatus.Pending)
            .OrderBy(s => s.UploadedAt).Select(s => (DateTime?)s.UploadedAt).FirstOrDefaultAsync(ct);

        int Count(string key) => counts[key]!.GetValue<int>();
        return Ok(new JsonObject
        {
            ["status"] = "success",
            ["pending"] = Count(ScanStatus.Pending),
            ["extracting"] = Count(ScanStatus.Queued) + Count(ScanStatus.Processing),
            ["by_status"] = counts,
            ["total"] = rows.Sum(r => r.N),
            ["oldest_pending_at"] = oldest is { } o ? ReceiptScanner.Api.Json.UtcDateTimeConverter.ToWire(o) : null,
        });
    }

    // --- one scan -------------------------------------------------------------------

    /// <summary>GET /api/scans/{id}/ — the scan, its proposed receipt and the OCR text.</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var scan = await FindAsync(id, ct);
        return Ok(Envelope(Describe(scan, includeText: true), ("status", "success")));
    }

    /// <summary>GET /api/scans/{id}/image/ — the original, exactly as uploaded.</summary>
    [HttpGet("{id:long}/image")]
    public async Task<IActionResult> Image(long id, CancellationToken ct) =>
        ReceiptsController.MediaFile(media, Response, (await FindAsync(id, ct)).Image, "This scan has no image.");

    /// <summary>GET /api/scans/{id}/thumbnail/ — the derived JPEG preview.</summary>
    [HttpGet("{id:long}/thumbnail")]
    public async Task<IActionResult> Thumbnail(long id, CancellationToken ct) =>
        ReceiptsController.MediaFile(media, Response, (await FindAsync(id, ct)).Thumbnail, "This scan has no thumbnail.");

    /// <summary>
    /// POST /api/scans/{id}/approve/ — accept the proposal as a receipt. Any
    /// of shop, date, total, items in the body replace the extracted values
    /// (items replaces the whole list). "category" (an id) is the user's pick.
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public async Task<IActionResult> Approve(long id, CancellationToken ct)
    {
        var body = await ReadObjectAsync(ct);
        var scan = await FindAsync(id, ct);
        if (scan.Status != ScanStatus.Pending || string.IsNullOrEmpty(scan.Extraction))
            throw InvalidState(scan, "approve");

        var data = ScanViews.ParseJson(scan.Extraction) is { ValueKind: JsonValueKind.Object } e
            ? JsonNode.Parse(e.GetRawText())!.AsObject()
            : throw InvalidState(scan, "approve");

        var edited = Editable.Where(body.ContainsKey).Order(StringComparer.Ordinal).ToList();
        foreach (var key in edited) data[key] = body[key]?.DeepClone();

        // The worker may have attached a suggested category; it isn't receipt data.
        var suggestedId = data["category"] is JsonObject suggested && suggested["id"] is JsonValue sid && sid.TryGetValue<long>(out var s) ? s : (long?)null;
        data.Remove("category");

        long? chosenId = null;
        if (body["category"] is { } categoryNode)
        {
            var text = categoryNode is JsonValue cv && cv.GetValueKind() is JsonValueKind.Number or JsonValueKind.String
                ? (cv.GetValueKind() == JsonValueKind.String ? cv.GetValue<string>() : cv.ToJsonString()) : "";
            long? candidate = long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : null;
            chosenId = candidate is { } cid && await db.Categories.AnyAsync(x => x.Id == cid && x.IsActive, ct) ? cid : null;
            if (chosenId is null)
                throw ApiException.Validation(new Dictionary<string, string[]>
                {
                    ["category"] = [$"No active category {Repr(categoryNode)}."],
                }, "Some fields didn't validate.");
        }

        var (input, errors) = ReceiptValidator.Validate(data);
        if (errors.Count > 0) throw ApiException.Validation(errors, "Some fields didn't validate.");

        // The scan's photo becomes the receipt's: a copy, re-encoded as a
        // receipt photo, so neither can delete the other's file.
        StoredImage? photo = null;
        try
        {
            photo = images.Normalize(await System.IO.File.ReadAllBytesAsync(media.ResolvePath(scan.Image), ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Scan {Id} original unreadable ({Message}); approving without a photo.", id, ex.Message);
        }

        var now = Now;
        var userId = HttpContext.CurrentUser()?.Id;
        Receipt receipt;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var claimed = await db.ReceiptScans
                .Where(x => x.Id == id && x.Status == ScanStatus.Pending)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.Status, ScanStatus.Accepted)
                    .SetProperty(x => x.ReviewedAt, now)
                    .SetProperty(x => x.ReviewedById, userId), ct);
            if (claimed != 1)
            {
                await tx.RollbackAsync(ct);
                throw InvalidState(await FindAsync(id, ct), "approve");
            }

            receipt = (await writer.CreateAsync(input!, photo, chosenId, ct)).Receipt;
            await db.ReceiptScans.Where(x => x.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ReceiptId, receipt.Id), ct);
            await tx.CommitAsync(ct);
        }

        if (chosenId is not null && chosenId != suggestedId) edited.Add("category");
        logger.LogInformation("Scan {Id} approved as receipt {ReceiptId}{Edited}", id, receipt.Id,
            edited.Count > 0 ? $" (edited: {string.Join(", ", edited)})" : "");

        // After the commit: a matching failure must never undo the approval.
        var bankMatch = await reconciler.TryAutoMatchAsync(receipt, ct);
        var current = await FindAsync(id, ct);
        return StatusCode(StatusCodes.Status201Created, new JsonObject
        {
            ["status"] = "success",
            ["receipt_id"] = receipt.Id,
            ["edited"] = new JsonArray([.. edited.Select(x => (JsonNode)x)]),
            ["totals"] = ToNode(ReceiptViews.Totals(receipt)),
            ["bank_match"] = bankMatch is null ? null : ToNode(bankMatch),
            ["receipt"] = ToNode(ReceiptViews.Read(receipt, Request)),
            ["scan"] = ToNode(Describe(current)),
        });
    }

    /// <summary>POST /api/scans/{id}/reject/ — drop the proposal, keep the photo. {"rerun": true} retries.</summary>
    [HttpPost("{id:long}/reject")]
    public async Task<IActionResult> Reject(long id, CancellationToken ct)
    {
        var body = await ReadObjectAsync(ct);
        var rerun = Uploads.IsTruthy(body["rerun"]);
        var now = Now;
        var userId = HttpContext.CurrentUser()?.Id;

        var updated = await ClearExtraction(db.ReceiptScans.Where(x => x.Id == id && x.Status == ScanStatus.Pending),
            rerun ? ScanStatus.Queued : ScanStatus.Rejected, now, userId, ct);
        if (updated != 1) throw InvalidState(await FindAsync(id, ct), "reject");

        var scan = await FindAsync(id, ct);
        if (rerun) await QueueAsync(scan, ct);
        logger.LogInformation("Scan {Id} rejected{Rerun}", id, rerun ? " and re-queued" : "");

        return StatusCode(rerun ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
            Envelope(Describe(await FindAsync(id, ct)), ("status", "success"), ("rerun_queued", rerun)));
    }

    /// <summary>
    /// POST /api/scans/{id}/rerun/ — queue extraction again. Allowed when
    /// rejected, failed, still queued, or processing for longer than the
    /// stale limit (the worker died).
    /// </summary>
    [HttpPost("{id:long}/rerun")]
    public async Task<IActionResult> Rerun(long id, CancellationToken ct)
    {
        var now = Now;
        var staleBefore = now - StaleAfter;
        var rerunnable = ScanStatus.Rerunnable.ToList();

        var updated = await ClearExtraction(
            db.ReceiptScans.Where(x => x.Id == id && (rerunnable.Contains(x.Status)
                || (x.Status == ScanStatus.Processing && x.ExtractionStartedAt != null && x.ExtractionStartedAt < staleBefore))),
            ScanStatus.Queued, reviewedAt: null, reviewedBy: null, ct);
        if (updated != 1) throw InvalidState(await FindAsync(id, ct), "re-run");

        var scan = await FindAsync(id, ct);
        await QueueAsync(scan, ct);
        logger.LogInformation("Scan {Id} re-queued for extraction", id);

        return StatusCode(StatusCodes.Status202Accepted,
            Envelope(Describe(await FindAsync(id, ct)), ("status", "success"), ("rerun_queued", true)));
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>ReceiptScan.clear_extraction() plus the new status, as one conditional UPDATE.</summary>
    private static Task<int> ClearExtraction(IQueryable<ReceiptScan> target, string status, DateTime? reviewedAt, int? reviewedBy, CancellationToken ct)
    {
        var review = reviewedAt is not null;
        return target.ExecuteUpdateAsync(u =>
        {
            u.SetProperty(x => x.Status, status)
             .SetProperty(x => x.Extraction, (string?)null)
             .SetProperty(x => x.ExtractionCheck, "")
             .SetProperty(x => x.ExtractionNotes, "[]")
             .SetProperty(x => x.ExtractionText, "")
             .SetProperty(x => x.ExtractionError, "")
             .SetProperty(x => x.ExtractionSeconds, (double?)null)
             .SetProperty(x => x.ExtractedAt, (DateTime?)null);
            if (review)
                u.SetProperty(x => x.ReviewedAt, reviewedAt).SetProperty(x => x.ReviewedById, reviewedBy);
        }, ct);
    }

    /// <summary>Queue extraction and record the task id. A broker outage leaves the scan queued for a re-run.</summary>
    private async Task QueueAsync(ReceiptScan scan, CancellationToken ct)
    {
        var taskId = await queue.EnqueueAsync(scan.Id, ct);
        if (taskId is null) return;
        await db.ReceiptScans.Where(x => x.Id == scan.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ExtractionTaskId, taskId.Length > 64 ? taskId[..64] : taskId), ct);
        scan.ExtractionTaskId = taskId;
    }

    private async Task<ReceiptScan> FindAsync(long id, CancellationToken ct) =>
        await db.ReceiptScans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound();

    private ScanRead Describe(ReceiptScan scan, bool includeText = false) =>
        ScanViews.Describe(scan, Request, Now, StaleAfter, includeText);

    private ApiException InvalidState(ReceiptScan scan, string action) =>
        ApiException.Conflict("invalid_state", $"Can't {action} a scan that is {scan.Status}.",
            new Dictionary<string, object?> { ["scan"] = ToNode(Describe(scan)) });

    private JsonNode? ToNode(object value) => JsonSerializer.SerializeToNode(value, value.GetType(), jsonOptions.Value.JsonSerializerOptions);

    /// <summary>{head..., ...view} — the view's fields after the leading keys, as Django's {**describe_scan()}.</summary>
    private JsonObject Envelope(object view, params (string Key, JsonNode? Value)[] head)
    {
        var fields = ToNode(view)!.AsObject();
        var result = new JsonObject();
        foreach (var (key, value) in head) result[key] = value;
        foreach (var (key, _) in fields.ToList())
        {
            var value = fields[key];
            fields.Remove(key);
            result[key] = value;
        }
        return result;
    }

    /// <summary>A JSON object body, or {} when there's no body or it isn't an object (as Django does).</summary>
    private async Task<JsonObject> ReadObjectAsync(CancellationToken ct)
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            var obj = new JsonObject();
            foreach (var (key, value) in form) obj[key] = value.ToString();
            return obj;
        }
        try
        {
            return await Uploads.ReadJsonAsync(Request, ct) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest("malformed_payload", $"Body is not valid JSON: {ex.Message}");
        }
    }

    private TimeZoneInfo LocalTimeZone()
    {
        var id = config["TimeZone"];
        try { return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// The phone's capture time, or null (scan_views.parse_captured_at). ISO
    /// 8601 with or without an offset, or a bare date. A value without an
    /// offset is read in the server's time zone (config "TimeZone", default
    /// UTC). Anything implausible is dropped: a wrong capture time is worse
    /// than none, because it sorts the review queue.
    /// </summary>
    internal static DateTime? ParseCapturedAt(string? raw, DateTime utcNow, TimeZoneInfo local)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        DateTime utc;
        if (HasOffset().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            utc = withOffset.UtcDateTime;
        }
        else if (IsoLocal().IsMatch(text)
                 && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
        {
            utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(naive, DateTimeKind.Unspecified), local);
        }
        else if (ReceiptParsing.ParseApiDate(text) is { } day)
        {
            utc = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), local);
        }
        else return null;

        // A camera clock can be wrong, but not by years, and never ahead.
        if (utc > utcNow.AddDays(1) || utc < utcNow.AddDays(-365 * 10)) return null;
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    }

    /// <summary>Python repr of a JSON value, for error messages: '5' for a string, 5 for a number.</summary>
    private static string Repr(JsonNode node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? $"'{v.GetValue<string>().Replace("'", "\\'")}'"
            : node.ToJsonString();

    private static string Truncate(string? value, int max) =>
        value is null ? "" : value.Length > max ? value[..max] : value;

    private void TryDelete(string name)
    {
        try { media.Delete(name); } catch (Exception ex) { logger.LogWarning(ex, "Could not remove {Name}", name); }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+\-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex HasOffset();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?$")]
    private static partial Regex IsoLocal();
}
