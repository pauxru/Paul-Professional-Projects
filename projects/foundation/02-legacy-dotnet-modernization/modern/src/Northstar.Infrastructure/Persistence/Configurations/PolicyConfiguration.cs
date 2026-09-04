using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northstar.Domain.Policies;

namespace Northstar.Infrastructure.Persistence.Configurations;

public sealed class PolicyConfiguration : IEntityTypeConfiguration<Policy>
{
    public void Configure(EntityTypeBuilder<Policy> builder)
    {
        builder.ToTable("Policies", table =>
        {
            table.HasCheckConstraint("CK_Policies_Deductible", "\"DeductibleAmount\" >= 0");
            table.HasCheckConstraint("CK_Policies_Limit", "\"LimitAmount\" > 0");
        });
        builder.HasKey(policy => policy.Id);
        builder.Property(policy => policy.Id).ValueGeneratedNever();
        builder.Property(policy => policy.PolicyholderId).IsRequired();
        builder.Property(policy => policy.PolicyNumber).HasMaxLength(64).IsRequired();
        builder.HasIndex(policy => policy.PolicyNumber).IsUnique();
        builder.Property(policy => policy.DeductibleAmount).HasPrecision(18, 2);
        builder.Property(policy => policy.LimitAmount).HasPrecision(18, 2);
        builder.Property(policy => policy.Currency).HasMaxLength(3).IsRequired();
    }
}
