using System.Text;
using ReceiptScanner.Core.Banking;

namespace ReceiptScanner.Tests.Core.Banking;

/// <summary>Port of banking/test_ofx.py. All input is fabricated (see <see cref="OfxFixtures"/>).</summary>
public class OfxParserTests
{
    // --- scalars ---------------------------------------------------------------------------

    [Fact]
    public void Dates()
    {
        Assert.Equal(new DateOnly(2026, 9, 17), OfxParser.ParseDay("20260917"));
        Assert.Equal(new DateTime(2026, 9, 17, 12, 44, 30), OfxParser.ParseDate("20260917124430"));
    }

    [Fact]
    public void Date_with_bracketed_timezone() =>
        Assert.Equal(new DateOnly(2026, 9, 17), OfxParser.ParseDay("20260917120000.000[-5:EST]"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026")]
    [InlineData("notadate")]
    [InlineData("0")]
    public void Bad_dates(string junk) => Assert.Null(OfxParser.ParseDay(junk));

    [Theory]
    [InlineData("-12.34", "-12.34")]
    [InlineData("1500", "1500.00")]
    [InlineData("+8.50", "8.50")]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("-12,34", "-12.34")]
    public void Amounts(string raw, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), OfxParser.ParseAmount(raw));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("--")]
    [InlineData(null)]
    public void Bad_amounts(string? junk) => Assert.Null(OfxParser.ParseAmount(junk));

    [Fact]
    public void Decode_honours_cp1252_charset()
    {
        var raw = OfxFixtures.Build([new("T1", "20260901", "", "-1.00", "DEBIT", "CAFÉ NOIR")]);
        Assert.Contains("CAFÉ NOIR", OfxParser.Decode(raw));
    }

    [Fact]
    public void Decode_utf8() => Assert.Contains("<OFX>", OfxParser.Decode(OfxFixtures.BuildV2()));

    // --- parsing ---------------------------------------------------------------------------

    [Fact]
    public void Account_and_statement_metadata()
    {
        var stmt = OfxParser.Parse(OfxFixtures.Build());
        Assert.Equal("062807", stmt.BankId);
        Assert.Equal("12345678", stmt.AccountId);
        Assert.Equal("SAVINGS", stmt.AccountType);
        Assert.Equal("AUD", stmt.Currency);
        Assert.Equal(new DateOnly(2026, 9, 1), stmt.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 30), stmt.EndDate);
        Assert.Equal(1482.66m, stmt.LedgerBalance);
        Assert.Equal(1482.66m, stmt.AvailableBalance);
        Assert.Equal(2026, stmt.ServerDate!.Value.Year);
        Assert.False(stmt.IsCreditCard);
    }

    [Fact]
    public void Transactions()
    {
        var stmt = OfxParser.Parse(OfxFixtures.Build());
        Assert.Equal(3, stmt.Transactions.Count);
        var first = stmt.Transactions[0];
        Assert.Equal("T0001", first.Fitid);
        Assert.Equal(new DateOnly(2026, 9, 1), first.PostedOn);
        Assert.Equal(-12.34m, first.Amount);
        Assert.Equal("DEBIT", first.Trntype);
        Assert.Equal("COLES 1234 SYDNEY", first.Memo);
        Assert.Equal(new DateOnly(2026, 9, 1), first.UserDate);
        Assert.Equal(new[] { -12.34m, -5.00m, 1500.00m }, stmt.Transactions.Select(t => t.Amount));
    }

    [Fact]
    public void Single_transaction_is_not_mistaken_for_a_list()
    {
        var stmt = OfxParser.Parse(OfxFixtures.Build([new("S1", "20260901", "", "-1.00", "DEBIT", "One")]));
        Assert.Equal("S1", Assert.Single(stmt.Transactions).Fitid);
    }

    [Fact]
    public void No_transactions()
    {
        var stmt = OfxParser.Parse(OfxFixtures.Build([]));
        Assert.Empty(stmt.Transactions);
        Assert.Equal("12345678", stmt.AccountId);
    }

    [Fact]
    public void Missing_availbal_is_fine()
    {
        var stmt = OfxParser.Parse(OfxFixtures.Build(includeAvailBal: false));
        Assert.Null(stmt.AvailableBalance);
        Assert.Equal(1482.66m, stmt.LedgerBalance);
    }

    [Fact]
    public void Ofx_v2_xml_parses_too()
    {
        var stmt = OfxParser.Parse(OfxFixtures.BuildV2());
        Assert.Equal("12345678", stmt.AccountId);
        Assert.Equal(3, stmt.Transactions.Count);
        Assert.Equal(1500.00m, stmt.Transactions[2].Amount);
    }

    [Fact]
    public void Credit_card_statement_is_flagged()
    {
        var stmt = OfxParser.Parse(OfxFixtures.Build(creditCard: true, acctId: "4000123412341234"));
        Assert.True(stmt.IsCreditCard);
        Assert.Equal("4000123412341234", stmt.AccountId);
        Assert.Equal(3, stmt.Transactions.Count);
    }

    [Fact]
    public void Transaction_missing_required_fields_is_dropped()
    {
        var stmt = OfxParser.Parse(OfxFixtures.Build(
        [
            new("T1", "20260901", "", "-1.00", "DEBIT", "good"),
            new("", "20260902", "", "-2.00", "DEBIT", "no fitid"),
            new("T3", "", "", "-3.00", "DEBIT", "no date"),
            new("T4", "20260904", "", "", "DEBIT", "no amount"),
        ]));
        // No FITID is not a reason to drop a row (CommBank's card export
        // leaves it empty on every purchase); no date or amount is.
        Assert.Equal(new[] { "good", "no fitid" }, stmt.Transactions.Select(t => t.Memo));
        Assert.Equal("", stmt.Transactions[1].Fitid);
        Assert.Equal(2, stmt.Skipped);
    }

    [Fact]
    public void Memo_with_ampersand_entity()
    {
        var stmt = OfxParser.Parse(OfxFixtures.Build([new("T1", "20260901", "", "-1.00", "DEBIT", "BEN &amp; JERRY")]));
        Assert.Equal("BEN & JERRY", stmt.Transactions[0].Memo);
    }

    [Fact]
    public void Long_values_are_truncated_not_rejected()
    {
        var stmt = OfxParser.Parse(OfxFixtures.Build([new(new string('F', 400), "20260901", "", "-1.00", "DEBIT", new string('x', 900))]));
        Assert.Equal(255, stmt.Transactions[0].Fitid.Length);
        Assert.Equal(512, stmt.Transactions[0].Memo.Length);
    }

    // --- rejection -------------------------------------------------------------------------

    [Theory]
    [InlineData("this is a plain text file")]
    [InlineData("<html><body>Sign in to your bank</body></html>")]
    [InlineData("<OFX>\n<SIGNONMSGSRSV1>\n</SIGNONMSGSRSV1>\n</OFX>")]
    [InlineData("")]
    public void Not_a_statement(string text) =>
        Assert.Throws<OfxException>(() => OfxParser.Parse(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void Account_block_without_acctid()
    {
        var broken = Encoding.Latin1.GetString(OfxFixtures.Build()).Replace("<ACCTID>12345678", "<ACCTID>");
        Assert.Throws<OfxException>(() => OfxParser.Parse(Encoding.Latin1.GetBytes(broken)));
    }
}
