using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReceiptScanner.Core.Banking;

/// <summary>
/// Port of banking/identity.py: what counts as "the same transaction".
/// </summary>
/// <remarks>
/// Most banks keep FITID stable; ANZ's is the posting date plus the row's
/// position in the download, so the same payment gets a new FITID in every
/// export. Accounts therefore choose a strategy: <c>fitid</c> trusts the
/// bank, <c>content</c> keys on (posted_on, amount, memo) plus an occurrence
/// number so a genuine second identical payment on the same day is kept.
/// </remarks>
public static partial class Identity
{
    public const int DigestLength = 32;

    /// <summary>Collapse whitespace and case. Banks pad memos to widths that vary between exports.</summary>
    public static string NormaliseMemo(string? memo) => Whitespace().Replace(memo ?? "", " ").Trim().ToUpperInvariant();

    /// <summary>A stable digest of the three fields a bank can't renumber.</summary>
    public static string Signature(DateOnly postedOn, decimal amount, string? memo)
    {
        var amountText = decimal.Round(amount, 2, MidpointRounding.ToEven).ToString("F2", CultureInfo.InvariantCulture);
        var raw = string.Join("|", postedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), amountText, NormaliseMemo(memo));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..DigestLength];
    }

    public static string ContentKey(DateOnly postedOn, decimal amount, string? memo, int occurrence = 1) =>
        $"{Signature(postedOn, amount, memo)}#{occurrence}";

    // How much of a file must match before we say anything (ANZ detection).
    public const double OrdinalThreshold = 0.8;
    public const int OrdinalMinRows = 5;

    /// <summary>
    /// Is this file numbering rows rather than identifying transactions?
    /// ANZ: FITID = DTPOSTED + "000" + 1-based row number. Only a signal: it
    /// never changes an account's strategy by itself.
    /// </summary>
    public static bool LooksLikeOrdinalFitid(IEnumerable<ParsedTransaction> transactions)
    {
        var rows = transactions.Where(t => t.Fitid.Length > 0).ToList();
        if (rows.Count < OrdinalMinRows) return false;

        var matches = rows.Where((t, i) => t.Fitid == $"{t.PostedOn:yyyyMMdd}000{i + 1}").Count();
        return (double)matches / rows.Count >= OrdinalThreshold;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
