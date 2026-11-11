using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Infrastructure.Data.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("categories");
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Kind).HasMaxLength(10);
        b.Ignore(x => x.FullName);

        b.HasIndex(x => x.Name).IsUnique();

        b.HasOne(x => x.Parent).WithMany(p => p.Children)
            .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);   // PROTECT
    }
}

internal sealed class CategoryRuleConfiguration : IEntityTypeConfiguration<CategoryRule>
{
    public void Configure(EntityTypeBuilder<CategoryRule> b)
    {
        b.ToTable("category_rules");
        b.Property(x => x.Pattern).HasMaxLength(255);
        b.Property(x => x.MatchType).HasMaxLength(10);
        b.Property(x => x.AppliesTo).HasMaxLength(12);
        b.Property(x => x.CreatedBy).HasMaxLength(10);
        b.Property(x => x.Note).HasMaxLength(255);

        b.HasIndex(x => new { x.Pattern, x.MatchType, x.AppliesTo }, "uniq_category_rule").IsUnique();   // Django

        b.HasOne(x => x.Category).WithMany()
            .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Django's auth_user, mapped as-is. Its id is a plain int (Django AutoField).</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("auth_user");
        b.Property(x => x.Password).HasMaxLength(128);
        b.Property(x => x.Username).HasMaxLength(150);
        b.Property(x => x.FirstName).HasMaxLength(150);
        b.Property(x => x.LastName).HasMaxLength(150);
        b.Property(x => x.Email).HasMaxLength(254);

        b.HasIndex(x => x.Username).IsUnique();
    }
}
