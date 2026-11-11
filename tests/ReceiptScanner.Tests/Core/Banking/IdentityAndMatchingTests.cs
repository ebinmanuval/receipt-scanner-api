using ReceiptScanner.Core.Banking;

namespace ReceiptScanner.Tests.Core.Banking;

/// <summary>The pure parts of banking/test_identity.py and test_matching.py.</summary>
public class IdentityTests
{
    private static readonly DateOnly Day = new(2026, 9, 11);

    [Fact]
    public void Padding_and_case_do_not_change_the_signature() =>
        // Banks pad memos to fixed widths, and not always the same width.
        Assert.Equal(Identity.Signature(Day, -670.00m, "DEFT REAL ESTATE    "),
                     Identity.Signature(Day, -670.00m, "  deft  real   estate"));

    [Fact]
    public void Trailing_zeros_on_the_amount_do_not_matter() =>
        Assert.Equal(Identity.Signature(Day, -670.0m, "X"), Identity.Signature(Day, -670.000m, "X"));

    [Fact]
    public void A_different_date_amount_or_memo_is_a_different_signature()
    {
        var baseline = Identity.Signature(Day, -670.00m, "X");
        Assert.NotEqual(baseline, Identity.Signature(Day.AddDays(1), -670.00m, "X"));
        Assert.NotEqual(baseline, Identity.Signature(Day, -671.00m, "X"));
        Assert.NotEqual(baseline, Identity.Signature(Day, -670.00m, "Y"));
    }

    [Fact]
    public void Signature_is_a_fixed_length_lowercase_hex_digest()
    {
        var sig = Identity.Signature(Day, -1m, "X");
        Assert.Equal(Identity.DigestLength, sig.Length);
        Assert.Matches("^[0-9a-f]+$", sig);
    }

    [Fact]
    public void Content_key_numbers_repeats()
    {
        var sig = Identity.Signature(Day, -670m, "RENT");
        Assert.Equal($"{sig}#1", Identity.ContentKey(Day, -670m, "RENT"));
        Assert.Equal($"{sig}#2", Identity.ContentKey(Day, -670m, "RENT", 2));
    }

    private static ParsedTransaction Row(string day, int ordinal) =>
        new($"{day}000{ordinal}", DateOnly.ParseExact(day, "yyyyMMdd"), -1m, "DEBIT", "X");

    [Fact]
    public void Anz_ordinal_fitids_are_recognised() =>
        Assert.True(Identity.LooksLikeOrdinalFitid(
            Enumerable.Range(1, 7).Select(i => Row($"202609{20 - i:00}", i))));

    [Fact]
    public void A_short_file_is_not_judged() =>
        // Three coincidental matches shouldn't condemn an account.
        Assert.False(Identity.LooksLikeOrdinalFitid(
            Enumerable.Range(1, 3).Select(i => Row($"202609{20 - i:00}", i))));

    [Fact]
    public void Ordinary_fitids_are_not_flagged() =>
        Assert.False(Identity.LooksLikeOrdinalFitid(
            Enumerable.Range(1, 7).Select(i => new ParsedTransaction($"T000{i}", new DateOnly(2026, 9, i), -1m))));
}

public class MatchingTests
{
    private const string Card = "COLES 5690 THORNTON NS AUS Card xx9426 Value Date: 15/09/2026";
    private const string Short = "BLOOMS THE CHEMIST EAST MAITLAND AU";
    private const string Debit = "Direct Debit 438876 WOOLWORTHS INSUR 63001628";
    private const string Xfer = "Transfer To RR ELEVEN CommBank App ATO lodgments";

    [Fact]
    public void Strips_card_and_value_date() => Assert.Equal("COLES 5690 THORNTON", Matching.MemoMerchant(Card));

    [Fact]
    public void Strips_trailing_country_and_state() =>
        Assert.Equal("BLOOMS THE CHEMIST EAST MAITLAND", Matching.MemoMerchant(Short));

    [Fact]
    public void Leaves_a_plain_memo_alone() =>
        Assert.Equal("Bakers Delight Thornton", Matching.MemoMerchant("Bakers Delight Thornton"));

