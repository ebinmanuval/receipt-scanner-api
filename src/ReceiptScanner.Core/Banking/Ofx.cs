using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ReceiptScanner.Core.Banking;

/// <summary>The file isn't OFX, or is missing something the importer requires.</summary>
public sealed class OfxException(string message) : Exception(message);

public sealed record ParsedTransaction(
    string Fitid,
    DateOnly PostedOn,
    decimal Amount,
    string Trntype = "",
    string Memo = "",
    string Name = "",
    string Checknum = "",
    DateOnly? UserDate = null);

public sealed class ParsedStatement
{
    public string BankId { get; init; } = "";
    public string AccountId { get; init; } = "";
    public string AccountType { get; init; } = "";
    /// <summary>CCACCTFROM rather than BANKACCTFROM. Card files carry no ACCTTYPE.</summary>
    public bool IsCreditCard { get; init; }
    public string Currency { get; init; } = "AUD";
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTime? ServerDate { get; init; }
    public decimal? LedgerBalance { get; set; }
    public DateTime? LedgerBalanceAt { get; set; }
    public decimal? AvailableBalance { get; set; }
    public List<ParsedTransaction> Transactions { get; } = [];
    /// <summary>STMTTRN blocks with no usable date or amount.</summary>
    public int Skipped { get; set; }
}

/// <summary>
/// Port of banking/ofx.py. OFX 1.x is SGML (leaf elements have no closing
/// tag), so it's tokenised directly rather than repaired into XML. OFX 2.x
/// (real XML) reads through the same path: a closing tag on a leaf simply
/// pops nothing.
/// </summary>
public static partial class OfxParser
{
    // Tags that hold children rather than a value. Anything else is a leaf, so
    // unknown bank extensions degrade to plain values.
    private static readonly HashSet<string> Containers =
    [
        "OFX", "SIGNONMSGSRSV1", "SONRS", "STATUS", "FI",
        "BANKMSGSRSV1", "STMTTRNRS", "STMTRS", "BANKACCTFROM", "CCACCTFROM",
        "BANKTRANLIST", "STMTTRN", "LEDGERBAL", "AVAILBAL",
        "CREDITCARDMSGSRSV1", "CCSTMTTRNRS", "CCSTMTRS", "PAYEE",
    ];

    static OfxParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // --- decoding --------------------------------------------------------------------

