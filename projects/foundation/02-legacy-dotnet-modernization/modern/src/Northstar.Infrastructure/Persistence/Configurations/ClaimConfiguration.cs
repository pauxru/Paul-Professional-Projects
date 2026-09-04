using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northstar.Domain.Claims;

namespace Northstar.Infrastructure.Persistence.Configurations;

public sealed class ClaimConfiguration : IEntityTypeConfiguration<Claim>
{
    public void Configure(EntityTypeBuilder<Claim> builder)
    {
        builder.ToTable("Claims", table =>
        {
            table.HasCheckConstraint("CK_Claims_ClaimedAmount", "\"ClaimedAmount\" > 0");
            table.HasCheckConstraint("CK_Claims_ReserveAmount", "\"ReserveAmount\" >= 0");
            table.HasCheckConstraint("CK_Claims_SettlementAmount", "\"SettlementAmount\" >= 0");
        });
        builder.HasKey(claim => claim.Id);
        builder.Property(claim => claim.Id).ValueGeneratedNever();
        builder.Property(claim => claim.PolicyId).IsRequired();
        builder.Property(claim => claim.Reference).HasMaxLength(64).IsRequired();
        builder.HasIndex(claim => claim.Reference).IsUnique();
        builder.HasIndex(claim => new { claim.Status, claim.CreatedAt });
        builder.Property(claim => claim.ClaimedAmount).HasPrecision(18, 2);
        builder.Property(claim => claim.ReserveAmount).HasPrecision(18, 2);
        builder.Property(claim => claim.SettlementAmount).HasPrecision(18, 2);
        builder.Property(claim => claim.Currency).HasMaxLength(3).IsRequired();
        builder.Property(claim => claim.AssignedAdjuster).HasMaxLength(200);
        builder.Property(claim => claim.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(claim => claim.Version).IsConcurrencyToken();
        builder.Property(claim => claim.CreatedAt).HasConversion<string>();
        builder.HasOne<Northstar.Domain.Policies.Policy>()
            .WithMany()
            .HasForeignKey(claim => claim.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(claim => claim.Documents)
            .WithOne()
            .HasForeignKey(document => document.ClaimId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(claim => claim.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
