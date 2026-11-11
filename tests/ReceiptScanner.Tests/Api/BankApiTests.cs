using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReceiptScanner.Core.Domain;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;
using ReceiptScanner.Tests.Core.Banking;

namespace ReceiptScanner.Tests.Api;

/// <summary>
/// Phase 3 endpoints: OFX upload, transaction search, the reconcile queue
/// and decisions, and auto-linking from both ends. Ports of the scenarios
/// in banking/test_import.py, test_identity.py, test_reconcile_api.py and
/// test_transfers.py. Each test gets its own database: the scenarios all
/// depend on exactly which transactions exist.
/// </summary>
public sealed class BankApiTests : ApiTestBase, IDisposable
{
    public BankApiTests() : base(new ApiFactory()) { }

    public void Dispose() => Factory.Dispose();

    private const string Queue = "/api/bank/reconcile/queue/";
    private const string Summary = "/api/bank/reconcile/summary/";
    private const string Run = "/api/bank/reconcile/run/";
    private const string Transactions = "/api/bank/transactions/";
    private const string Statements = "/api/bank/statements/";
    private static readonly DateOnly Day = new(2026, 9, 15);

    private static string Decision(long receiptId, string action) => $"/api/bank/reconcile/{receiptId}/{action}/";

    private static readonly Txn[] Aug =
    [
        new("A1", "20260801", "20260801", "-10.00", "DEBIT", "SHOP A"),
        new("A2", "20260802", "20260802", "-20.00", "DEBIT", "SHOP B"),
    ];

    // Overlaps Aug on A2, then adds two of its own.
    private static readonly Txn[] Sep =
    [
        new("A2", "20260802", "20260802", "-20.00", "DEBIT", "SHOP B"),
        new("B1", "20260901", "20260901", "-30.00", "DEBIT", "SHOP C"),
        new("B2", "20260902", "20260902", "500.00", "CREDIT", "PAY"),
    ];

    // --- helpers -------------------------------------------------------------------------------

    private static MultipartFormDataContent Ofx(byte[] data, string fileName = "statement.ofx", string field = "file",
        params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        form.Add(content, field, fileName);
        foreach (var (key, value) in fields) form.Add(new StringContent(value), key);
        return form;
    }

    private async Task<JsonElement> Upload(byte[] data, HttpStatusCode expected = HttpStatusCode.Created,
        params (string Key, string Value)[] fields)
    {
        var client = await ClientAsync();
        return await Expect(await client.PostAsync(Statements, Ofx(data, fields: fields)), expected);
    }

