using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReceiptScanner.Core.Domain;

namespace ReceiptScanner.Tests.Api;

/// <summary>
/// Phase 2: the scan lifecycle. The Celery worker is simulated by writing the
/// result onto the row, exactly as receipts.tasks.extract_scan does.
/// </summary>
public class ScansApiTests(ApiFactory factory) : ApiTestBase(factory)
{
    private const string Proposed = """
        {"shop": "Coles", "date": "2026-11-01", "total": "11.60",
         "items": [{"item": "Bananas", "qty": "1", "price": "3.60"},
                   {"item": "Milk", "qty": "2", "price": "8.00"}]}
        """;

    private async Task<long> UploadAsync(byte[]? image = null, params (string, string)[] fields)
    {
        var client = await ClientAsync();
        var body = await Expect(await client.PostAsync("/api/scans/", Form(image ?? MakeImage(), fields: fields)), HttpStatusCode.Created);
        return body.GetProperty("scan_id").GetInt64();
    }

    /// <summary>What the worker does when extraction succeeds.</summary>
    private Task Extracted(long id, string proposed = Proposed) =>
        Factory.WithDbAsync(db => db.ReceiptScans.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Status, ScanStatus.Pending)
            .SetProperty(s => s.Extraction, proposed)
            .SetProperty(s => s.ExtractionCheck, "ok")
            .SetProperty(s => s.ExtractionNotes, """["Date corrected."]""")
            .SetProperty(s => s.ExtractionText, "COLES\nBANANAS 3.60")
            .SetProperty(s => s.ExtractionAttempts, (ushort)1)
            .SetProperty(s => s.ExtractionSeconds, 24.3)
            .SetProperty(s => s.ExtractionStartedAt, DateTime.UtcNow.AddSeconds(-25))
            .SetProperty(s => s.ExtractedAt, DateTime.UtcNow)));

    private Task SetStatus(long id, string status, DateTime? startedAt = null) =>
        Factory.WithDbAsync(db => db.ReceiptScans.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Status, status)
            .SetProperty(s => s.ExtractionStartedAt, startedAt)));

    // --- upload --------------------------------------------------------------------------

    [Fact]
    public async Task Upload_stores_the_photo_and_queues_extraction()
    {
        var client = await ClientAsync();
        var uuid = Guid.NewGuid();
        var image = MakeImage(80, 60);

        var body = await Expect(await client.PostAsync("/api/scans/", Form(image, "IMG_1.JPG", fields:
            [("client_uuid", uuid.ToString()), ("captured_at", "2026-10-26T18:42:11+11:00"), ("note", "lunch")])),
            HttpStatusCode.Created);

        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("already_uploaded").GetBoolean());
        Assert.Equal("queued", body.GetProperty("scan_status").GetString());
        Assert.Equal(uuid.ToString(), body.GetProperty("client_uuid").GetString());
        Assert.Equal("2026-10-26T07:42:11Z", body.GetProperty("captured_at").GetString());

        var stored = body.GetProperty("stored");
        Assert.Equal("JPEG", stored.GetProperty("format").GetString());
        Assert.Equal(80, stored.GetProperty("width").GetInt32());
        Assert.Equal(60, stored.GetProperty("height").GetInt32());
        Assert.Equal(image.Length, stored.GetProperty("bytes").GetInt32());
        Assert.True(stored.GetProperty("thumbnail").GetBoolean());

        var id = body.GetProperty("scan_id").GetInt64();
        Assert.Equal($"/api/scans/{id}/image/", body.GetProperty("image_url").GetString());
        Assert.Equal($"/api/scans/{id}/thumbnail/", body.GetProperty("thumbnail_url").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("extraction").GetProperty("proposed").ValueKind);
        Assert.False(body.GetProperty("extraction").TryGetProperty("ocr_text", out _));
        Assert.Equal("""{"approve":false,"reject":false,"rerun":true}""", body.GetProperty("actions").GetRawText());

        Assert.Contains(id, Factory.Queue.Queued);
        var taskId = await Factory.WithDbAsync(db => db.ReceiptScans.Where(s => s.Id == id).Select(s => s.ExtractionTaskId).SingleAsync());
        Assert.Equal($"task-{id}", taskId);

        // The original comes back byte for byte.
        var original = await client.GetAsync($"/api/scans/{id}/image/");
        Assert.Equal(image, await original.Content.ReadAsByteArrayAsync());
        AssertLongPrivateCache(original);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/scans/{id}/thumbnail/")).StatusCode);
    }

    [Fact]
    public async Task A_retry_with_the_same_client_uuid_returns_the_first_scan()
    {
        var client = await ClientAsync();
        var uuid = Guid.NewGuid().ToString();
        var first = await UploadAsync(MakeImage(color: "blue"), ("client_uuid", uuid));

        var again = await Expect(await client.PostAsync("/api/scans/", Form(MakeImage(color: "blue"), fields: ("client_uuid", uuid))), HttpStatusCode.OK);

        Assert.True(again.GetProperty("already_uploaded").GetBoolean());
        Assert.Equal(first, again.GetProperty("scan_id").GetInt64());
    }

    [Fact]
    public async Task The_same_photo_twice_is_flagged_not_refused()
    {
        var client = await ClientAsync();
        var image = MakeImage(33, 33, color: "green");
        var first = await UploadAsync(image);

        var second = await Expect(await client.PostAsync("/api/scans/", Form(image)), HttpStatusCode.Created);

        Assert.Equal(first, second.GetProperty("possible_duplicate_of").GetInt64());
        Assert.NotEqual(first, second.GetProperty("scan_id").GetInt64());
    }

    [Fact]
    public async Task Upload_errors_use_the_documented_codes()
    {
        var client = await ClientAsync();

        AssertError(await Expect(await client.PostAsync("/api/scans/", Form(fields: ("note", "x"))), HttpStatusCode.BadRequest), "no_file");
        AssertError(await Expect(await client.PostAsync("/api/scans/", Form("%PDF-1.4 not a photo"u8.ToArray())), HttpStatusCode.UnprocessableEntity), "invalid_image");
        AssertError(await Expect(await client.PostAsync("/api/scans/", Json(new { image = "x" })), HttpStatusCode.UnsupportedMediaType), "unsupported_media_type");
        AssertError(await Expect(await client.PostAsync("/api/scans/", Form(MakeImage(), fields: ("client_uuid", "nope"))), HttpStatusCode.UnprocessableEntity), "validation_error");
        AssertError(await Expect(await Anonymous.PostAsync("/api/scans/", Form(MakeImage())), HttpStatusCode.Unauthorized), "not_authenticated");
    }

    [Fact]
    public async Task A_broker_outage_leaves_the_scan_queued_for_a_rerun()
    {
        Factory.Queue.Down = true;
        try
        {
            var id = await UploadAsync(MakeImage(color: "orange"));
            Assert.DoesNotContain(id, Factory.Queue.Queued);
            var scan = await Expect(await (await ClientAsync()).GetAsync($"/api/scans/{id}/"), HttpStatusCode.OK);
            Assert.Equal("queued", scan.GetProperty("scan_status").GetString());
            Assert.True(scan.GetProperty("actions").GetProperty("rerun").GetBoolean());
        }
        finally
        {
            Factory.Queue.Down = false;
        }
    }

    // --- review --------------------------------------------------------------------------

    [Fact]
    public async Task Detail_shows_the_proposal_with_totals_and_ocr_text()
    {
        var client = await ClientAsync();
        var id = await UploadAsync(MakeImage(color: "purple"));
        await Extracted(id);

        var body = await Expect(await client.GetAsync($"/api/scans/{id}/"), HttpStatusCode.OK);

        Assert.Equal("pending", body.GetProperty("scan_status").GetString());
        var extraction = body.GetProperty("extraction");
        Assert.Equal("Coles", extraction.GetProperty("proposed").GetProperty("shop").GetString());
        Assert.Equal("""{"stated":"11.60","items_sum":"11.60","discrepancy":"0.00","reconciles":true}""",
            extraction.GetProperty("totals").GetRawText());
        Assert.Equal("ok", extraction.GetProperty("check").GetString());
        Assert.Equal("Date corrected.", extraction.GetProperty("notes")[0].GetString());
        Assert.Equal(1, extraction.GetProperty("attempts").GetInt32());
        Assert.Equal("COLES\nBANANAS 3.60", extraction.GetProperty("ocr_text").GetString());
        Assert.Equal("""{"approve":true,"reject":true,"rerun":false}""", body.GetProperty("actions").GetRawText());
    }

    [Fact]
    public async Task Approve_with_corrections_creates_the_receipt_once()
    {
        var client = await ClientAsync();
        var id = await UploadAsync(MakeImage(color: "yellow"));
        await Extracted(id);

        var body = await Expect(await client.PostAsync($"/api/scans/{id}/approve/",
            Json(new { shop = "Woolworths", total = "11.60" })), HttpStatusCode.Created);

        Assert.Equal("""["shop","total"]""", body.GetProperty("edited").GetRawText());
        Assert.True(body.GetProperty("totals").GetProperty("reconciles").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bank_match").ValueKind);
        var receipt = body.GetProperty("receipt");
        Assert.Equal("Woolworths", receipt.GetProperty("shop").GetString());
        Assert.Equal("2026-11-01", receipt.GetProperty("date").GetString());
        Assert.Equal(2, receipt.GetProperty("items").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, receipt.GetProperty("image_url").ValueKind);   // the scan's photo, copied

        var scan = body.GetProperty("scan");
        Assert.Equal("accepted", scan.GetProperty("scan_status").GetString());
        Assert.Equal(body.GetProperty("receipt_id").GetInt64(), scan.GetProperty("receipt_id").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, scan.GetProperty("reviewed_at").ValueKind);

        // Approving again is a conflict that shows the current state.
        var again = await Expect(await client.PostAsync($"/api/scans/{id}/approve/", Json(new { })), HttpStatusCode.Conflict);
        AssertError(again, "invalid_state");
        Assert.Equal("accepted", again.GetProperty("scan").GetProperty("scan_status").GetString());
        Assert.Equal(1, await Factory.WithDbAsync(db => db.Receipts.CountAsync(r => r.Shop == "Woolworths")));
    }

    [Fact]
    public async Task A_correction_that_doesnt_validate_changes_nothing()
    {
        var client = await ClientAsync();
        var id = await UploadAsync(MakeImage(color: "pink"));
        await Extracted(id);

        var body = await Expect(await client.PostAsync($"/api/scans/{id}/approve/", Json(new { total = "abc" })), HttpStatusCode.UnprocessableEntity);
        AssertError(body, "validation_error");

        var scan = await Expect(await client.GetAsync($"/api/scans/{id}/"), HttpStatusCode.OK);
        Assert.Equal("pending", scan.GetProperty("scan_status").GetString());
    }

    [Fact]
    public async Task Approve_needs_a_pending_scan()
    {
        var client = await ClientAsync();
        var id = await UploadAsync(MakeImage(color: "white"));   // still queued

        AssertError(await Expect(await client.PostAsync($"/api/scans/{id}/approve/", Json(new { })), HttpStatusCode.Conflict), "invalid_state");
        AssertError(await Expect(await client.PostAsync("/api/scans/999999/approve/", Json(new { })), HttpStatusCode.NotFound), "not_found");
    }

    [Fact]
    public async Task Reject_keeps_the_photo_and_can_rerun_in_one_go()
    {
        var client = await ClientAsync();
        var id = await UploadAsync(MakeImage(color: "black"));
        await Extracted(id);

        var rejected = await Expect(await client.PostAsync($"/api/scans/{id}/reject/", Json(new { })), HttpStatusCode.OK);
        Assert.Equal("rejected", rejected.GetProperty("scan_status").GetString());
        Assert.False(rejected.GetProperty("rerun_queued").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rejected.GetProperty("extraction").GetProperty("proposed").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, rejected.GetProperty("image_url").ValueKind);

        var id2 = await UploadAsync(MakeImage(color: "gray"));
        await Extracted(id2);
        var before = Factory.Queue.Queued.Count(x => x == id2);
        var rerun = await Expect(await client.PostAsync($"/api/scans/{id2}/reject/", Json(new { rerun = true })), HttpStatusCode.Accepted);
        Assert.Equal("queued", rerun.GetProperty("scan_status").GetString());
        Assert.True(rerun.GetProperty("rerun_queued").GetBoolean());
        Assert.Equal(before + 1, Factory.Queue.Queued.Count(x => x == id2));

        AssertError(await Expect(await client.PostAsync($"/api/scans/{id}/reject/", Json(new { })), HttpStatusCode.Conflict), "invalid_state");
    }

    [Fact]
    public async Task Rerun_follows_the_documented_rules()
    {
        var client = await ClientAsync();
        var id = await UploadAsync(MakeImage(color: "navy"));

        await SetStatus(id, ScanStatus.Failed);
        Assert.Equal("queued", (await Expect(await client.PostAsync($"/api/scans/{id}/rerun/", null), HttpStatusCode.Accepted))
            .GetProperty("scan_status").GetString());

        await SetStatus(id, ScanStatus.Processing, DateTime.UtcNow.AddMinutes(-5));
        AssertError(await Expect(await client.PostAsync($"/api/scans/{id}/rerun/", null), HttpStatusCode.Conflict), "invalid_state");

        // Processing for longer than the stale limit: the worker died.
        await SetStatus(id, ScanStatus.Processing, DateTime.UtcNow.AddMinutes(-31));
        await Expect(await client.PostAsync($"/api/scans/{id}/rerun/", null), HttpStatusCode.Accepted);

        await Extracted(id);
        AssertError(await Expect(await client.PostAsync($"/api/scans/{id}/rerun/", null), HttpStatusCode.Conflict), "invalid_state");
    }

    // --- lists ---------------------------------------------------------------------------

    [Fact]
    public async Task Pending_counts_every_status_and_list_filters()
    {
        var client = await ClientAsync();
        var id = await UploadAsync(MakeImage(color: "teal"));
        await Extracted(id);

        var counts = await Expect(await client.GetAsync("/api/scans/pending/"), HttpStatusCode.OK);
        var byStatus = counts.GetProperty("by_status");
        foreach (var status in ScanStatus.All) Assert.True(byStatus.TryGetProperty(status, out _), status);
        Assert.True(counts.GetProperty("pending").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.String, counts.GetProperty("oldest_pending_at").ValueKind);

        var pending = await Expect(await client.GetAsync("/api/scans/?status=pending,failed"), HttpStatusCode.OK);
        Assert.All(pending.GetProperty("results").EnumerateArray(),
            s => Assert.Contains(s.GetProperty("scan_status").GetString(), new[] { "pending", "failed" }));

        AssertError(await Expect(await client.GetAsync("/api/scans/?status=pending,bogus"), HttpStatusCode.UnprocessableEntity), "validation_error");
    }
}
