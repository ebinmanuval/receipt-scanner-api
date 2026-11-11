using System.Globalization;
using System.Text.RegularExpressions;

namespace ReceiptScanner.Core.Receipts;

/// <summary>Port of receipts/parsing.py: loose money and date input.</summary>
public static partial class ReceiptParsing
{
    /// <summary>
    /// Parse loose money/quantity input, quantised to <paramref name="places"/>
    /// (half-to-even, like Python's Decimal.quantize). Null if unparseable.
    /// "£1,234.50" → 1234.50.
    /// </summary>
    public static decimal? CleanDecimal(string? text, int places = 2)
    {
        if (text is null) return null;
        text = text.Trim();
        if (text.Length == 0) return null;

        var raw = MoneyClean().Replace(text.Replace(",", ""), "");
        if (raw is "" or "-" or "." or "-.") return null;
        // Python's Decimal() accepts exactly one sign at the start and one dot.
        if (!DecimalShape().IsMatch(raw)) return null;
        if (!decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
            return null;
        // Round, then fix the scale too (1234.5 → 1234.50), as quantize does.
        var rounded = decimal.Round(value, places, MidpointRounding.ToEven);
        return decimal.Parse(rounded.ToString("F" + places, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Formats tried in order, ISO first (parsing.py API_DATE_FORMATS).</summary>
    private static readonly string[] ApiDateFormats =
        ["yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "yyyy/MM/dd", "dd.MM.yyyy", "dd/MM/yy"];

    private static readonly string[] TextDateFormats =
        ["d MMMM yyyy", "MMMM d, yyyy", "d MMM yyyy", "MMM d, yyyy"];

    /// <summary>parse_api_date: the purchase date. ISO is canonical; dd/mm/yyyy and friends also work.</summary>
    public static DateOnly? ParseApiDate(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        // A two-digit year (20/12/25) means 2025. Python's %Y would read it as
        // year 25 AD; that's a Django bug not worth porting.
        if (TwoDigitYear().IsMatch(text))
            return ParseDateComponents(text, dayFirst: true);

        foreach (var format in ApiDateFormats)
        {
            // Python's %d/%m accept one or two digits; so do d/M here.
            var lenient = format.Replace("dd", "d").Replace("MM", "M");
            if (DateOnly.TryParseExact(text, [format, lenient], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
        }
        return ParseDateComponents(text, dayFirst: true);
    }

    /// <summary>parse_date_components: ambiguous orders, =DATE(y,m,d), long-form dates.</summary>
    public static DateOnly? ParseDateComponents(string text, bool dayFirst = true)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        var formula = SheetDateFormula().Match(text);
        if (formula.Success)
            return SafeDate(int.Parse(formula.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(formula.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(formula.Groups[3].Value, CultureInfo.InvariantCulture));

        if (DateOnly.TryParseExact(text, TextDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var named))
            return named;

        var parts = DateSplit().Split(text).Where(p => p.Length > 0).ToArray();
        if (parts.Length != 3 || !parts.All(p => p.All(char.IsAsciiDigit))) return null;

        if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var a)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var b)
            || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var c))
            return null;

        int y, m, day;
        if (parts[0].Length == 4)
        {
            (y, m, day) = (a, b, c);
        }
        else
        {
            y = c;
            if (a > 12 && b <= 12) (day, m) = (a, b);
            else if (b > 12 && a <= 12) (m, day) = (a, b);
            else (day, m) = dayFirst ? (a, b) : (b, a);
            if (parts[2].Length <= 2) y += y < 70 ? 2000 : 1900;
        }
        return SafeDate(y, m, day);
    }

    private static DateOnly? SafeDate(int y, int m, int d)
    {
        if (y is < 1 or > 9999 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return null;
        return new DateOnly(y, m, d);
    }

    /// <summary>Python's str() of a 2-place Decimal, e.g. "20.10".</summary>
    public static string Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.ToEven).ToString("F2", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"[^\d.\-]")]
    private static partial Regex MoneyClean();

    [GeneratedRegex(@"^-?(\d+\.?\d*|\.\d+)$")]
    private static partial Regex DecimalShape();

    [GeneratedRegex(@"^=\s*DATE\s*\(\s*(\d{1,4})\s*,\s*(\d{1,2})\s*,\s*(\d{1,2})\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SheetDateFormula();

    [GeneratedRegex(@"^\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2}$")]
    private static partial Regex TwoDigitYear();

    [GeneratedRegex(@"[/\-.]")]
    private static partial Regex DateSplit();
}
