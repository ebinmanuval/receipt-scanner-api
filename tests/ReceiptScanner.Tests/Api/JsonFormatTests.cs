using System.Text.Json;
using ReceiptScanner.Api.Json;

namespace ReceiptScanner.Tests.Api;

public class JsonFormatTests
{
    private static readonly JsonSerializerOptions Options = ApiJson.Create();

    private sealed class Line
    {
        public long Id { get; set; }
        public string Item { get; set; } = "";
        [DecimalString(3)] public decimal Qty { get; set; }
        public decimal Price { get; set; }
        [DecimalString(4)] public decimal? UnitPrice { get; set; }
        public DateOnly Date { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ReviewedAt { get; set; }
    }

    [Fact]
    public void Serialises_in_the_API_md_shape()
    {
        var json = JsonSerializer.Serialize(new Line
        {
            Id = 1,
            Item = "Fuel",
            Qty = 36.14m,
            Price = 70.08m,
            UnitPrice = 1.93913m,
            Date = new DateOnly(2025, 12, 20),
            CreatedAt = new DateTime(2026, 9, 20, 7, 16, 46, 789, DateTimeKind.Utc),
        }, Options);

        Assert.Equal(
            """{"id":1,"item":"Fuel","qty":"36.140","price":"70.08","unit_price":"1.9391","date":"2025-12-20","created_at":"2026-09-20T07:16:46Z","reviewed_at":null}""",
            json);
    }

    [Fact]
    public void Unspecified_kind_is_treated_as_utc()
    {
        var value = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        Assert.Equal("2026-01-02T03:04:05Z", UtcDateTimeConverter.ToWire(value));
    }

    [Theory]
    [InlineData("0.125", 2, "0.12")]   // half-to-even, like Decimal.quantize
    [InlineData("0.135", 2, "0.14")]
    [InlineData("8.1", 4, "8.1000")]
    [InlineData("-20.1", 2, "-20.10")]
    public void Money_rounds_like_python(string input, int places, string expected) =>
        Assert.Equal(expected, DecimalText.Format(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture), places));

    [Fact]
    public void Reads_money_from_string_or_number_and_dates_in_either_format()
    {
        var line = JsonSerializer.Deserialize<Line>(
            """{"qty":"2","price":12.5,"date":"20/12/2025","created_at":"2026-09-20T17:16:46+10:00"}""", Options)!;

        Assert.Equal(2m, line.Qty);
        Assert.Equal(12.5m, line.Price);
        Assert.Equal(new DateOnly(2025, 12, 20), line.Date);
        Assert.Equal(new DateTime(2026, 9, 20, 7, 16, 46, DateTimeKind.Utc), line.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, line.CreatedAt.Kind);
    }

    [Fact]
    public void Rejects_a_bad_amount()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Line>("""{"price":"abc"}""", Options));
    }
}
