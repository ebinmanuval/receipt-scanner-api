using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReceiptScanner.Core.Receipts;

public sealed record ValidatedItem(string Name, decimal Qty, decimal LineTotal);

public sealed record ValidatedReceipt(
    string Shop, decimal Total, DateOnly PurchasedOn, Guid? ClientUuid, IReadOnlyList<ValidatedItem> Items);

public sealed class PayloadException(string message) : Exception(message);

/// <summary>Port of receipts/payload.py: the loose bodies the iOS app and Shortcut send.</summary>
public static partial class ReceiptPayload
{
    /// <summary>
    /// Reduce a body to the receipt object. Handles a plain receipt object,
    /// <c>{"data": "&lt;json string&gt;"}</c>, <c>{"data": {...}}</c> and
    /// Markdown code fences around JSON (left by an LLM).
    /// </summary>
    public static JsonObject Normalize(JsonNode? body, int depth = 0)
    {
        if (depth > 5) throw new PayloadException("Payload nesting is too deep.");

        if (body is JsonValue value && value.TryGetValue<string>(out var text))
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(StripCodeFences(text));
            }
            catch (JsonException ex)
            {
                throw new PayloadException($"Body is not valid JSON: {ex.Message}");
            }
            return Normalize(parsed, depth + 1);
        }

        if (body is not JsonObject obj) throw new PayloadException("Expected a JSON object.");

        if (obj.ContainsKey("shop") || obj.ContainsKey("items")) return obj;
        if (obj.ContainsKey("data")) return Normalize(obj["data"], depth + 1);

        throw new PayloadException("Invalid payload: expected a 'data' key or receipt fields.");
    }

    public static string StripCodeFences(string text) => CodeFence().Replace(text.Trim(), "").Trim();

    [GeneratedRegex(@"^```[\w-]*\s*|\s*```$")]
    private static partial Regex CodeFence();
}

/// <summary>
/// Port of ReceiptSerializer's validation (receipts/serializers.py). Errors
/// come back flattened the way config/api.py normalise_errors does it:
/// <c>{field: [messages]}</c>, with item errors as "price: …" under "items".
/// </summary>
public static class ReceiptValidator
{
    public const string Required = "This field is required.";
    public const string NotNull = "This field may not be null.";
    public const string NotBlank = "This field may not be blank.";
    public const string NotString = "Not a valid string.";
    public const string DuplicateClientUuid = "receipt with this client uuid already exists.";

    public static (ValidatedReceipt? Receipt, Dictionary<string, string[]> Errors) Validate(JsonObject data)
    {
        var errors = new Dictionary<string, string[]>();
        void Fail(string field, params string[] messages) => errors[field] = messages;

        // shop: CharField(max_length=255), trimmed, not blank.
        string? shop = null;
        switch (Char(data, "shop", out var shopText))
        {
            case CharResult.Ok when shopText!.Length > 255:
                Fail("shop", "Ensure this field has no more than 255 characters.");
                break;
            case CharResult.Ok: shop = shopText; break;
            case var r: Fail("shop", Message(r)); break;
        }

        // total: CharField, then parse_money.
        decimal? total = null;
        switch (Char(data, "total", out var totalText))
        {
            case CharResult.Ok:
                total = ReceiptParsing.CleanDecimal(totalText);
                if (total is null) Fail("total", $"Not a valid amount: {Repr(totalText)}");
                break;
            case var r: Fail("total", Message(r)); break;
        }

        // date: FlexibleDateField.
        DateOnly? date = null;
        if (!data.TryGetPropertyValue("date", out var dateNode)) Fail("date", Required);
        else if (dateNode is null) Fail("date", NotNull);
        else if (!IsString(dateNode, out var dateText) || string.IsNullOrWhiteSpace(dateText)) Fail("date", "A date string is required.");
        else
        {
            date = ReceiptParsing.ParseApiDate(dateText);
            if (date is null)
                Fail("date", $"Unrecognized date {Repr(dateText)}. Use ISO yyyy-mm-dd (dd/mm/yyyy is also accepted).");
        }

        // client_uuid: optional UUID.
        Guid? clientUuid = null;
        if (data.TryGetPropertyValue("client_uuid", out var uuidNode) && uuidNode is not null)
        {
            var uuidText = uuidNode is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : uuidNode.ToJsonString();
            if (uuidText.Length > 0)
            {
                if (Guid.TryParse(uuidText, out var g)) clientUuid = g;
                else Fail("client_uuid", "Must be a valid UUID.");
            }
        }

        // items: a list of {item, qty, price}.
        var items = new List<ValidatedItem>();
        if (!data.TryGetPropertyValue("items", out var itemsNode)) Fail("items", Required);
        else if (itemsNode is null) Fail("items", NotNull);
        else if (itemsNode is not JsonArray array) Fail("items", $"Expected a list of items but got type \"{PythonType(itemsNode)}\".");
        else
        {
            var itemErrors = new List<string>();
            foreach (var node in array)
            {
                if (node is not JsonObject item)
                {
                    itemErrors.Add($"non_field_errors: Invalid data. Expected a dictionary, but got {PythonType(node)}.");
                    continue;
                }
                var parsed = ValidateItem(item, itemErrors);
                if (parsed is not null) items.Add(parsed);
            }
            if (itemErrors.Count > 0) Fail("items", [.. itemErrors]);
        }

        if (errors.Count > 0) return (null, errors);
        return (new ValidatedReceipt(shop!, total!.Value, date!.Value, clientUuid, items), errors);
    }

