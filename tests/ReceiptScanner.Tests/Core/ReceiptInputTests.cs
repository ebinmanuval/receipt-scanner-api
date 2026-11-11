using System.Text.Json;
using System.Text.Json.Nodes;
using ReceiptScanner.Api.Controllers;
using ReceiptScanner.Core.Extraction;
using ReceiptScanner.Core.Images;
using ReceiptScanner.Core.Receipts;
using ReceiptScanner.Infrastructure.Extraction;

namespace ReceiptScanner.Tests.Core;

/// <summary>Ports of receipts/parsing.py, payload.py and the serializer rules.</summary>
public class ReceiptInputTests
{
    [Theory]
    [InlineData("£1,234.50", "1234.50")]
    [InlineData("1234.5", "1234.50")]
    [InlineData("$-3.00", "-3.00")]
    [InlineData("0.125", "0.12")]      // half-to-even
    [InlineData(" 12 ", "12.00")]
    [InlineData("abc", null)]
    [InlineData("-", null)]
    [InlineData("1-2", null)]
    public void Money_parsing_matches_clean_decimal(string input, string? expected) =>
        Assert.Equal(expected, ReceiptParsing.CleanDecimal(input)?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("2025-12-20", "2025-12-20")]
    [InlineData("20/12/2025", "2025-12-20")]
    [InlineData("1/2/2025", "2025-02-01")]
    [InlineData("20/12/25", "2025-12-20")]
    [InlineData("20.12.2025", "2025-12-20")]
    [InlineData("08/13/2026", "2026-08-13")]   // 13 can't be a month
    [InlineData("=DATE(2026,1,5)", "2026-01-05")]
    [InlineData("5 January 2026", "2026-01-05")]
    [InlineData("31/02/2026", null)]
    [InlineData("nope", null)]
    public void Dates_parse_like_parse_api_date(string input, string? expected) =>
        Assert.Equal(expected, ReceiptParsing.ParseApiDate(input)?.ToString("yyyy-MM-dd"));

    [Fact]
    public void Payload_unwraps_the_legacy_shapes()
    {
        var fenced = JsonNode.Parse("""{"data": "```json\n{\"shop\": \"Coles\", \"items\": []}\n```"}""");
        Assert.Equal("Coles", ReceiptPayload.Normalize(fenced)["shop"]!.GetValue<string>());

        var nested = JsonNode.Parse("""{"data": {"shop": "Aldi", "items": []}}""");
        Assert.Equal("Aldi", ReceiptPayload.Normalize(nested)["shop"]!.GetValue<string>());

        Assert.Throws<PayloadException>(() => ReceiptPayload.Normalize(JsonNode.Parse("""{"foo": 1}""")));
        Assert.Throws<PayloadException>(() => ReceiptPayload.Normalize(JsonNode.Parse("""{"data": "{bad"}""")));
        Assert.Throws<PayloadException>(() => ReceiptPayload.Normalize(null));
    }

    [Fact]
    public void Validation_matches_the_serializer()
    {
        var (ok, none) = ReceiptValidator.Validate(JsonNode.Parse("""
            {"shop": " Coles ", "total": 20.1, "date": "20/12/2025",
             "items": [{"item": "Bananas", "qty": "", "price": 8.1}, {"item": "Milk"}]}
            """)!.AsObject());
        Assert.Empty(none);
        Assert.Equal("Coles", ok!.Shop);
        Assert.Equal(20.10m, ok.Total);
        Assert.Equal(1.00m, ok.Items[0].Qty);       // blank qty → "1"
        Assert.Equal(0.00m, ok.Items[1].LineTotal); // missing price → "0"

        var (_, errors) = ReceiptValidator.Validate(JsonNode.Parse("""{"shop": "", "items": "x"}""")!.AsObject());
        Assert.Equal("This field may not be blank.", errors["shop"][0]);
        Assert.Equal("This field is required.", errors["total"][0]);
        Assert.Equal("This field is required.", errors["date"][0]);
        Assert.Equal("Expected a list of items but got type \"str\".", errors["items"][0]);
    }

    [Fact]
    public void Reconcile_flags_items_that_dont_add_up()
    {
        Assert.Equal((false, ""), ReceiptReconciler.Check(20.10m, [8.10m, 12.00m]));
        Assert.Equal((false, ""), ReceiptReconciler.Check(20.10m, []));   // no items is never flagged
        Assert.Equal((true, "Items sum to 93.38, total says 84.04 (9.34 more than the total)."),
            ReceiptReconciler.Check(84.04m, [50.00m, 43.38m]));
    }

    [Fact]
    public void The_celery_message_is_what_extract_scan_delay_sends()
    {
        var message = CeleryMessage.ExtractScan(42, "extract", "task-id", "dotnet@host");
        var envelope = JsonDocument.Parse(message).RootElement;

        Assert.Equal("receipts.tasks.extract_scan", envelope.GetProperty("headers").GetProperty("task").GetString());
        Assert.Equal("task-id", envelope.GetProperty("headers").GetProperty("id").GetString());
        Assert.Equal("(42,)", envelope.GetProperty("headers").GetProperty("argsrepr").GetString());
        Assert.Equal("extract", envelope.GetProperty("properties").GetProperty("delivery_info").GetProperty("routing_key").GetString());
        Assert.Equal("base64", envelope.GetProperty("properties").GetProperty("body_encoding").GetString());
        Assert.Equal("""[[42],{},{"callbacks":null,"errbacks":null,"chain":null,"chord":null}]""",
            CeleryMessage.DecodeBody(message).ToJsonString());
    }

    [Fact]
    public void Broker_urls_parse_like_celerys()
    {
        Assert.Equal(new RedisBroker.Endpoint("localhost", 6379, 1, null, null), RedisBroker.Parse("redis://localhost:6379/1"));
        Assert.Equal(new RedisBroker.Endpoint("127.0.0.1", 6380, 0, null, "p@ss"), RedisBroker.Parse("redis://:p%40ss@127.0.0.1:6380"));
        Assert.Throws<FormatException>(() => RedisBroker.Parse("amqp://x"));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, "JPEG")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "PNG")]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 }, "HEIF")]   // ....ftypheic
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x20, 0x78, 0x6D, 0x6C, 0x6E, 0x73, 0x3D, 0x22 }, null)]   // <svg xmlns="
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A, 0, 0, 0 }, null)]   // %PDF-1.4
    public void Only_photo_formats_are_ever_decoded(byte[] head, string? expected) =>
        Assert.Equal(expected, ImageSniffer.Detect(head));

    [Theory]
    [InlineData("2026-10-26T18:42:11+11:00", "UTC", "2026-10-26T07:42:11")]
    [InlineData("2026-10-26T07:42:11Z", "UTC", "2026-10-26T07:42:11")]
    [InlineData("2026-10-26T18:42:11", "Australia/Sydney", "2026-10-26T07:42:11")]   // no offset: server's zone
    [InlineData("2026-10-26", "UTC", "2026-10-26T00:00:00")]
    [InlineData("2099-01-01T00:00:00Z", "UTC", null)]   // in the future
    [InlineData("1999-01-01T00:00:00Z", "UTC", null)]   // over ten years ago
    [InlineData("garbage", "UTC", null)]
    public void Capture_times_are_parsed_and_sanity_checked(string raw, string zone, string? expected)
    {
        var now = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        Assert.Equal(expected, ScansController.ParseCapturedAt(raw, now, tz)?.ToString("yyyy-MM-ddTHH:mm:ss"));
    }
}
