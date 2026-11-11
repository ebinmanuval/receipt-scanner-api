using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReceiptScanner.Api.Banking;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Api.Http;
using ReceiptScanner.Api.Pagination;
using ReceiptScanner.Core.Banking;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;
using ReceiptScanner.Infrastructure.Images;

namespace ReceiptScanner.Api.Controllers;

/// <summary>OFX upload and the transaction search (banking/views.py, reconcile_views.TransactionListView).</summary>
[ApiController]
[Route("api/bank")]
public sealed class BankController(
    ReceiptScannerDbContext db,
    OfxImporter importer,
    IOptions<ImageOptions> uploadOptions,
    ILogger<BankController> logger) : ControllerBase
{
    private static readonly string[] FileFields = ["file", "ofx", "statement"];

    /// <summary>
    /// POST /api/bank/statements/ — multipart, the file in "file" (or "ofx",
    /// "statement"). Options: reimport, dry_run, nickname. 201 when anything
    /// was created, 200 when everything was already there.
    /// </summary>
    [HttpPost("statements")]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        if (!Request.HasFormContentType)
            throw ApiException.BadRequest("no_file", "No file uploaded. Send it as a 'file' form field.");

        var form = await Request.ReadFormAsync(ct);
        // Exact names on purpose: this is driven by curl or a script, where a
        // mismatch should fail loudly rather than be guessed at.
        var upload = FileFields.Select(f => form.Files.GetFile(f)).FirstOrDefault(f => f is not null)
            ?? throw ApiException.BadRequest("no_file", "No file uploaded. Send it as a 'file' form field.");

        var limit = uploadOptions.Value.MaxUploadBytes;
        if (upload.Length > limit)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "file_too_large",
                $"File is {upload.Length} bytes; the limit is {limit}.");

        bool Flag(string name) => form[name].ToString().Trim().ToLowerInvariant() is "1" or "true" or "yes";
        var nickname = form["nickname"].ToString();

        ImportResult result;
        try
        {
            result = await importer.ImportAsync(await Uploads.ReadAllAsync(upload, ct), upload.FileName,
                dryRun: Flag("dry_run"), allowDuplicateFile: Flag("reimport"),
                nickname: nickname.Length > 100 ? nickname[..100] : nickname, ct);
        }
        catch (DuplicateStatementException ex)
        {
            throw ApiException.Conflict("duplicate_statement", ex.Message, new Dictionary<string, object?>
            {
                ["statement_id"] = ex.Existing.Id,
                ["hint"] = "Send reimport=true to process it anyway.",
            });
        }
        catch (OfxException ex)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "invalid_ofx", $"Could not parse the file: {ex.Message}");
        }

        logger.LogInformation("OFX import: statement={Statement} created={Created} duplicate={Duplicates} conflicts={Conflicts}",
            result.Statement?.Id, result.Created, result.Duplicates, result.Conflicts.Count);

        var body = new Dictionary<string, object?> { ["status"] = "success" };
        foreach (var (key, value) in result.AsDict()) body[key] = value;
        return StatusCode(result.Created > 0 ? StatusCodes.Status201Created : StatusCodes.Status200OK, body);
    }

    /// <summary>
    /// GET /api/bank/transactions/ — find the right one by hand. Unmatched,
    /// newest first by default. matched, q, amount (sign-insensitive), since,
    /// until, account, purchases_only, category.
    /// </summary>
    [HttpGet("transactions")]
    public async Task<IActionResult> Transactions(
        [FromQuery] string? matched, [FromQuery] string? q, [FromQuery] string? amount,
        [FromQuery] string? since, [FromQuery] string? until, [FromQuery] string? account,
        [FromQuery(Name = "purchases_only")] string? purchasesOnly, [FromQuery] string? category, CancellationToken ct)
    {
        IQueryable<BankTransaction> query = db.BankTransactions.AsNoTracking();

        switch ((matched ?? "false").ToLowerInvariant())
        {
            case "false": query = query.Where(t => t.ReceiptId == null); break;
            case "true": query = query.Where(t => t.ReceiptId != null); break;
            case "all": break;
            default: throw Invalid("matched must be true, false or all.");
        }

        if (!string.IsNullOrEmpty(q))
        {
            var p = ReceiptsController.LikePattern(q);
            query = query.Where(t => EF.Functions.Like(t.Memo, p, ReceiptsController.LikeEscape)
                                     || EF.Functions.Like(t.Name, p, ReceiptsController.LikeEscape));
        }
        if (!string.IsNullOrEmpty(account))
        {
            if (!long.TryParse(account, NumberStyles.None, CultureInfo.InvariantCulture, out var accountId))
                throw Invalid($"account must be an id, got '{account}'.");
            query = query.Where(t => t.AccountId == accountId);
        }
        if (ParseDate(since, "since") is { } fromDate) query = query.Where(t => t.PostedOn >= fromDate);
        if (ParseDate(until, "until") is { } toDate) query = query.Where(t => t.PostedOn <= toDate);

        if (category == "none") query = query.Where(t => t.CategoryId == null);
        else if (!string.IsNullOrEmpty(category) && long.TryParse(category, NumberStyles.None, CultureInfo.InvariantCulture, out var cid))
            query = query.Where(t => t.CategoryId == cid || (t.Category != null && t.Category.ParentId == cid));

        if (!string.IsNullOrEmpty(amount))
        {
            // Sign-insensitive: a receipt total is positive, the debit that paid it negative.
            if (!decimal.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw Invalid($"amount must be a number, got '{amount}'.");
            var abs = Math.Abs(value);
            query = query.Where(t => t.Amount == abs || t.Amount == -abs);
        }

        query = query
            .OrderByDescending(t => t.PostedOn).ThenByDescending(t => t.Id)
            .Include(t => t.Account)
            .Include(t => t.Category!).ThenInclude(c => c.Parent);

        if ((purchasesOnly ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes")
        {
            // The matcher's regex can't be SQL, so filter the already-narrowed rows here.
            var rows = (await query.ToListAsync(ct)).Where(t => Matching.IsPurchase(t.Memo)).ToList();
            return Ok(Paginator.PaginateList(rows, BankViews.Transaction, Request));
        }
        return Ok(await Paginator.PaginateAsync(query, BankViews.Transaction, Request, ct));
    }

    internal static DateOnly? ParseDate(string? value, string name)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        throw ApiException.BadRequest("invalid_parameter", $"{name} must be a date in yyyy-mm-dd form.");
    }

    private static ApiException Invalid(string message) =>
        new(StatusCodes.Status422UnprocessableEntity, "validation_error", message);
}
