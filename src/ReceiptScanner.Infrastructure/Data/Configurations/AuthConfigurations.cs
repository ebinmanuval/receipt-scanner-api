using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Infrastructure.Data.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("auth_refresh_tokens");
        b.Property(x => x.Jti).HasMaxLength(32).HasColumnType("char(32)");

        b.HasIndex(x => x.Jti).IsUnique();
        b.HasIndex(x => x.ExpiresAt);   // for pruning expired rows

        b.HasOne(x => x.User).WithMany()
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