    [Fact]
    public void Normalize_splits_on_punctuation() =>
        Assert.Equal(new[] { "7", "ELEVEN", "METFORD" }, Matching.Normalize("7-Eleven Metford"));

    [Fact]
    public void Direct_debits_and_transfers_are_not_purchases()
    {
        Assert.False(Matching.IsPurchase(Debit));
        Assert.False(Matching.IsPurchase(Xfer));
        Assert.False(Matching.IsPurchase("Salary CDC NSW PTY LTD 21590"));
    }

    [Fact]
    public void Card_purchases_are()
    {
        Assert.True(Matching.IsPurchase(Card));
        Assert.True(Matching.IsPurchase(Short));
    }

    [Theory]
    [InlineData("Coles", Card, "prefix")]
    // The reason matching is anchored: a grocery receipt must not be linked
    // to an insurance direct debit that happens to say WOOLWORTHS.
    [InlineData("Woolworths", Debit, "contains")]
    // Receipt says "Bunnings Warehouse", bank says just "BUNNINGS".
    [InlineData("Bunnings Warehouse", "BUNNINGS 123456 THORNTON AU", "prefix")]
    [InlineData("BIG W", "BIG W 0234 EAST MAITLAND AU", "prefix")]
    [InlineData("BIG W", "BIG BANANA COFFS HARBOUR AU", "contains")]
    [InlineData("Coles", "WOOLWORTHS 1179 EAST MAITLAND", "none")]
    [InlineData("7-Eleven", "7 ELEVEN 2308 METFORD NS AUS", "prefix")]
    public void Merchant_match(string shop, string memo, string expected) =>
        Assert.Equal(expected, Matching.MerchantMatch(shop, memo));

    [Fact]
    public void Transaction_date_prefers_the_user_date()
    {
        Assert.Equal(new DateOnly(2026, 9, 13), Matching.TransactionDate(new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 13)));
        Assert.Equal(new DateOnly(2026, 9, 15), Matching.TransactionDate(new DateOnly(2026, 9, 15), null));
    }

    [Fact]
    public void Confidence_from_days_and_shop()
    {
        Assert.Equal(Matching.Exact, new Candidate(1, 0, Matching.Prefix).Confidence);
        Assert.Equal(Matching.High, new Candidate(1, 2, Matching.Prefix).Confidence);
        Assert.Equal(Matching.Possible, new Candidate(1, 0, Matching.Contains).Confidence);
        Assert.Equal(Matching.Possible, new Candidate(1, 0, Matching.None).Confidence);
        Assert.Equal("same day, shop matches memo", new Candidate(1, 0, Matching.Prefix).Note);
        Assert.Equal("2 day(s) apart, shop not in memo", new Candidate(1, 2, Matching.None).Note);
    }

    [Fact]
    public void Buckets()
    {
        Assert.Equal(CandidateSet.NoCandidate, new CandidateSet([]).Bucket);

        var one = new CandidateSet([new Candidate(1, 0, Matching.Prefix)]);
        Assert.Equal(CandidateSet.Auto, one.Bucket);
        Assert.True(one.ShouldAutoLink);

        // Two equally good: a person has to choose.
        var two = new CandidateSet([new Candidate(1, 1, Matching.Prefix), new Candidate(2, 2, Matching.Prefix)]);
        Assert.True(two.IsAmbiguous);
        Assert.False(two.ShouldAutoLink);
        Assert.Equal(CandidateSet.Ambiguous, two.Bucket);

        // A better one beats a worse one: not ambiguous.
        var mixed = new CandidateSet([new Candidate(1, 0, Matching.Prefix), new Candidate(2, 0, Matching.None)]);
        Assert.Equal(1, mixed.Best!.TransactionId);
        Assert.True(mixed.ShouldAutoLink);

        var weak = new CandidateSet([new Candidate(1, 0, Matching.None)]);
        Assert.Equal(CandidateSet.Weak, weak.Bucket);
        Assert.False(weak.ShouldAutoLink);

        // Taking the best away leaves the rest to stand on their own.
        Assert.Equal(CandidateSet.Weak, mixed.Without(new HashSet<long> { 1 }).Bucket);
    }
}