    /// <summary>
    /// Decode using the file's own ENCODING/CHARSET header where possible.
    /// Australian bank files are routinely CHARSET:1252 with smart quotes in
    /// the memo, which a naive UTF-8 decode mangles.
    /// </summary>
    public static string Decode(byte[] data)
    {
        var head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 2048)).ToUpperInvariant();
        var charset = CharsetHeader().Match(head);
        var encodingHeader = EncodingHeader().Match(head);

        var candidates = new List<string>();
        if (charset.Success)
        {
            var value = charset.Groups[1].Value.Trim();
            if (value is "1252" or "WINDOWS-1252") candidates.Add("windows-1252");
            else if (value is not ("NONE" or "")) candidates.Add(value);
        }
        if (encodingHeader.Success && encodingHeader.Groups[1].Value.Trim() == "UTF-8")
            candidates.Insert(0, "utf-8");
        candidates.AddRange(["utf-8", "windows-1252", "latin1"]);

        foreach (var name in candidates)
        {
            try
            {
                var encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return encoding.GetString(data);
            }
            catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException or NotSupportedException)
            {
                // try the next one
            }
        }
        return Encoding.Latin1.GetString(data);
    }

    // --- scalars -------------------------------------------------------------------------

    /// <summary>OFX datetimes: YYYYMMDD[HHMMSS[.XXX]][[+/-TZ:name]], read as UTC.</summary>
    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = BracketedZone().Replace(value.Trim(), "").Trim();
        var digits = NonDigits().Replace(text, "");
        if (digits.Length < 8) return null;

        var (format, part) = digits.Length >= 14 ? ("yyyyMMddHHmmss", digits[..14]) : ("yyyyMMdd", digits[..8]);
        return DateTime.TryParseExact(part, format, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    public static DateOnly? ParseDay(string? value) => ParseDate(value) is { } d ? DateOnly.FromDateTime(d) : null;

    /// <summary>Plain decimals, but some banks emit 1.234,56 or 1,234.56.</summary>
    public static decimal? ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().Replace(" ", "");
        if (text.Contains(',') && text.Contains('.'))
        {
            // Whichever separator is last is the decimal point.
            text = text.LastIndexOf(',') > text.LastIndexOf('.')
                ? text.Replace(".", "").Replace(",", ".")
                : text.Replace(",", "");
        }
        else if (text.Contains(','))
        {
            // A lone comma is a decimal comma when it's 1–2 digits from the end.
            var after = text.Length - text.LastIndexOf(',') - 1;
            text = after is 1 or 2 ? text.Replace(",", ".") : text.Replace(",", "");
        }

        if (!DecimalShape().IsMatch(text)) return null;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return null;
        return Quantize(amount);
    }

    private static decimal Quantize(decimal value) =>
        decimal.Parse(decimal.Round(value, 2, MidpointRounding.ToEven).ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    // --- tree --------------------------------------------------------------------------------

    /// <summary>A container element: children by tag, repeated tags in document order.</summary>
    public sealed class Node
    {
        public List<KeyValuePair<string, object>> Children { get; } = [];

        public IEnumerable<object> All(string tag) => Children.Where(c => c.Key == tag).Select(c => c.Value);
        public object? First(string tag) => Children.FirstOrDefault(c => c.Key == tag).Value;
        public string Text(string tag) => First(tag) as string ?? "";
    }

    /// <summary>Tokenise OFX markup into nested nodes.</summary>
    public static Node ParseTree(string text)
    {
        var start = text.IndexOf("<OFX>", StringComparison.Ordinal);
        if (start == -1) start = text.ToUpperInvariant().IndexOf("<OFX>", StringComparison.Ordinal);
        if (start == -1) throw new OfxException("No <OFX> element found — is this really an OFX file?");

        var root = new Node();
        // (tag, node) so a closing tag can be matched against what's actually open.
        var stack = new List<(string Tag, Node Node)> { ("", root) };

        foreach (Match m in Token().Matches(text, start))
        {
            var closing = m.Groups[1].Value.Length > 0;
            var tag = m.Groups[2].Value.ToUpperInvariant();
            var value = WebUtility.HtmlDecode(m.Groups[3].Value).Trim();

            if (closing)
            {
                // OFX 2.x closes leaves too; only pop a container that's really open.
                for (var depth = stack.Count - 1; depth > 0; depth--)
                {
                    if (stack[depth].Tag != tag) continue;
                    stack.RemoveRange(depth, stack.Count - depth);
                    break;
                }
                continue;
            }

            if (Containers.Contains(tag) && value.Length == 0)
            {
                var node = new Node();
                stack[^1].Node.Children.Add(new(tag, node));
                stack.Add((tag, node));
            }
            else
            {
                stack[^1].Node.Children.Add(new(tag, value));
            }
        }
        return root;
    }

    /// <summary>Depth-first search for the first occurrence of a tag.</summary>
    private static object? Find(Node node, string tag)
    {
        if (node.First(tag) is { } here) return here;
        foreach (var child in node.Children)
            if (child.Value is Node n && Find(n, tag) is { } found)
                return found;
        return null;
    }

    // --- the API -------------------------------------------------------------------------------

    /// <summary>Parse OFX bytes into a statement, or throw <see cref="OfxException"/>.</summary>
    public static ParsedStatement Parse(byte[] data)
    {
        var tree = ParseTree(Decode(data));

        var bankAccount = Find(tree, "BANKACCTFROM") as Node;
        var account = bankAccount ?? Find(tree, "CCACCTFROM") as Node
            ?? throw new OfxException("No account block (BANKACCTFROM/CCACCTFROM) in the file.");

        var accountId = account.Text("ACCTID").Trim();
        if (accountId.Length == 0) throw new OfxException("Account block has no ACCTID.");

        var currency = (Find(tree, "CURDEF") as string ?? "AUD").Trim();
        var statement = new ParsedStatement
        {
            BankId = account.Text("BANKID").Trim(),
            AccountId = accountId,
            AccountType = account.Text("ACCTTYPE").Trim(),
            IsCreditCard = bankAccount is null,
            Currency = currency.Length == 0 ? "AUD" : currency,
            ServerDate = ParseDate(Find(tree, "DTSERVER") as string),
        };

        var entries = new List<object>();
        if (Find(tree, "BANKTRANLIST") is Node tranList)
        {
            statement.StartDate = ParseDay(tranList.Text("DTSTART"));
            statement.EndDate = ParseDay(tranList.Text("DTEND"));
            entries.AddRange(tranList.All("STMTTRN"));
        }

        if (Find(tree, "LEDGERBAL") is Node ledger)
        {
            statement.LedgerBalance = ParseAmount(ledger.Text("BALAMT"));
            statement.LedgerBalanceAt = ParseDate(ledger.Text("DTASOF"));
        }
        if (Find(tree, "AVAILBAL") is Node available)
            statement.AvailableBalance = ParseAmount(available.Text("BALAMT"));

        foreach (var entry in entries.OfType<Node>())
        {
            if (Transaction(entry) is { } parsed) statement.Transactions.Add(parsed);
            else statement.Skipped++;
        }
        return statement;
    }

    /// <summary>
    /// One transaction, or null without a date or amount. FITID is not
    /// required: CommBank's card export leaves it empty on every purchase,
    /// and the importer identifies such a row by content instead.
    /// </summary>
    private static ParsedTransaction? Transaction(Node entry)
    {
        var posted = ParseDay(entry.Text("DTPOSTED"));
        var amount = ParseAmount(entry.Text("TRNAMT"));
        if (posted is null || amount is null) return null;

        var name = entry.Text("NAME").Trim();
        if (name.Length == 0 && entry.First("PAYEE") is Node payee) name = payee.Text("NAME").Trim();

        return new ParsedTransaction(
            Cut(entry.Text("FITID").Trim(), 255),
            posted.Value,
            amount.Value,
            Cut(entry.Text("TRNTYPE").Trim(), 20),
            Cut(entry.Text("MEMO").Trim(), 512),
            Cut(name, 255),
            Cut(entry.Text("CHECKNUM").Trim(), 64),
            ParseDay(entry.Text("DTUSER")));
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;

    [GeneratedRegex(@"<\s*(/?)\s*([A-Za-z0-9._]+)\s*>([^<]*)")]
    private static partial Regex Token();

    [GeneratedRegex(@"CHARSET\s*:\s*(\S+)")]
    private static partial Regex CharsetHeader();

    [GeneratedRegex(@"ENCODING\s*:\s*(\S+)")]
    private static partial Regex EncodingHeader();

    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex BracketedZone();

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();

    [GeneratedRegex(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")]
    private static partial Regex DecimalShape();
}
