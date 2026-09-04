using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northstar.Domain.Policies;

namespace Northstar.Infrastructure.Persistence.Configurations;

public sealed class PolicyholderConfiguration : IEntityTypeConfiguration<Policyholder>
{
    public void Configure(EntityTypeBuilder<Policyholder> builder)
    {
        builder.ToTable("Policyholders");
        builder.HasKey(policyholder => policyholder.Id);
        builder.Property(policyholder => policyholder.Id).ValueGeneratedNever();
        builder.Property(policyholder => policyholder.Name).HasMaxLength(200).IsRequired();
        builder.Property(policyholder => policyholder.Email).HasMaxLength(320).IsRequired();
        builder.HasIndex(policyholder => policyholder.Email).IsUnique();
        builder.HasMany(policyholder => policyholder.Policies)
            .WithOne(policy => policy.Policyholder)
            .HasForeignKey(policy => policy.PolicyholderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
