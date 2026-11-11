using Microsoft.EntityFrameworkCore;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Infrastructure.Data;

/// <summary>
/// The Django schema, mapped table-for-table. EF Core migrations manage it
/// from the InitialBaseline onwards; Django's own <c>django_migrations</c>
/// table is left alone.
/// </summary>
public class ReceiptScannerDbContext(DbContextOptions<ReceiptScannerDbContext> options) : DbContext(options)
{
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<ReceiptItem> ReceiptItems => Set<ReceiptItem>();
    public DbSet<ReceiptScan> ReceiptScans => Set<ReceiptScan>();

    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<OfxStatement> OfxStatements => Set<OfxStatement>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<MatchRejection> MatchRejections => Set<MatchRejection>();
    public DbSet<TransactionConflict> TransactionConflicts => Set<TransactionConflict>();

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Charset: tables created by migrations take the database default,
        // which is utf8mb4 on MySQL 8 and on the Django database. The
        // connection itself sets CharSet=utf8mb4 in the connection string.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReceiptScannerDbContext).Assembly);
        DjangoConventions.Apply(modelBuilder);
    }

    /// <summary>
    /// Django's auto_now_add / auto_now, which EF doesn't do on its own.
    /// Only fills a creation stamp that hasn't been set, so a caller (or a
    /// test) can still pin one.
    /// </summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimes(DateTime.UtcNow);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimes(DateTime.UtcNow);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    // property name → set on insert only (auto_now_add) or on every save (auto_now)
    private static readonly string[] AutoNowAdd =
        ["CreatedAt", "UploadedAt", "RejectedAt", "DetectedAt", "DateJoined"];
    private static readonly string[] AutoNow = ["UpdatedAt"];

    private void StampTimes(DateTime now)
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            foreach (var name in AutoNow)
            {
                var p = entry.Metadata.FindProperty(name);
                if (p is not null) entry.Property(name).CurrentValue = now;
            }

            if (entry.State != EntityState.Added) continue;
            foreach (var name in AutoNowAdd)
            {
                var p = entry.Metadata.FindProperty(name);
                if (p is not null && entry.Property(name).CurrentValue is DateTime d && d == default)
                    entry.Property(name).CurrentValue = now;
            }
        }
    }
}
