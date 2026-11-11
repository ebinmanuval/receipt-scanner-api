using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Infrastructure.Data.Configurations;

internal sealed class BankAccountConfiguration : IEntityTypeConfiguration<BankAccount>
{
    public void Configure(EntityTypeBuilder<BankAccount> b)
    {
        b.ToTable("bank_accounts");
        b.Property(x => x.BankId).HasMaxLength(32);
        b.Property(x => x.AccountId).HasMaxLength(64);
        b.Property(x => x.AccountType).HasMaxLength(32);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Nickname).HasMaxLength(100);
        b.Property(x => x.DedupeStrategy).HasMaxLength(10);
        b.Ignore(x => x.MaskedAccountId);

        b.HasIndex(x => new { x.BankId, x.AccountId }, "uniq_bank_account").IsUnique();   // Django
    }
}

internal sealed class OfxStatementConfiguration : IEntityTypeConfiguration<OfxStatement>
{
    public void Configure(EntityTypeBuilder<OfxStatement> b)
    {
        b.ToTable("ofx_statements");
        b.Property(x => x.LedgerBalance).HasPrecision(14, 2);
        b.Property(x => x.AvailableBalance).HasPrecision(14, 2);
        b.Property(x => x.File).HasMaxLength(100);
        b.Property(x => x.OriginalFilename).HasMaxLength(255);
        b.Property(x => x.FileHash).HasMaxLength(64);

        b.HasIndex(x => x.FileHash);
        b.HasIndex(x => new { x.StartDate, x.EndDate }, "ofx_stateme_start_d_7ef5c2_idx");   // Django

        b.HasOne(x => x.Account).WithMany(a => a.Statements)
            .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BankTransactionConfiguration : IEntityTypeConfiguration<BankTransaction>
{
    public void Configure(EntityTypeBuilder<BankTransaction> b)
    {
        b.ToTable("bank_transactions");
        b.Property(x => x.Fitid).HasMaxLength(255);
        b.Property(x => x.DedupeKey).HasMaxLength(255);
        b.Property(x => x.Trntype).HasMaxLength(20);
        b.Property(x => x.Amount).HasPrecision(14, 2);
        b.Property(x => x.Memo).HasMaxLength(512);
        b.Property(x => x.Name).HasMaxLength(255);
        b.Property(x => x.Checknum).HasMaxLength(64);
        b.Property(x => x.MatchMethod).HasMaxLength(10);
        b.Property(x => x.MatchConfidence).HasMaxLength(20);
        b.Property(x => x.MatchNote).HasMaxLength(255);
        b.Property(x => x.CategorySource).HasMaxLength(10);
        b.Ignore(x => x.IsDebit);

        b.HasIndex(x => new { x.AccountId, x.DedupeKey }, "uniq_account_dedupe_key").IsUnique();   // Django
        b.HasIndex(x => x.DedupeKey);
        b.HasIndex(x => new { x.PostedOn, x.Amount }, "bank_transa_posted__5529ce_idx");          // Django
        b.HasIndex(x => new { x.AccountId, x.PostedOn }, "bank_transa_account_d9e0db_idx");       // Django
        b.HasIndex(x => x.CategorySource);

        b.HasOne(x => x.Account).WithMany(a => a.Transactions)
            .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Statement).WithMany(s => s.Transactions)
            .HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.SetNull);

        // One-to-one: a receipt is paid for by exactly one transaction. The
        // unique index on receipt_id is what stops a matcher bug claiming a
        // receipt twice.
        b.HasOne(x => x.Receipt).WithOne(r => r.BankTransaction)
            .HasForeignKey<BankTransaction>(x => x.ReceiptId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.ReceiptId).IsUnique();

        // Transfer pairs point at each other (self one-to-one).
        b.HasOne(x => x.TransferPair).WithOne()
            .HasForeignKey<BankTransaction>(x => x.TransferPairId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.TransferPairId).IsUnique();

        b.HasOne(x => x.Category).WithMany()
            .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);   // PROTECT
        b.HasOne(x => x.CategoryRule).WithMany()
            .HasForeignKey(x => x.CategoryRuleId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class MatchRejectionConfiguration : IEntityTypeConfiguration<MatchRejection>
{
    public void Configure(EntityTypeBuilder<MatchRejection> b)
    {
        b.ToTable("receipt_match_rejections");
        b.Property(x => x.Note).HasMaxLength(255);

        b.HasIndex(x => new { x.ReceiptId, x.TransactionId }, "uniq_match_rejection").IsUnique();   // Django

        b.HasOne(x => x.Receipt).WithMany(r => r.MatchRejections)
            .HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Transaction).WithMany(t => t.MatchRejections)
            .HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TransactionConflictConfiguration : IEntityTypeConfiguration<TransactionConflict>
{
    public void Configure(EntityTypeBuilder<TransactionConflict> b)
    {
        b.ToTable("bank_transaction_conflicts");
        b.Property(x => x.Fields).HasColumnType("json");
        b.Property(x => x.Stored).HasColumnType("json");
        b.Property(x => x.Incoming).HasColumnType("json");
        b.Property(x => x.Resolution).HasMaxLength(20);
        b.Property(x => x.Note).HasMaxLength(255);
        b.Ignore(x => x.IsOpen);

        b.HasIndex(x => x.ResolvedAt);

        b.HasOne(x => x.Transaction).WithMany(t => t.Conflicts)
            .HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Statement).WithMany(s => s.Conflicts)
            .HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.SetNull);
    }
}