    private async Task<T> WithDb<T>(Func<ReceiptScannerDbContext, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ReceiptScannerDbContext>());
    }

    private Task<BankAccount> MakeAccount(bool creditCard = false, string accountId = "12345678",
        string strategy = DedupeStrategy.Fitid) => WithDb(async db =>
    {
        var account = new BankAccount
        {
            BankId = "062807", AccountId = accountId, AccountType = creditCard ? "" : "SAVINGS",
            IsCreditCard = creditCard, DedupeStrategy = strategy,
        };
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    });

    private int _fitid;

    private Task<BankTransaction> MakeTxn(BankAccount account, decimal amount, string memo, DateOnly? day = null) => WithDb(async db =>
    {
        var fitid = $"F{++_fitid}";
        var txn = new BankTransaction
        {
            AccountId = account.Id, Fitid = fitid, DedupeKey = fitid,
            Trntype = amount < 0 ? "DEBIT" : "CREDIT",
            PostedOn = day ?? Day, UserDate = day ?? Day, Amount = amount, Memo = memo,
        };
        db.BankTransactions.Add(txn);
        await db.SaveChangesAsync();
        return txn;
    });

    /// <summary>Stored directly, so nothing is auto-linked on the way in (as make_receipt does).</summary>
    private Task<Receipt> MakeReceipt(string shop, decimal total, DateOnly? day = null) => WithDb(async db =>
    {
        var receipt = new Receipt { Shop = shop, Total = total, PurchasedOn = day ?? Day };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    });

    private Task<BankTransaction> Reload(long id) =>
        WithDb(db => db.BankTransactions.AsNoTracking().FirstAsync(t => t.Id == id));

    private async Task<JsonElement> Get(string url)
    {
        var client = await ClientAsync();
        return await Expect(await client.GetAsync(url), HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> Post(string url, object? body = null)
    {
        var client = await ClientAsync();
        return await client.PostAsync(url, Json(body ?? new { }));
    }

    // --- upload --------------------------------------------------------------------------------

    [Fact]
    public async Task First_import_creates_account_statement_and_rows()
    {
        var body = await Upload(OfxFixtures.Build(Aug));

        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("account_created").GetBoolean());
        Assert.Equal(2, body.GetProperty("created").GetInt32());
        Assert.Equal(0, body.GetProperty("duplicates_skipped").GetInt32());
        Assert.Equal(2, body.GetProperty("transactions_in_file").GetInt32());
        Assert.Equal("2026-09-01", body.GetProperty("period").GetProperty("start").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("statement_id").ValueKind);

        var statement = await WithDb(db => db.OfxStatements.AsNoTracking().SingleAsync());
        Assert.Equal(2u, statement.TransactionsCreated);
        Assert.Equal(1482.66m, statement.LedgerBalance);
        Assert.Equal("statement.ofx", statement.OriginalFilename);
        Assert.StartsWith("ofx/2026/09/", statement.File);
        Assert.True(File.Exists(Path.Combine(Factory.MediaRoot, statement.File!)));

        var txn = await WithDb(db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.Fitid == "A1"));
        Assert.Equal(-10.00m, txn.Amount);
        Assert.Equal(new DateOnly(2026, 8, 1), txn.PostedOn);
        Assert.Null(txn.ReceiptId);
    }

    [Fact]
    public async Task Overlapping_statements_do_not_duplicate_transactions()
    {
        await Upload(OfxFixtures.Build(Aug, dtEnd: "20260831"));
        var body = await Upload(OfxFixtures.Build(Sep));

        Assert.Equal(2, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("duplicates_skipped").GetInt32());
        Assert.Equal(0, body.GetProperty("conflicts").GetArrayLength());
        Assert.Equal(4, await WithDb(db => db.BankTransactions.CountAsync()));
    }

    [Fact]
    public async Task Identical_file_is_a_409_and_reimport_skips_every_row()
    {
        var data = OfxFixtures.Build(Aug);
        var first = await Upload(data);

        var again = await Upload(data, HttpStatusCode.Conflict);
        AssertError(again, "duplicate_statement");
        Assert.Equal(first.GetProperty("statement_id").GetInt64(), again.GetProperty("statement_id").GetInt64());
        Assert.Equal("Send reimport=true to process it anyway.", again.GetProperty("hint").GetString());

        // Nothing new: 200, not 201.
        var forced = await Upload(data, HttpStatusCode.OK, ("reimport", "true"));
        Assert.Equal(0, forced.GetProperty("created").GetInt32());
        Assert.Equal(2, forced.GetProperty("duplicates_skipped").GetInt32());
        Assert.Equal(2, await WithDb(db => db.BankTransactions.CountAsync()));
    }

    [Fact]
    public async Task Same_fitid_on_a_different_account_is_not_a_duplicate()
    {
        await Upload(OfxFixtures.Build(Aug, acctId: "11111111"));
        var body = await Upload(OfxFixtures.Build(Aug, acctId: "22222222"));

        Assert.Equal(2, body.GetProperty("created").GetInt32());
        Assert.Equal(2, await WithDb(db => db.BankAccounts.CountAsync()));
    }

    [Fact]
    public async Task Changed_amount_is_a_conflict_and_the_stored_row_wins()
    {
        await Upload(OfxFixtures.Build(Aug));
        var body = await Upload(OfxFixtures.Build([new("A1", "20260801", "20260801", "-99.00", "DEBIT", "SHOP A"), Aug[1]]),
            HttpStatusCode.OK);

        var conflict = Assert.Single(body.GetProperty("conflicts").EnumerateArray());
        Assert.Equal("A1", conflict.GetProperty("fitid").GetString());
        Assert.Equal(new[] { "amount" }, conflict.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("-10.00", conflict.GetProperty("stored").GetProperty("amount").GetString());
        Assert.Equal("-99.00", conflict.GetProperty("incoming").GetProperty("amount").GetString());

        Assert.Equal(-10.00m, (await WithDb(db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.Fitid == "A1"))).Amount);
        Assert.Equal(1, await WithDb(db => db.TransactionConflicts.CountAsync()));
        var latest = await WithDb(db => db.OfxStatements.AsNoTracking().OrderByDescending(s => s.Id).FirstAsync());
        Assert.Equal(1u, latest.TransactionsConflicted);
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        var body = await Upload(OfxFixtures.Build(Aug), HttpStatusCode.Created, ("dry_run", "true"));

        Assert.True(body.GetProperty("dry_run").GetBoolean());
        Assert.Equal(2, body.GetProperty("created").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("statement_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("auto_matched").ValueKind);
        Assert.Equal(0, await WithDb(db => db.BankTransactions.CountAsync()));
        Assert.Equal(0, await WithDb(db => db.OfxStatements.CountAsync()));
        Assert.Equal(0, await WithDb(db => db.BankAccounts.CountAsync()));
        if (Directory.Exists(Factory.MediaRoot))
            Assert.Empty(Directory.EnumerateFiles(Factory.MediaRoot, "*.ofx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Nickname_applies_to_a_new_account_only()
    {
        await Upload(OfxFixtures.Build(Aug), HttpStatusCode.Created, ("nickname", "Savings"));
        await Upload(OfxFixtures.Build(Sep), HttpStatusCode.Created, ("nickname", "Ignored"));
        Assert.Equal("Savings", (await WithDb(db => db.BankAccounts.AsNoTracking().SingleAsync())).Nickname);
    }

    [Fact]
    public async Task Upload_errors()
    {
        var client = await ClientAsync();

        var noFile = await Expect(await client.PostAsync(Statements, Ofx(OfxFixtures.Build(), field: "document")), HttpStatusCode.BadRequest);
        AssertError(noFile, "no_file");

        var json = await Expect(await client.PostAsync(Statements, Json(new { })), HttpStatusCode.BadRequest);
        AssertError(json, "no_file");

        var junk = await Expect(await client.PostAsync(Statements, Ofx("not an ofx file"u8.ToArray())), HttpStatusCode.UnprocessableEntity);
        AssertError(junk, "invalid_ofx");
        Assert.StartsWith("Could not parse the file: ", junk.GetProperty("message").GetString());

        // The other accepted field names.
        await Expect(await client.PostAsync(Statements, Ofx(OfxFixtures.Build(Aug), field: "ofx")), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Upload_needs_a_token()
    {
        var withKey = new HttpRequestMessage(HttpMethod.Post, Statements) { Content = Ofx(OfxFixtures.Build()) };
        withKey.Headers.Add("X-API-Key", ApiFactory.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous.SendAsync(withKey)).StatusCode);
    }

    // --- FITID identity --------------------------------------------------------------------------

    private static readonly (string Bank, string Acct) Anz = ("012720", "170454654");
    private const string Rent = "ANZ INTERNET BANKING BPAY DEFT REAL ESTATE";

    private static Txn Row(string day, int ordinal, string amount, string memo, string type = "DEBIT") =>
        new($"{day}000{ordinal}", day, day, amount, type, memo);

    // A long export: rent on the 11th is the 7th newest row.
    private static readonly Txn[] Long =
    [
        Row("20260918", 1, "-670.00", Rent),
        Row("20260917", 2, "-195.34", "PAYMENT TO TOYOTA INSURANCE"),
        Row("20260916", 3, "3296.90", "PAY/SALARY FROM NSW HEALTH", "CREDIT"),
        Row("20260915", 4, "-33.00", "PAYMENT TO NSWNMA"),
        Row("20260914", 5, "-80.12", "WOOLWORTHS 1234"),
        Row("20260913", 6, "-15.00", "COFFEE"),
        Row("20260911", 7, "-670.00", Rent),
    ];

    // A later, shorter export over part of the same period: the row numbers moved.
    private static readonly Txn[] ShortExport =
    [
        Row("20260925", 1, "-670.00", Rent),
        Row("20260918", 2, "-670.00", Rent),
        Row("20260911", 3, "-670.00", Rent),
        Row("20260913", 4, "-15.00", "COFFEE"),
        Row("20260914", 5, "-80.12", "WOOLWORTHS 1234"),
    ];

    [Fact]
    public async Task Ordinal_fitids_are_flagged_but_still_imported()
    {
        var body = await Upload(OfxFixtures.Build(Long, bankId: Anz.Bank, acctId: Anz.Acct));
        Assert.True(body.GetProperty("unstable_fitid").GetBoolean());
        Assert.Equal(Long.Length, body.GetProperty("created").GetInt32());

        var cba = await Upload(OfxFixtures.Build());
        Assert.False(cba.GetProperty("unstable_fitid").GetBoolean());
    }

    [Fact]
    public async Task Content_keying_recognises_the_overlap()
    {
        await WithDb(async db =>
        {
            db.BankAccounts.Add(new BankAccount
            {
                BankId = Anz.Bank, AccountId = Anz.Acct, AccountType = "SAVINGS", DedupeStrategy = DedupeStrategy.Content,
            });
            return await db.SaveChangesAsync();
        });

        var first = await Upload(OfxFixtures.Build(Long, bankId: Anz.Bank, acctId: Anz.Acct));
        Assert.False(first.GetProperty("unstable_fitid").GetBoolean());   // silent once the account uses content keys

        var second = await Upload(OfxFixtures.Build(ShortExport, bankId: Anz.Bank, acctId: Anz.Acct));
        // ShortExport's five rows: four already stored, one new.
        Assert.Equal(1, second.GetProperty("created").GetInt32());
        Assert.Equal(4, second.GetProperty("duplicates_skipped").GetInt32());
        Assert.Equal(3, await WithDb(db => db.BankTransactions.CountAsync(t => t.Memo == Rent)));

        // The FITID from the file that introduced it is kept as a record.
        var rent = await WithDb(db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.Memo == Rent && t.PostedOn == new DateOnly(2026, 9, 11)));
        Assert.Equal("202609110007", rent.Fitid);
        Assert.EndsWith("#1", rent.DedupeKey);
    }

    [Fact]
    public async Task Two_identical_payments_on_one_day_are_two_transactions()
    {
        await MakeAccount(accountId: Anz.Acct, strategy: DedupeStrategy.Content);
        await WithDb(db => db.BankAccounts.Where(a => a.AccountId == Anz.Acct).ExecuteUpdateAsync(u => u.SetProperty(a => a.BankId, Anz.Bank)));

        var twice = new[] { Row("20260501", 1, "-670.00", Rent), Row("20260501", 2, "-670.00", Rent) };
        Assert.Equal(2, (await Upload(OfxFixtures.Build(twice, bankId: Anz.Bank, acctId: Anz.Acct))).GetProperty("created").GetInt32());

        var again = await Upload(OfxFixtures.Build(twice, bankId: Anz.Bank, acctId: Anz.Acct), HttpStatusCode.OK, ("reimport", "1"));
        Assert.Equal(2, again.GetProperty("duplicates_skipped").GetInt32());
    }

    // --- transaction search ----------------------------------------------------------------------

    [Fact]
    public async Task Transaction_search()
    {
        var account = await MakeAccount();
        var coles = await MakeTxn(account, -26.40m, "COLES 5690 THORNTON NS AUS Card xx9426");
        await MakeTxn(account, -63.00m, "Direct Debit 438876 WOOLWORTHS INSUR 63001628");
        await MakeTxn(account, -100.00m, "Transfer To RR ELEVEN CommBank App", Day.AddDays(-10));
        var linked = await MakeTxn(account, -5.00m, "COFFEE");
        var receipt = await MakeReceipt("Cafe", 5m);
        await WithDb(db => db.BankTransactions.Where(t => t.Id == linked.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.ReceiptId, receipt.Id)));

        // Unmatched only by default, newest first.
        var all = await Get(Transactions);
        Assert.Equal(3, all.GetProperty("count").GetInt32());
        var first = all.GetProperty("results")[0];
        Assert.Equal("26.40", first.GetProperty("amount_abs").GetString());
        Assert.Equal("-26.40", first.GetProperty("amount").GetString());
        Assert.Equal("COLES 5690 THORNTON", first.GetProperty("merchant").GetString());
        Assert.DoesNotContain("12345678", first.GetProperty("account").GetProperty("account").GetString());

        Assert.Equal(1, (await Get($"{Transactions}?matched=true")).GetProperty("count").GetInt32());
        Assert.Equal(4, (await Get($"{Transactions}?matched=all")).GetProperty("count").GetInt32());

        // The sign is the bank's; a search for a receipt total ignores it.
        var byAmount = await Get($"{Transactions}?amount=26.40");
        Assert.Equal(coles.Id, Assert.Single(byAmount.GetProperty("results").EnumerateArray()).GetProperty("id").GetInt64());

        Assert.Equal(1, (await Get($"{Transactions}?q=coles")).GetProperty("count").GetInt32());
        Assert.Equal(1, (await Get($"{Transactions}?since=2026-09-01&until=2026-09-10")).GetProperty("count").GetInt32());
        Assert.Equal(1, (await Get($"{Transactions}?purchases_only=true")).GetProperty("count").GetInt32());

        var client = await ClientAsync();
        AssertError(await Expect(await client.GetAsync($"{Transactions}?amount=abc"), HttpStatusCode.UnprocessableEntity), "validation_error");
        AssertError(await Expect(await client.GetAsync($"{Transactions}?matched=maybe"), HttpStatusCode.UnprocessableEntity), "validation_error");
        AssertError(await Expect(await client.GetAsync($"{Transactions}?since=15/09/2026"), HttpStatusCode.BadRequest), "invalid_parameter");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous.GetAsync(Transactions)).StatusCode);
    }

    // --- the queue -------------------------------------------------------------------------------

    [Fact]
    public async Task A_confident_match_never_reaches_the_queue_but_a_tie_does()
    {
        var account = await MakeAccount();
        await MakeReceipt("Woolworths", 12.00m);
        await MakeTxn(account, -12.00m, "WOOLWORTHS 1179 EAST MAITLAND");   // confident, but not yet linked

        var receipt = await MakeReceipt("Coles", 45.60m);
        await MakeTxn(account, -45.60m, "COLES 5690 THORNTON");
        await MakeTxn(account, -45.60m, "COLES 0898 EAST MAITLAND");

        var body = await Get(Queue);
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        var item = body.GetProperty("results")[0];
        Assert.Equal(receipt.Id, item.GetProperty("receipt").GetProperty("id").GetInt64());
        Assert.Equal("ambiguous", item.GetProperty("bucket").GetString());
        Assert.Equal(2, item.GetProperty("candidates").GetArrayLength());
        Assert.True(item.GetProperty("needs_choice").GetBoolean());
        var candidate = item.GetProperty("candidates")[0];
        Assert.Equal("exact", candidate.GetProperty("confidence").GetString());
        Assert.Equal("same day, shop matches memo", candidate.GetProperty("reason").GetString());

        var auto = await Get($"{Queue}?bucket=auto");
        Assert.Equal("Woolworths", auto.GetProperty("results")[0].GetProperty("receipt").GetProperty("shop").GetString());
    }

    [Fact]
    public async Task Weak_and_no_candidate_buckets()
    {
        var account = await MakeAccount();
        await MakeReceipt("Bunnings Warehouse", 88.00m);
        await MakeTxn(account, -88.00m, "MYSTERY SHOP");
        await MakeReceipt("Farmers Market", 7.00m);

        var weak = (await Get(Queue)).GetProperty("results");
        Assert.Equal("weak", Assert.Single(weak.EnumerateArray()).GetProperty("bucket").GetString());

        var none = (await Get($"{Queue}?bucket=none")).GetProperty("results");
        Assert.Equal("Farmers Market", Assert.Single(none.EnumerateArray()).GetProperty("receipt").GetProperty("shop").GetString());

        var client = await ClientAsync();
        var bad = await Expect(await client.GetAsync($"{Queue}?bucket=weak,bogus"), HttpStatusCode.BadRequest);
        AssertError(bad, "invalid_bucket");
        Assert.Equal("Unknown bucket(s): bogus. Valid: ambiguous, auto, none, weak.", bad.GetProperty("message").GetString());

        var summary = await Get(Summary);
        Assert.Equal(1, summary.GetProperty("weak").GetInt32());
        Assert.Equal(1, summary.GetProperty("none").GetInt32());
        Assert.Equal(0, summary.GetProperty("ambiguous").GetInt32());
    }

    [Fact]
    public async Task The_window_can_be_widened_but_not_arbitrarily()
    {
        var account = await MakeAccount();
        await MakeReceipt("Coles", 26.40m, Day.AddDays(-10));
        await MakeTxn(account, -26.40m, "MYSTERY");

        Assert.Equal(0, (await Get(Queue)).GetProperty("count").GetInt32());
        Assert.Equal(1, (await Get($"{Queue}?window=10")).GetProperty("count").GetInt32());
        Assert.Equal(0, (await Get($"{Queue}?window=9")).GetProperty("count").GetInt32());
        Assert.Equal(1, (await Get($"{Queue}?window=999")).GetProperty("count").GetInt32());   // clamped to 14
        Assert.Equal(0, (await Get($"{Queue}?window=abc")).GetProperty("count").GetInt32());   // junk → 3
    }

    // --- decisions -------------------------------------------------------------------------------

    [Fact]
    public async Task Confirming_links_the_chosen_one_and_guards_against_stale_choices()
    {
        var account = await MakeAccount();
        var receipt = await MakeReceipt("Coles", 45.60m);
        var a = await MakeTxn(account, -45.60m, "COLES 5690 THORNTON");
        var b = await MakeTxn(account, -45.60m, "COLES 0898 EAST MAITLAND");

        var body = await Expect(await Post(Decision(receipt.Id, "confirm"), new { transaction_id = b.Id }), HttpStatusCode.OK);
        Assert.Equal(receipt.Id, body.GetProperty("receipt_id").GetInt64());
        Assert.Equal(b.Id, body.GetProperty("transaction").GetProperty("id").GetInt64());
        Assert.Equal(receipt.Id, body.GetProperty("transaction").GetProperty("receipt_id").GetInt64());

        var stored = await Reload(b.Id);
        Assert.Equal(receipt.Id, stored.ReceiptId);
        Assert.Equal("manual", stored.MatchMethod);
        Assert.Equal("exact", stored.MatchConfidence);   // the matcher's reasoning, since it suggested this one
        Assert.Equal(0, (await Get(Queue)).GetProperty("count").GetInt32());

        // Idempotent.
        await Expect(await Post(Decision(receipt.Id, "confirm"), new { transaction_id = b.Id }), HttpStatusCode.OK);

        // A second transaction for the same receipt.
        var second = await Expect(await Post(Decision(receipt.Id, "confirm"), new { transaction_id = a.Id }), HttpStatusCode.Conflict);
        AssertError(second, "match_unavailable");

        // A transaction someone else took.
        var other = await MakeReceipt("Coles", 45.60m);
        AssertError(await Expect(await Post(Decision(other.Id, "confirm"), new { transaction_id = b.Id }), HttpStatusCode.Conflict),
            "match_unavailable");
    }

    [Fact]
    public async Task A_transaction_the_matcher_never_offered_can_still_be_chosen()
    {
        var account = await MakeAccount();
        var receipt = await MakeReceipt("Coles", 45.60m);
        var odd = await MakeTxn(account, -44.00m, "SOMETHING ELSE", Day.AddDays(-20));

        await Expect(await Post(Decision(receipt.Id, "confirm"), new { transaction_id = odd.Id.ToString() }), HttpStatusCode.OK);
        var stored = await Reload(odd.Id);
        Assert.Equal("manual", stored.MatchConfidence);
        Assert.Equal("chosen by hand, not a suggested match", stored.MatchNote);
    }

    [Fact]
    public async Task Bad_decisions()
    {
        var receipt = await MakeReceipt("Coles", 45.60m);

        var missing = await Expect(await Post(Decision(receipt.Id, "confirm")), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("transaction_id is required.", missing.GetProperty("message").GetString());

        var word = await Expect(await Post(Decision(receipt.Id, "confirm"), new { transaction_id = "abc" }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("transaction_id must be an integer, got 'abc'.", word.GetProperty("message").GetString());

        await Expect(await Post(Decision(receipt.Id, "confirm"), new { transaction_id = 999999 }), HttpStatusCode.NotFound);
        await Expect(await Post(Decision(999999, "no-match")), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(Decision(receipt.Id, "explode"))).StatusCode);
    }

    [Fact]
    public async Task Rejecting_down_to_one_links_the_survivor()
    {
        var account = await MakeAccount();
        var receipt = await MakeReceipt("Coles", 45.60m);
        var a = await MakeTxn(account, -45.60m, "COLES 5690 THORNTON");
        var b = await MakeTxn(account, -45.60m, "COLES 0898 MAITLAND");
        var c = await MakeTxn(account, -45.60m, "COLES 1122 KOTARA");

        var first = await Expect(await Post(Decision(receipt.Id, "reject"), new { transaction_id = a.Id }), HttpStatusCode.OK);
        Assert.Equal("This pair won't be suggested again.", first.GetProperty("detail").GetString());
        var ids = (await Get(Queue)).GetProperty("results")[0].GetProperty("candidates").EnumerateArray()
            .Select(x => x.GetProperty("transaction").GetProperty("id").GetInt64()).Order().ToArray();
        Assert.Equal(new[] { b.Id, c.Id }, ids);

        // Idempotent.
        await Expect(await Post(Decision(receipt.Id, "reject"), new { transaction_id = a.Id }), HttpStatusCode.OK);
        Assert.Equal(1, await WithDb(db => db.MatchRejections.CountAsync()));

        await Expect(await Post(Decision(receipt.Id, "reject"), new { transaction_id = b.Id }), HttpStatusCode.OK);
        var survivor = await Reload(c.Id);
        Assert.Equal(receipt.Id, survivor.ReceiptId);
        Assert.Equal("auto", survivor.MatchMethod);
        Assert.Equal(0, (await Get(Queue)).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_rejection_can_be_undone_and_rejecting_a_linked_pair_unlinks_it()
    {
        var account = await MakeAccount();
        var receipt = await MakeReceipt("Coles", 45.60m);
        var txn = await MakeTxn(account, -45.60m, "COLES 5690 THORNTON");
        await Expect(await Post(Decision(receipt.Id, "confirm"), new { transaction_id = txn.Id }), HttpStatusCode.OK);

        await Expect(await Post(Decision(receipt.Id, "reject"), new { transaction_id = txn.Id }), HttpStatusCode.OK);
        Assert.Null((await Reload(txn.Id)).ReceiptId);

        var undo = await Expect(await Post(Decision(receipt.Id, "unreject"), new { transaction_id = txn.Id }), HttpStatusCode.OK);
        Assert.True(undo.GetProperty("undone").GetBoolean());
        // The last rejection gone, the only candidate is confident again.
        Assert.Equal(receipt.Id, (await Reload(txn.Id)).ReceiptId);

        var nothing = await Expect(await Post(Decision(receipt.Id, "unreject"), new { transaction_id = txn.Id }), HttpStatusCode.OK);
        Assert.False(nothing.GetProperty("undone").GetBoolean());
        Assert.Equal("There was no rejection to undo.", nothing.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Cash_marks_and_unlinking()
    {
        var account = await MakeAccount();
        var cash = await MakeReceipt("Farmers Market", 7.00m);

        var marked = await Expect(await Post(Decision(cash.Id, "no-match"), new { note = "paid cash" }), HttpStatusCode.OK);
        Assert.True(marked.GetProperty("no_bank_match").GetBoolean());
        Assert.Equal(0, (await Get($"{Queue}?bucket=none")).GetProperty("count").GetInt32());
        Assert.Equal(1, (await Get(Summary)).GetProperty("marked_no_match").GetInt32());

        await Expect(await Post(Decision(cash.Id, "clear-no-match")), HttpStatusCode.OK);
        Assert.Equal(1, (await Get($"{Queue}?bucket=none")).GetProperty("count").GetInt32());

        var receipt = await MakeReceipt("Coles", 45.60m);
        var txn = await MakeTxn(account, -45.60m, "COLES 5690 THORNTON");
        await Expect(await Post(Decision(receipt.Id, "confirm"), new { transaction_id = txn.Id }), HttpStatusCode.OK);

        // A linked receipt evidently wasn't cash.
        await Expect(await Post(Decision(receipt.Id, "no-match")), HttpStatusCode.Conflict);

        var client = await ClientAsync();
        var removed = await Expect(await client.DeleteAsync(Decision(receipt.Id, "match")), HttpStatusCode.OK);
        Assert.Equal(txn.Id, removed.GetProperty("transaction_id").GetInt64());
        Assert.Null((await Reload(txn.Id)).ReceiptId);
        Assert.Equal(0, await WithDb(db => db.MatchRejections.CountAsync()));   // undoing isn't rejecting

        AssertError(await Expect(await client.DeleteAsync(Decision(receipt.Id, "match")), HttpStatusCode.Conflict), "not_linked");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync(Decision(receipt.Id, "confirm"))).StatusCode);
    }

    [Fact]
    public async Task Running_links_the_confident_ones()
    {
        var account = await MakeAccount();
        var receipt = await MakeReceipt("Coles", 26.40m);
        var txn = await MakeTxn(account, -26.40m, "COLES 5690 THORNTON");
        await MakeReceipt("Bunnings", 88.00m);
        await MakeTxn(account, -88.00m, "MYSTERY SHOP");

        var dry = await Expect(await Post(Run, new { dry_run = true }), HttpStatusCode.OK);
        Assert.True(dry.GetProperty("dry_run").GetBoolean());
        Assert.Equal(1, dry.GetProperty("linked").GetInt32());
        Assert.Null((await Reload(txn.Id)).ReceiptId);

        var real = await Expect(await Post(Run), HttpStatusCode.OK);
        Assert.Equal(1, real.GetProperty("linked").GetInt32());
        Assert.Equal(1, real.GetProperty("weak").GetInt32());
        Assert.Equal(1, real.GetProperty("needs_review").GetInt32());
        Assert.Equal(receipt.Id, (await Reload(txn.Id)).ReceiptId);
    }

    // --- auto-linking from both ends -------------------------------------------------------------

    [Fact]
    public async Task Uploading_a_receipt_matches_a_transaction_already_there()
    {
        var account = await MakeAccount();
        var txn = await MakeTxn(account, -45.60m, "COLES 5690 THORNTON");
        var client = await ClientAsync();

        var body = await Expect(await client.PostAsync("/api/receipts/",
            Json(new { shop = "Coles", total = "45.60", date = "2026-09-15", items = Array.Empty<object>() })), HttpStatusCode.Created);

        var match = body.GetProperty("bank_match");
        Assert.Equal(txn.Id, match.GetProperty("transaction_id").GetInt64());
        Assert.Equal("exact", match.GetProperty("confidence").GetString());
        Assert.Equal("auto", (await Reload(txn.Id)).MatchMethod);
    }

    [Fact]
    public async Task An_uncertain_match_is_left_for_the_queue()
    {
        var account = await MakeAccount();
        await MakeTxn(account, -45.60m, "MYSTERY SHOP");
        var client = await ClientAsync();

        var body = await Expect(await client.PostAsync("/api/receipts/",
            Json(new { shop = "Coles", total = "45.60", date = "2026-09-15", items = Array.Empty<object>() })), HttpStatusCode.Created);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("bank_match").ValueKind);
        Assert.Equal(1, (await Get(Queue)).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Importing_a_statement_matches_the_receipts_waiting_for_it()
    {
        await MakeReceipt("Shop A", 10.00m, new DateOnly(2026, 9, 1));

        var body = await Upload(OfxFixtures.Build([new("T1", "20260901", "20260901", "-10.00", "DEBIT", "SHOP A SYDNEY")]));

        Assert.Equal(1, body.GetProperty("auto_matched").GetProperty("linked").GetInt32());
        Assert.True(await WithDb(db => db.BankTransactions.AnyAsync(t => t.ReceiptId != null)));
    }

    // --- card repayments -------------------------------------------------------------------------

    [Fact]
    public async Task A_card_statement_pairs_with_the_repayment_from_the_everyday_account()
    {
        await Upload(OfxFixtures.Build([new("S1", "20260910", "20260910", "-500.00", "DEBIT", "Transfer to xx1234 CommBank app")]));

        var card = await Upload(OfxFixtures.Build(
            [
                new("C1", "20260911", "20260911", "500.00", "CREDIT", "PAYMENT RECEIVED, THANK YOU"),
                new("C2", "20260912", "20260912", "-20.00", "DEBIT", "SOMEWHERE"),
            ],
            creditCard: true, acctId: "4000123412341234"));

        Assert.Equal(1, card.GetProperty("transfers_paired").GetProperty("paired").GetInt32());
        Assert.True((await WithDb(db => db.BankAccounts.AsNoTracking().SingleAsync(a => a.AccountId == "4000123412341234"))).IsCreditCard);

        var debit = await WithDb(db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.Fitid == "S1"));
        var credit = await WithDb(db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.Fitid == "C1"));
        Assert.Equal(credit.Id, debit.TransferPairId);
        Assert.Equal(debit.Id, credit.TransferPairId);
        Assert.Null((await WithDb(db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.Fitid == "C2"))).TransferPairId);
    }

    [Fact]
    public async Task Two_possible_repayments_are_left_alone()
    {
        await Upload(OfxFixtures.Build(
        [
            new("S1", "20260909", "20260909", "-500.00", "DEBIT", "Transfer A"),
            new("S2", "20260910", "20260910", "-500.00", "DEBIT", "Transfer B"),
        ]));
        var card = await Upload(OfxFixtures.Build([new("C1", "20260911", "20260911", "500.00", "CREDIT", "PAYMENT")],
            creditCard: true, acctId: "4000123412341234"));

        var pairs = card.GetProperty("transfers_paired");
        Assert.Equal(0, pairs.GetProperty("paired").GetInt32());
        Assert.Equal(1, pairs.GetProperty("ambiguous").GetInt32());
    }
}