    private static ValidatedItem? ValidateItem(JsonObject item, List<string> errors)
    {
        var before = errors.Count;

        // item: CharField(max_length=255, allow_blank=True)
        string? name = null;
        switch (Char(item, "item", out var nameText, allowBlank: true))
        {
            case CharResult.Ok when nameText!.Length > 255:
                errors.Add("item: Ensure this field has no more than 255 characters.");
                break;
            case CharResult.Ok: name = nameText; break;
            case var r: errors.Add($"item: {Message(r)}"); break;
        }

        // qty / price: optional, blank allowed; parse_money(value or default)
        decimal? qty = OptionalMoney(item, "qty", "1", errors);
        decimal? price = OptionalMoney(item, "price", "0", errors);

        return errors.Count == before ? new ValidatedItem(name!, qty!.Value, price!.Value) : null;
    }

    private static decimal? OptionalMoney(JsonObject item, string field, string fallback, List<string> errors)
    {
        string text;
        if (!item.TryGetPropertyValue(field, out var node)) text = fallback;
        else if (node is null) { errors.Add($"{field}: {NotNull}"); return null; }
        else if (!TryCharValue(node, out var t)) { errors.Add($"{field}: {NotString}"); return null; }
        else text = t.Trim();

        // parse_money: empty → 0.00, except qty, whose validator substitutes "1" first.
        if (text.Length == 0) text = field == "qty" ? "1" : "0";
        var value = ReceiptParsing.CleanDecimal(text);
        if (value is null) errors.Add($"{field}: Not a valid amount: {Repr(text)}");
        return value;
    }

    // --- DRF CharField semantics ---------------------------------------------

    private enum CharResult { Ok, Missing, Null, Blank, NotString }

    private static CharResult Char(JsonObject obj, string field, out string? value, bool allowBlank = false)
    {
        value = null;
        if (!obj.TryGetPropertyValue(field, out var node)) return CharResult.Missing;
        if (node is null) return CharResult.Null;
        if (!TryCharValue(node, out var text)) return CharResult.NotString;
        text = text.Trim();   // trim_whitespace=True
        if (text.Length == 0 && !allowBlank) return CharResult.Blank;
        value = text;
        return CharResult.Ok;
    }

    private static string Message(CharResult r) => r switch
    {
        CharResult.Missing => Required,
        CharResult.Null => NotNull,
        CharResult.Blank => NotBlank,
        _ => NotString,
    };

    /// <summary>CharField accepts strings and numbers (as their text), not bools, lists or objects.</summary>
    private static bool TryCharValue(JsonNode node, out string text)
    {
        text = "";
        if (node is not JsonValue v) return false;
        switch (v.GetValueKind())
        {
            case JsonValueKind.String: text = v.GetValue<string>(); return true;
            case JsonValueKind.Number: text = v.ToJsonString(); return true;
            default: return false;
        }
    }

    private static bool IsString(JsonNode node, out string text)
    {
        text = "";
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            text = v.GetValue<string>();
            return true;
        }
        return false;
    }

    private static string PythonType(JsonNode? node) => node switch
    {
        null => "NoneType",
        JsonObject => "dict",
        JsonArray => "list",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "str",
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Number => v.ToJsonString().Contains('.') ? "float" : "int",
            _ => "str",
        },
        _ => "str",
    };

    /// <summary>Python's repr() of a string, near enough: single-quoted.</summary>
    internal static string Repr(string? text) => text is null ? "None" : $"'{text.Replace("\\", "\\\\").Replace("'", "\\'")}'";
}

/// <summary>Port of Receipt.reconcile(): do the items add up to the total?</summary>
public static class ReceiptReconciler
{
    public const decimal DefaultTolerance = 0.01m;

    /// <returns>(needs_review, note). No items is never flagged: shop + total only is a deliberate choice.</returns>
    public static (bool NeedsReview, string Note) Check(decimal total, IReadOnlyCollection<decimal> lineTotals, decimal tolerance = DefaultTolerance)
    {
        if (lineTotals.Count == 0) return (false, "");

        var itemsTotal = lineTotals.Sum();
        var gap = decimal.Round(total - itemsTotal, 2, MidpointRounding.ToEven);
        if (Math.Abs(gap) <= tolerance) return (false, "");

        var direction = gap > 0 ? "missing from items" : "more than the total";
        return (true,
            $"Items sum to {ReceiptParsing.Money(itemsTotal)}, total says {ReceiptParsing.Money(total)} " +
            $"({ReceiptParsing.Money(Math.Abs(gap))} {direction}).");
    }
}

/// <summary>Port of scan_views._totals: do a proposed receipt's items add up?</summary>
public static class ProposedTotals
{
    public sealed record Totals(string Stated, string ItemsSum, string Discrepancy, bool Reconciles);

    public static Totals From(JsonElement proposed)
    {
        var stated = Dec(proposed.TryGetProperty("total", out var t) ? t : default);
        var sum = 0m;
        if (proposed.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("price", out var p))
                    sum += Dec(p);

        stated = decimal.Round(stated, 2, MidpointRounding.ToEven);
        sum = decimal.Round(sum, 2, MidpointRounding.ToEven);
        var gap = stated - sum;
        return new Totals(ReceiptParsing.Money(stated), ReceiptParsing.Money(sum), ReceiptParsing.Money(gap), Math.Abs(gap) < 0.01m);
    }

    /// <summary>Decimal(str(v)), or 0 when that fails.</summary>
    private static decimal Dec(JsonElement v)
    {
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return decimal.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }
}
