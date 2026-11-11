using ReceiptScanner.Api.Banking;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReceiptScanner.Api.Auth;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Api.Http;
using ReceiptScanner.Api.Pagination;
using ReceiptScanner.Api.Receipts;
using ReceiptScanner.Core.Receipts;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;
using ReceiptScanner.Infrastructure.Images;
using ReceiptScanner.Infrastructure.Storage;

namespace ReceiptScanner.Api.Controllers;

/// <summary>Port of receipts/views.py: create, list, detail, delete and photo.</summary>
[ApiController]
[Route("api/receipts")]
public sealed class ReceiptsController(
    ReceiptScannerDbContext db,
    ReceiptWriter writer,
    IImageProcessor images,
    IMediaStorage media,
    Reconciler reconciler,
    IOptions<ImageOptions> imageOptions) : ControllerBase
{
    public const string InvalidImage =
        "Upload a valid image. The file you uploaded was either not an image or a corrupted image.";

    /// <summary>
    /// POST /api/receipts/ — a receipt whose data was already extracted on the
    /// phone. Body: receipt JSON; the legacy {"data": "```json …```"} wrapper;
    /// or multipart with a "data" field and an image. Bearer token or X-API-Key.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [RequireUserOrApiKey]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        JsonNode? raw;
        IFormFile? upload = null;
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            upload = Uploads.Find(form.Files);
            raw = FormToJson(form);
        }
        else
        {
            try
            {
                raw = await Uploads.ReadJsonAsync(Request, ct);
            }
            catch (JsonException ex)
            {
                throw ApiException.BadRequest("malformed_payload", $"Body is not valid JSON: {ex.Message}");
            }
        }

        JsonObject data;
        try
        {
            data = ReceiptPayload.Normalize(raw);
        }
        catch (PayloadException ex)
        {
            throw ApiException.BadRequest("malformed_payload", ex.Message);
        }

        StoredImage? photo = null;
        var imageErrors = new List<string>();
        if (upload is not null)
        {
            var limit = imageOptions.Value.MaxUploadBytes;
            if (upload.Length > limit)
                throw new ApiException(StatusCodes.Status413PayloadTooLarge, "image_too_large",
                    $"Image is {upload.Length} bytes; the limit is {limit}.");
            // HEIC becomes JPEG; oversized photos shrink to 2400px.
            photo = images.Normalize(await Uploads.ReadAllAsync(upload, ct));
            if (photo is null) imageErrors.Add(InvalidImage);
        }

        var (input, errors) = ReceiptValidator.Validate(data);
        if (imageErrors.Count > 0) errors["image"] = [.. imageErrors];

        // A repeated client_uuid isn't a validation failure: the request that
        // timed out already stored it. 409 + a code the app treats as success.
        var clientUuid = input?.ClientUuid ?? ClientUuidOf(data);
        if (clientUuid is { } uuid && await writer.ClientUuidExistsAsync(uuid, ct))
            throw Duplicate(errors);

        if (errors.Count > 0)
            throw ApiException.Validation(errors, "Some fields didn't validate.");

        WrittenReceipt written;
        try
        {
            written = await writer.CreateAsync(input!, photo, categoryId: null, ct);
        }
        catch (DuplicateReceiptException)
        {
            throw Duplicate(errors);
        }

        var receipt = written.Receipt;
        var bankMatch = await reconciler.TryAutoMatchAsync(receipt, ct);
        return StatusCode(StatusCodes.Status201Created, new ReceiptCreated(
            "success",
            receipt.Id,
            ReceiptRows: 1,
            ItemRows: receipt.Items.Count,
            ImageSaved: receipt.Image is not null,
            written.ImageNote,
            ReceiptViews.Totals(receipt),
            bankMatch,
            ReceiptViews.Read(receipt, Request)));
    }

    /// <summary>GET /api/receipts/list/ — newest first, paginated, with items.</summary>
    [HttpGet("list")]
    public async Task<IActionResult> List(
        [FromQuery] string? q, [FromQuery] string? shop, [FromQuery] string? item,
        [FromQuery] string? since, [FromQuery] string? category, CancellationToken ct)
    {
        IQueryable<Receipt> query = db.Receipts.AsNoTracking();

        if (!string.IsNullOrEmpty(shop))
        {
            var p = LikePattern(shop);
            query = query.Where(r => EF.Functions.Like(r.Shop, p, LikeEscape));
        }
        if (!string.IsNullOrEmpty(item))
        {
            var p = LikePattern(item);
            query = query.Where(r => r.Items.Any(i => EF.Functions.Like(i.Name, p, LikeEscape)));
        }
        if (!string.IsNullOrEmpty(q))
        {
            var p = LikePattern(q);
            query = query.Where(r => EF.Functions.Like(r.Shop, p, LikeEscape) || r.Items.Any(i => EF.Functions.Like(i.Name, p, LikeEscape)));
        }
        if (!string.IsNullOrEmpty(since))
        {
            if (!DateOnly.TryParseExact(since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
                throw ApiException.BadRequest("invalid_parameter", "since must be a date in yyyy-mm-dd form.");
            query = query.Where(r => r.PurchasedOn >= from);
        }
        if (category == "none")
        {
            query = query.Where(r => r.CategoryId == null);
        }
        else if (!string.IsNullOrEmpty(category) && long.TryParse(category, NumberStyles.None, CultureInfo.InvariantCulture, out var cid))
        {
            // A parent includes its children.
            query = query.Where(r => r.CategoryId == cid || (r.Category != null && r.Category.ParentId == cid));
        }

        query = query
            .OrderByDescending(r => r.PurchasedOn).ThenByDescending(r => r.Id)
            .Include(r => r.Items)
            .Include(r => r.Category!).ThenInclude(c => c.Parent)
            .AsSplitQuery();

        return Ok(await Paginator.PaginateAsync(query, r => ReceiptViews.Read(r, Request), Request, ct));
    }

    /// <summary>GET /api/receipts/{id}/</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var receipt = await db.Receipts.AsNoTracking()
            .Include(r => r.Items)
            .Include(r => r.Category!).ThenInclude(c => c.Parent)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw ApiException.NotFound();
        return Ok(ReceiptViews.Read(receipt, Request));
    }

    /// <summary>
    /// DELETE /api/receipts/{id}/ — 204. Items and rejections go with it;
    /// scans and bank transactions that pointed at it are unlinked. Done
    /// explicitly, because a database Django created has no ON DELETE rules.
    /// </summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        if (!await db.Receipts.AnyAsync(r => r.Id == id, ct)) throw ApiException.NotFound();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ReceiptScans.Where(s => s.ReceiptId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReceiptId, (long?)null), ct);
        await db.BankTransactions.Where(t => t.ReceiptId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReceiptId, (long?)null), ct);
        await db.MatchRejections.Where(m => m.ReceiptId == id).ExecuteDeleteAsync(ct);
        await db.ReceiptItems.Where(i => i.ReceiptId == id).ExecuteDeleteAsync(ct);
        await db.Receipts.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);

        return NoContent();
    }

    /// <summary>GET /api/receipts/{id}/image/ — the photo, for a signed-in user only.</summary>
    [HttpGet("{id:long}/image")]
    public async Task<IActionResult> Image(long id, CancellationToken ct)
    {
        var name = await db.Receipts.Where(r => r.Id == id).Select(r => new { r.Image }).FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound();
        return MediaFile(media, Response, name.Image, "This receipt has no image.");
    }

    /// <summary>Serve a stored file with the long private cache the API promises. 404 if missing.</summary>
    internal static IActionResult MediaFile(IMediaStorage media, HttpResponse response, string? name, string missing)
    {
        if (string.IsNullOrEmpty(name)) throw ApiException.NotFound(missing);
        var path = media.ResolvePath(name);
        if (!System.IO.File.Exists(path)) throw ApiException.NotFound(missing);

        // The bytes behind an id never change; `private` keeps personal
        // documents out of shared caches.
        response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return new PhysicalFileResult(path, Uploads.ContentTypeFor(path));
    }

    private static ApiException Duplicate(Dictionary<string, string[]> errors)
    {
        errors["client_uuid"] = [ReceiptValidator.DuplicateClientUuid];
        return new ApiException(StatusCodes.Status409Conflict, "duplicate_receipt",
            "This receipt has already been uploaded.", errors);
    }

    /// <summary>The client_uuid, if it parses, even when other fields don't validate.</summary>
    private static Guid? ClientUuidOf(JsonObject data) =>
        data["client_uuid"] is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : null;

    /// <summary>
    /// Multipart: the receipt JSON in a "data" field (what the Shortcut sends),
    /// or the receipt fields themselves, with "items" as a JSON string.
    /// </summary>
    private static JsonNode? FormToJson(IFormCollection form)
    {
        if (form.TryGetValue("data", out var data)) return JsonValue.Create(data.ToString());

        var obj = new JsonObject();
        foreach (var (key, value) in form)
        {
            var text = value.ToString();
            if (key == "items")
            {
                try { obj[key] = JsonNode.Parse(text); }
                catch (JsonException) { obj[key] = text; }
            }
            else obj[key] = text;
        }
        return obj.Count == 0 ? null : obj;
    }

    /// <summary>
    /// LIKE's escape character. Not a backslash: Oracle's MySQL provider
    /// writes it as <c>ESCAPE '\'</c>, where MySQL reads the backslash as
    /// escaping the closing quote, a syntax error. "!" means nothing special
    /// in MySQL, MariaDB or SQLite string literals.
    /// </summary>
    internal const string LikeEscape = "!";

    /// <summary>Case-insensitive "contains" (icontains), with LIKE wildcards in the term escaped.</summary>
    internal static string LikePattern(string term) =>
        "%" + term.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
}
