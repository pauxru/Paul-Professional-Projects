using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RagAssistant.Infrastructure.Persistence.Configurations;

public sealed class UsageEntryConfiguration : IEntityTypeConfiguration<UsageEntryRecord>
{
    public void Configure(EntityTypeBuilder<UsageEntryRecord> builder)
    {
        builder.ToTable("usage");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedOnAdd();
        builder.Property(u => u.Tenant).IsRequired().HasMaxLength(128);
        builder.Property(u => u.UserId).IsRequired().HasMaxLength(128);
        builder.Property(u => u.Model).IsRequired().HasMaxLength(128);
        builder.Property(u => u.PromptVersion).HasMaxLength(200);
        builder.Property(u => u.Cost).HasConversion<double>();
        builder.HasIndex(u => new { u.Tenant, u.Timestamp });
    }
}
