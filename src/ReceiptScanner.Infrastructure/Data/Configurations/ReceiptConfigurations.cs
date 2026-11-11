using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Infrastructure.Data.Configurations;

// Index names marked "Django" are copied from the Django migrations so the
// baseline lines up with production. Column names come from the snake_case
// convention in ReceiptScannerDbContext.
//
// Django never sets DB-level defaults, so neither do we: defaults live in the
// C# property initialisers, exactly as they lived in the Python models.
// Django's FileField/ImageField is varchar(100).

internal sealed class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    public void Configure(EntityTypeBuilder<Receipt> b)
    {
        b.ToTable("receipts");
        b.Property(x => x.Shop).HasMaxLength(255);
        b.Property(x => x.Total).HasPrecision(12, 2);
        b.Property(x => x.ClientUuid).IsDjangoUuid();
        b.Property(x => x.Image).HasMaxLength(100);
        b.Property(x => x.ReviewNote).HasMaxLength(255);
        b.Property(x => x.CategorySource).HasMaxLength(10);

        b.HasIndex(x => x.ClientUuid).IsUnique();
        b.HasIndex(x => x.Shop, "receipts_shop_1c0884_idx");                // Django
        b.HasIndex(x => x.PurchasedOn, "receipts_purchas_144f66_idx");      // Django
        b.HasIndex(x => x.SheetSyncedAt);
        b.HasIndex(x => x.NeedsReview);
        b.HasIndex(x => x.NoBankMatch);
        b.HasIndex(x => x.CategorySource);

        b.HasOne(x => x.Category).WithMany()
            .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);   // PROTECT
        b.HasOne(x => x.CategoryRule).WithMany()
            .HasForeignKey(x => x.CategoryRuleId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ReceiptItemConfiguration : IEntityTypeConfiguration<ReceiptItem>
{
    public void Configure(EntityTypeBuilder<ReceiptItem> b)
    {
        b.ToTable("receipt_items");
        b.Property(x => x.Name).HasMaxLength(255);
        b.Property(x => x.Qty).HasPrecision(10, 3);
        b.Property(x => x.LineTotal).HasPrecision(12, 2);
        b.Property(x => x.CategorySource).HasMaxLength(10);
        b.Ignore(x => x.UnitPrice);

        b.HasIndex(x => x.Name, "receipt_ite_name_b17b8f_idx");             // Django

        b.HasOne(x => x.Receipt).WithMany(r => r.Items)
            .HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Category).WithMany()
            .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);   // PROTECT
    }
}

internal sealed class ReceiptScanConfiguration : IEntityTypeConfiguration<ReceiptScan>
{
    public void Configure(EntityTypeBuilder<ReceiptScan> b)
    {
        b.ToTable("receipt_scans");
        b.Property(x => x.Image).HasMaxLength(100);
        b.Property(x => x.Thumbnail).HasMaxLength(100);
        b.Property(x => x.ContentHash).HasMaxLength(64);
        b.Property(x => x.ClientUuid).IsDjangoUuid();
        b.Property(x => x.Status).HasMaxLength(12);
        b.Property(x => x.OriginalFilename).HasMaxLength(255);
        b.Property(x => x.MimeType).HasMaxLength(100);
        b.Property(x => x.ImageFormat).HasMaxLength(20);
        b.Property(x => x.Note).HasMaxLength(255);
        b.Property(x => x.Extraction).HasColumnType("json");
        b.Property(x => x.ExtractionCheck).HasMaxLength(12);
        b.Property(x => x.ExtractionNotes).HasColumnType("json");
        b.Property(x => x.ExtractionText).HasColumnType("longtext");
        b.Property(x => x.ExtractionError).HasColumnType("longtext");
        b.Property(x => x.ExtractionTaskId).HasMaxLength(64);
        b.Ignore(x => x.Megapixels);

        b.HasIndex(x => x.ClientUuid).IsUnique();
        b.HasIndex(x => x.ContentHash);
        b.HasIndex(x => x.CapturedAt);
        b.HasIndex(x => x.UploadedAt);
        b.HasIndex(x => x.Status);
        b.HasIndex(x => new { x.Status, x.UploadedAt }, "receipt_sca_status_fab29b_idx")   // Django
            .IsDescending(false, true);

        b.HasOne(x => x.UploadedBy).WithMany()
            .HasForeignKey(x => x.UploadedById).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.ReviewedBy).WithMany()
            .HasForeignKey(x => x.ReviewedById).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Receipt).WithMany(r => r.Scans)
            .HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.SetNull);
    }
}
