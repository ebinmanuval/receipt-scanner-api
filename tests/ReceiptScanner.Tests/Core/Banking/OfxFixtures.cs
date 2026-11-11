using System.Text;

namespace ReceiptScanner.Tests.Core.Banking;

/// <summary>(fitid, dtposted, dtuser, amount, trntype, memo) — as in banking/fixtures.py.</summary>
public sealed record Txn(string Fitid, string Posted, string User, string Amount, string Trntype, string Memo);

/// <summary>
/// OFX builders, ported from banking/fixtures.py. Entirely fabricated: no
/// real account numbers or transactions. OFX 1.0.2 SGML in cp1252, leaf
/// elements with no closing tag, the way the bank emits it.
/// </summary>
public static class OfxFixtures
{
    static OfxFixtures() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static readonly Txn[] Default =
    [
        new("T0001", "20260901", "20260901", "-12.34", "DEBIT", "COLES 1234 SYDNEY"),
        new("T0002", "20260902", "20260902", "-5.00", "DEBIT", "COFFEE SHOP"),
        new("T0003", "20260903", "20260903", "1500.00", "CREDIT", "SALARY"),
    ];

    private const string Header = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE


        """;

    public static byte[] Build(
        IEnumerable<Txn>? txns = null,
        string bankId = "062807",
        string acctId = "12345678",
        string acctType = "SAVINGS",
        string curDef = "AUD",
        string dtStart = "20260901",
        string dtEnd = "20260930",
        string dtServer = "20260930120000",
        string balAmt = "1482.66",
        bool includeAvailBal = true,
        bool creditCard = false)
    {
        var rows = (txns ?? Default).Select(t =>
        {
            var block = new List<string> { "<STMTTRN>", $"<TRNTYPE>{t.Trntype}", $"<DTPOSTED>{t.Posted}" };
            if (t.User.Length > 0) block.Add($"<DTUSER>{t.User}");
            block.AddRange([$"<TRNAMT>{t.Amount}", $"<FITID>{t.Fitid}", $"<MEMO>{t.Memo}", "</STMTTRN>"]);
            return string.Join("\n", block);
        });

        var availBal = includeAvailBal ? $"<AVAILBAL>\n<BALAMT>{balAmt}\n<DTASOF>{dtServer}\n</AVAILBAL>\n" : "";

        var (msgs, rs, stmt, account) = creditCard
            ? ("CREDITCARDMSGSRSV1", "CCSTMTTRNRS", "CCSTMTRS", $"<CCACCTFROM>\n<ACCTID>{acctId}\n</CCACCTFROM>")
            : ("BANKMSGSRSV1", "STMTTRNRS", "STMTRS",
               $"<BANKACCTFROM>\n<BANKID>{bankId}\n<ACCTID>{acctId}\n<ACCTTYPE>{acctType}\n</BANKACCTFROM>");

        var body = $"""
            <OFX>
            <SIGNONMSGSRSV1>
            <SONRS>
            <STATUS>
            <CODE>0
            <SEVERITY>INFO
            </STATUS>
            <DTSERVER>{dtServer}
            <LANGUAGE>ENG
            </SONRS>
            </SIGNONMSGSRSV1>
            <{msgs}>
            <{rs}>
            <TRNUID>1
            <STATUS>
            <CODE>0
            <SEVERITY>INFO
            </STATUS>
            <{stmt}>
            <CURDEF>{curDef}
            {account}
            <BANKTRANLIST>
            <DTSTART>{dtStart}
            <DTEND>{dtEnd}
            {string.Join("\n", rows)}
            </BANKTRANLIST>
            <LEDGERBAL>
            <BALAMT>{balAmt}
            <DTASOF>{dtServer}
            </LEDGERBAL>
            {availBal}</{stmt}>
            </{rs}>
            </{msgs}>
            </OFX>

            """;
        return Encoding.GetEncoding(1252).GetBytes((Header + body).Replace("\r\n", "\n"));
    }

    /// <summary>OFX 2.x: real XML with closing tags, to prove the parser handles both.</summary>
    public static byte[] BuildV2(IEnumerable<Txn>? txns = null, string acctId = "12345678")
    {
        var rows = string.Join("\n", (txns ?? Default).Select(t =>
            $"<STMTTRN><TRNTYPE>{t.Trntype}</TRNTYPE><DTPOSTED>{t.Posted}</DTPOSTED>" +
            $"<TRNAMT>{t.Amount}</TRNAMT><FITID>{t.Fitid}</FITID><MEMO>{t.Memo}</MEMO></STMTTRN>"));
        return Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <?OFX OFXHEADER="200" VERSION="211" SECURITY="NONE" OLDFILEUID="NONE" NEWFILEUID="NONE"?>
            <OFX>
              <BANKMSGSRSV1><STMTTRNRS><STMTRS>
                <CURDEF>AUD</CURDEF>
                <BANKACCTFROM>
                  <BANKID>062807</BANKID><ACCTID>{acctId}</ACCTID><ACCTTYPE>SAVINGS</ACCTTYPE>
                </BANKACCTFROM>
                <BANKTRANLIST>
                  <DTSTART>20260901</DTSTART><DTEND>20260930</DTEND>
                  {rows}
                </BANKTRANLIST>
                <LEDGERBAL><BALAMT>1482.66</BALAMT><DTASOF>20260930120000</DTASOF></LEDGERBAL>
              </STMTRS></STMTTRNRS></BANKMSGSRSV1>
            </OFX>

            """);
    }
}
