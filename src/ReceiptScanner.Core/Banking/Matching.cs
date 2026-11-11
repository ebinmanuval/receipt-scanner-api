using System.Text.RegularExpressions;

namespace ReceiptScanner.Core.Banking;

/// <summary>
/// Port of banking/matching.py: which bank transaction paid for a receipt.
/// </summary>
/// <remarks>
/// The merchant is anchored to the start of the memo. A substring search for
/// "Woolworths" would match "Direct Debit 438876 WOOLWORTHS INSUR", and a
/// grocery receipt would be linked to an insurance payment. Dates use
/// user_date (DTUSER, when the purchase happened) over posted_on, which lags.
/// </remarks>
public static partial class Matching
{
    public const string Exact = "exact";
    public const string High = "high";
    public const string Possible = "possible";

    public const string Prefix = "prefix";
    public const string Contains = "contains";
    public const string None = "none";

    private static readonly HashSet<string> Stopwords =
        ["PTY", "LTD", "THE", "AND", "OF", "AUS", "AU", "NSW", "NS", "PL", "INC"];

    /// <summary>Upper-case alphanumeric tokens: "7-Eleven Metford" → [7, ELEVEN, METFORD].</summary>
    public static List<string> Normalize(string? text) =>
        NonAlphanumeric().Replace(text ?? "", " ").ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The merchant portion of a memo: card number, value date, state and country stripped.</summary>
    public static string MemoMerchant(string? memo)
    {
        var text = MemoTail().Replace(memo ?? "", "").Trim();
        string? previous = null;
        while (previous != text)   // "NS AUS" needs two passes
        {
            previous = text;
            text = TrailingLocale().Replace(text, "").Trim();
        }
        return text;
    }

    /// <summary>False for direct debits, transfers, ATM withdrawals, fees, salary and other non-shop lines.</summary>
    public static bool IsPurchase(string? memo) => !NonPurchase().IsMatch(memo ?? "");

    /// <summary>
    /// How well a receipt's shop matches a memo: "prefix", "contains" or
    /// "none". Only "prefix" can earn an automatic link.
    /// </summary>
    public static string MerchantMatch(string? shop, string? memo)
    {
        var shopTokens = Normalize(shop).Where(t => !Stopwords.Contains(t)).ToList();
        var memoTokens = Normalize(MemoMerchant(memo)).Where(t => !Stopwords.Contains(t)).ToList();
        if (shopTokens.Count == 0 || memoTokens.Count == 0) return None;

        if (shopTokens[0] == memoTokens[0])
        {
            // A long first token is distinctive by itself; a short one like
            // "BIG" needs corroboration, or "BIG W" would match "BIG BANANA".
            var rest = shopTokens.Skip(1).ToList();
            return rest.Count == 0 || rest.Any(memoTokens.Contains) || shopTokens[0].Length >= 5 ? Prefix : Contains;
        }
        return shopTokens.Any(memoTokens.Contains) ? Contains : None;
    }

    /// <summary>A chain name for grouping spend: everything up to the first number.</summary>
    public static string MerchantKey(string? memo, int maxWords = 3)
    {
        var words = new List<string>();
        foreach (var token in Normalize(MemoMerchant(memo)))
        {
            if (token.All(char.IsAsciiDigit)) break;
            if (Stopwords.Contains(token)) continue;
            words.Add(token);
            if (words.Count >= maxWords) break;
        }
        return string.Join(' ', words);
    }

    /// <summary>DTUSER is when the purchase happened; DTPOSTED is when it cleared.</summary>
    public static DateOnly TransactionDate(DateOnly postedOn, DateOnly? userDate) => userDate ?? postedOn;

    [GeneratedRegex(@"\s+(card\s+xx.*|value\s+date:.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex MemoTail();

    [GeneratedRegex(@"\s+(AUS?|NSW?|VIC|QLD|SA|WA|TAS|NT|ACT)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingLocale();

    // Money moved rather than spent at a merchant. Anchored: "WOOLWORTHS …
    // Cash Out 20" is a real purchase that happens to include cash out.
    [GeneratedRegex(@"^\s*(direct\s+debit|transfer\s+(to|from)|bpay|interest|wdl\b|atm\s|non\s+cba\s+atm|withdrawal|cash\s+advance|fee\b|salary|payroll|loan\s|repayment)", RegexOptions.IgnoreCase)]
    private static partial Regex NonPurchase();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}

/// <summary>One transaction that could have paid for a receipt.</summary>
public sealed record Candidate(long TransactionId, int DaysApart, string ShopMatch)
{
    public string Confidence => ShopMatch == Matching.Prefix
        ? (DaysApart == 0 ? Matching.Exact : Matching.High)
        : Matching.Possible;

    /// <summary>The sentence shown under a suggestion, so it's auditable rather than magic.</summary>
    public string Note
    {
        get
        {
            var when = DaysApart == 0 ? "same day" : $"{DaysApart} day(s) apart";
            var shop = ShopMatch switch
            {
                Matching.Prefix => "shop matches memo",
                Matching.Contains => "shop appears in memo",
                _ => "shop not in memo",
            };
            return $"{when}, {shop}";
        }
    }

    internal int Rank => Confidence switch { Matching.Exact => 0, Matching.High => 1, _ => 2 };
}

/// <summary>The candidates for one receipt, and what they add up to.</summary>
public sealed class CandidateSet(IEnumerable<Candidate> candidates)
{
    public const string Auto = "auto";
    public const string Ambiguous = "ambiguous";
    public const string Weak = "weak";
    public const string NoCandidate = "none";

    /// <summary>In the order found (newest transaction first), which breaks ties for <see cref="Best"/>.</summary>
    public List<Candidate> Candidates { get; } = candidates.ToList();

    public Candidate? Best => Candidates.OrderBy(c => c.Rank).ThenBy(c => c.DaysApart).FirstOrDefault();

    /// <summary>More than one candidate at the best confidence. Never resolved by guessing.</summary>
    public bool IsAmbiguous => Best is { } best && Candidates.Count(c => c.Confidence == best.Confidence) > 1;

    public bool ShouldAutoLink => Best is { } best && best.Confidence is Matching.Exact or Matching.High && !IsAmbiguous;

    /// <summary>auto / ambiguous / weak / none.</summary>
    public string Bucket => Best switch
    {
        null => NoCandidate,
        _ when IsAmbiguous => Ambiguous,
        { Confidence: Matching.Possible } => Weak,
        _ => Auto,
    };

    /// <summary>Best first, ties by transaction id so the list doesn't shuffle between refreshes.</summary>
    public IEnumerable<Candidate> Ordered => Candidates.OrderBy(c => c.Rank).ThenBy(c => c.DaysApart).ThenBy(c => c.TransactionId);

    public CandidateSet Without(IReadOnlySet<long> claimed) => new(Candidates.Where(c => !claimed.Contains(c.TransactionId)));
}
