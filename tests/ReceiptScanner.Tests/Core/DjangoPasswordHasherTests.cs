using ReceiptScanner.Core.Auth;

namespace ReceiptScanner.Tests.Core;

public class DjangoPasswordHasherTests
{
    // Both generated with Python's hashlib.pbkdf2_hmac, which is what Django uses.
    private const string Django870k = "pbkdf2_sha256$870000$fXDvYwUZ3AHT2fyuiN2D0p$CyOfC/01Zcodxy3yHNqmrn4jKWiQOvA0qI4pwPSeUjQ=";
    private const string Django1k = "pbkdf2_sha256$1000$abc123SALTxyz$LLucqgHKVOwi51DCM3mlQHCIMMD6AuTghk/8ofAInD0=";

    [Fact]
    public void Verifies_hashes_made_by_Django()
    {
        Assert.True(DjangoPasswordHasher.Verify("correct horse", Django870k));
        Assert.True(DjangoPasswordHasher.Verify("pässwörd", Django1k));
        Assert.False(DjangoPasswordHasher.Verify("correct horse ", Django870k));
        Assert.False(DjangoPasswordHasher.Verify("passwörd", Django1k));
    }

    [Fact]
    public void Produces_the_same_hash_as_Django_for_the_same_salt()
    {
        Assert.Equal(Django1k, DjangoPasswordHasher.Hash("pässwörd", 1000, "abc123SALTxyz"));
    }

    [Fact]
    public void New_hashes_use_Djangos_format_and_defaults()
    {
        var hash = DjangoPasswordHasher.Hash("s3cret-pass", iterations: 1000);
        var parts = hash.Split('$');

        Assert.Equal("pbkdf2_sha256", parts[0]);
        Assert.Equal("1000", parts[1]);
        Assert.Equal(22, parts[2].Length);
        Assert.True(DjangoPasswordHasher.Verify("s3cret-pass", hash));
        Assert.NotEqual(hash, DjangoPasswordHasher.Hash("s3cret-pass", iterations: 1000));   // random salt
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!unusablepasswordmarker")]
    [InlineData("argon2$argon2id$v=19$m=102400,t=2,p=8$c2FsdA$aGFzaA")]
    [InlineData("pbkdf2_sha256$abc$salt$hash")]
    [InlineData("pbkdf2_sha256$1000$salt$not-base64!!")]
    public void Never_verifies_other_formats(string? encoded)
    {
        Assert.False(DjangoPasswordHasher.Verify("anything", encoded));
    }

    [Fact]
    public void Old_iteration_counts_need_a_rehash()
    {
        Assert.True(DjangoPasswordHasher.NeedsRehash(Django1k));
        Assert.False(DjangoPasswordHasher.NeedsRehash(Django870k));
    }
}
