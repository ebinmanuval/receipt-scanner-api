using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using ReceiptScanner.Api.Banking;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Api.Http;
using ReceiptScanner.Api.Pagination;
using ReceiptScanner.Core.Banking;

namespace ReceiptScanner.Api.Controllers;

/// <summary>
/// The reconcile screens: a review queue and the per-receipt decisions
/// (banking/reconcile_views.py). Every write re-validates against the
/// database, because the app is always working from a list that may be
/// seconds old; a decision that no longer applies is 409 match_unavailable
/// with the ids involved.
/// </summary>
[ApiController]
[Route("api/bank/reconcile")]
public sealed class ReconcileController(Reconciler reconciler, ILogger<ReconcileController> logger) : ControllerBase
{
    /// <summary>
    /// GET queue/ — receipts awaiting a decision, oldest purchase first.
    /// Default buckets: ambiguous, weak. ?bucket=none is the "paid cash?" list.
    /// </summary>
    [HttpGet("queue")]
    public async Task<IActionResult> Queue([FromQuery] string? bucket, [FromQuery] string? window,
        [FromQuery] string? since, [FromQuery] string? until, CancellationToken ct)
    {
        HashSet<string> buckets;
        if (!string.IsNullOrEmpty(bucket))
        {
            buckets = bucket.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            var unknown = buckets.Except(Reconciler.Buckets).Order(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
                throw ApiException.BadRequest("invalid_bucket",
                    $"Unknown bucket(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", Reconciler.Buckets.Order(StringComparer.Ordinal))}.");
        }
        else
        {
            buckets = [CandidateSet.Ambiguous, CandidateSet.Weak];
        }

        var items = await reconciler.QueueAsync(Window(window), BankController.ParseDate(since, "since"),
            BankController.ParseDate(until, "until"), buckets, ct);
        return Ok(Paginator.PaginateList(items, i => BankViews.QueueItem(i, Request), Request));
    }

    /// <summary>GET summary/ — counts per bucket, for a badge.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] string? window, CancellationToken ct) =>
        Ok(await reconciler.SummaryAsync(Window(window), ct));

    /// <summary>POST run/ — link every receipt with one confident candidate. {"dry_run": true} previews.</summary>
    [HttpPost("run")]
    public async Task<IActionResult> Run([FromQuery] string? window, CancellationToken ct)
    {
        var body = await Uploads.ReadObjectAsync(Request, ct);
        var dryRun = (Uploads.Text(body, "dry_run") ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes";

        var result = await reconciler.AutoLinkAsync(null, Window(window),
            BankController.ParseDate(Uploads.Text(body, "since"), "since"),
            BankController.ParseDate(Uploads.Text(body, "until"), "until"), dryRun, ct);
        logger.LogInformation("reconcile run: linked={Linked} needs_review={Review} dry_run={DryRun}",
            result.Linked.Count, result.NeedsReview, dryRun);

        var response = new Dictionary<string, object> { ["status"] = "success", ["dry_run"] = dryRun };
        foreach (var (key, value) in (Dictionary<string, int>)result.AsDict()) response[key] = value;
        return Ok(response);
    }

    /// <summary>POST {receipt}/confirm/ {transaction_id} — any unlinked transaction, suggested or not. Idempotent.</summary>
    [HttpPost("{id:long}/confirm")]
    public async Task<IActionResult> Confirm(long id, CancellationToken ct)
    {
        var txnId = TransactionId(await Uploads.ReadObjectAsync(Request, ct));
        var txn = await reconciler.ConfirmAsync(id, txnId, ct);
        logger.LogInformation("confirmed receipt={Receipt} txn={Txn}", id, txn.Id);
        return Ok(new Dictionary<string, object> { ["status"] = "success", ["receipt_id"] = id, ["transaction"] = BankViews.Transaction(txn) });
    }

    /// <summary>POST {receipt}/reject/ {transaction_id, note} — this pair is wrong; stop offering it.</summary>
    [HttpPost("{id:long}/reject")]
    public async Task<IActionResult> Reject(long id, CancellationToken ct)
    {
        var body = await Uploads.ReadObjectAsync(Request, ct);
        var txnId = TransactionId(body);
        await reconciler.RejectAsync(id, txnId, Uploads.Text(body, "note") ?? "", ct);
        return Ok(new Dictionary<string, object>
        {
            ["status"] = "success", ["receipt_id"] = id, ["transaction_id"] = txnId,
            ["detail"] = "This pair won't be suggested again.",
        });
    }

    /// <summary>POST {receipt}/unreject/ {transaction_id} — undo a rejection (the mis-tap escape hatch).</summary>
    [HttpPost("{id:long}/unreject")]
    public async Task<IActionResult> Unreject(long id, CancellationToken ct)
    {
        var txnId = TransactionId(await Uploads.ReadObjectAsync(Request, ct));
        var undone = await reconciler.UnrejectAsync(id, txnId, ct);
        return Ok(new Dictionary<string, object>
        {
            ["status"] = "success", ["receipt_id"] = id, ["transaction_id"] = txnId, ["undone"] = undone,
            ["detail"] = undone ? "This pair can be suggested again." : "There was no rejection to undo.",
        });
    }

    /// <summary>POST {receipt}/no-match/ {note} — paid cash, or a card that isn't imported.</summary>
    [HttpPost("{id:long}/no-match")]
    public async Task<IActionResult> NoMatch(long id, CancellationToken ct)
    {
        var body = await Uploads.ReadObjectAsync(Request, ct);
        await reconciler.MarkNoMatchAsync(id, Uploads.Text(body, "note") ?? "", ct);
        return Ok(new Dictionary<string, object>
        {
            ["status"] = "success", ["receipt_id"] = id, ["no_bank_match"] = true,
            ["detail"] = "Marked as having no bank transaction.",
        });
    }

    /// <summary>POST {receipt}/clear-no-match/ — back into the queue.</summary>
    [HttpPost("{id:long}/clear-no-match")]
    public async Task<IActionResult> ClearNoMatch(long id, CancellationToken ct)
    {
        await reconciler.ClearNoMatchAsync(id, ct);
        return Ok(new Dictionary<string, object>
        {
            ["status"] = "success", ["receipt_id"] = id, ["no_bank_match"] = false, ["detail"] = "Back in the queue.",
        });
    }

    /// <summary>DELETE {receipt}/match/ — undo a link, auto or manual. Not a rejection.</summary>
    [HttpDelete("{id:long}/match")]
    public async Task<IActionResult> Unmatch(long id, CancellationToken ct)
    {
        var txnId = await reconciler.UnmatchAsync(id, ct);
        logger.LogInformation("unmatched receipt={Receipt} txn={Txn}", id, txnId);
        return Ok(new Dictionary<string, object>
        {
            ["status"] = "success", ["receipt_id"] = id, ["transaction_id"] = txnId, ["detail"] = "Link removed.",
        });
    }

    /// <summary>Days a receipt and transaction may differ: default 3, clamped to 0–14, junk ignored.</summary>
    private static int Window(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            ? Math.Clamp(w, 0, Reconciler.MaxWindow)
            : Reconciler.DefaultWindow;

    private static long TransactionId(JsonObject body)
    {
        var raw = body["transaction_id"];
        var text = raw switch
        {
            null => null,
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            _ => raw.ToJsonString(),
        };
        if (string.IsNullOrEmpty(text))
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "validation_error", "transaction_id is required.");
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
        {
            var repr = raw is JsonValue s && s.GetValueKind() == JsonValueKind.String ? $"'{text}'" : text;
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "validation_error",
                $"transaction_id must be an integer, got {repr}.");
        }
        return id;
    }
}
