using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReceiptScanner.Api.Json;

/// <summary>
/// The wire format from API.md: snake_case keys, money as strings with fixed
/// places, dates as yyyy-MM-dd, instants as ISO UTC with whole seconds.
/// </summary>
public static class ApiJson
{
    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.DictionaryKeyPolicy = null;                     // {field: [...]} keys are already API names
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        o.NumberHandling = JsonNumberHandling.Strict;
        // Like DRF (UNICODE_JSON): "›" and "&" go out as-is, not as \u escapes.
        o.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

        // Order matters only in that property-level [DecimalString(n)] beats these.
        o.Converters.Add(new DecimalStringConverter(2));  // money is the common case
        o.Converters.Add(new NullableDecimalStringConverter(2));
        o.Converters.Add(new UtcDateTimeConverter());
        o.Converters.Add(new NullableUtcDateTimeConverter());
        o.Converters.Add(new IsoDateOnlyConverter());
    }

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(o);
        return o;
    }
}

/// <summary>
/// Put on a decimal property to serialise it with a fixed number of places:
/// 2 for money (the default anyway), 3 for qty, 4 for unit price.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class DecimalStringAttribute(int places) : JsonConverterAttribute
{
    public int Places { get; } = places;

    public override JsonConverter? CreateConverter(Type typeToConvert) =>
        typeToConvert == typeof(decimal) ? new DecimalStringConverter(Places)
        : typeToConvert == typeof(decimal?) ? new NullableDecimalStringConverter(Places)
        : throw new InvalidOperationException($"[DecimalString] only applies to decimal, not {typeToConvert}.");
}

/// <summary>
/// decimal ↔ "12.30". Rounds half-to-even, like Python's Decimal.quantize.
/// Accepts a JSON string or number on input.
/// </summary>
public sealed class DecimalStringConverter(int places) : JsonConverter<decimal>
{
    public int Places { get; } = places;

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DecimalText.Read(ref reader);

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(DecimalText.Format(value, Places));
}

public sealed class NullableDecimalStringConverter(int places) : JsonConverter<decimal?>
{
    public int Places { get; } = places;

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : DecimalText.Read(ref reader);

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is { } v) writer.WriteStringValue(DecimalText.Format(v, Places));
        else writer.WriteNullValue();
    }
}

public static class DecimalText
{
    public static string Format(decimal value, int places) =>
        decimal.Round(value, places, MidpointRounding.ToEven)
            .ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    internal static decimal Read(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetDecimal();
        if (reader.TokenType == JsonTokenType.String
            && decimal.TryParse(reader.GetString()?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return d;
        throw new JsonException("Not a valid amount.");
    }
}

/// <summary>Instants as <c>2026-09-20T07:16:46Z</c>: UTC, whole seconds, no fraction.</summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWire(value));

    public static string ToWire(DateTime value)
    {
        // Unspecified is treated as UTC: that's how Django stores datetimes
        // and how the DbContext stamps them on read.
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString(Format, CultureInfo.InvariantCulture);
    }

    internal static DateTime Parse(string? text)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        throw new JsonException($"Unrecognized datetime '{text}'. Use ISO 8601.");
    }
}

public sealed class NullableUtcDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : UtcDateTimeConverter.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is { } v) writer.WriteStringValue(UtcDateTimeConverter.ToWire(v));
        else writer.WriteNullValue();
    }
}

/// <summary>
/// Calendar dates as yyyy-MM-dd. dd/MM/yyyy is still accepted on input
/// (the iOS Shortcut sends it), as API.md promises.
/// </summary>
public sealed class IsoDateOnlyConverter : JsonConverter<DateOnly>
{
    private static readonly string[] Formats = ["yyyy-MM-dd", "dd/MM/yyyy"];

    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString()?.Trim();
        if (DateOnly.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        throw new JsonException($"Unrecognized date '{text}'. Use ISO yyyy-mm-dd (dd/mm/yyyy is also accepted).");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}
