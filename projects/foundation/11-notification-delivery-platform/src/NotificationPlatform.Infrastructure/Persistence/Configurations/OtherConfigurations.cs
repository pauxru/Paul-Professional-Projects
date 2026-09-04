namespace NotificationPlatform.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationPlatform.Domain.Preferences;
using NotificationPlatform.Domain.Providers;
using NotificationPlatform.Domain.Suppressions;

public sealed class RecipientPreferenceConfiguration : IEntityTypeConfiguration<RecipientPreference>
{
    public void Configure(EntityTypeBuilder<RecipientPreference> b)
    {
        b.ToTable("preferences");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.TenantId, x.RecipientId, x.Channel, x.Category }).IsUnique();
    }
}

public sealed class SuppressionEntryConfiguration : IEntityTypeConfiguration<SuppressionEntry>
{
    public void Configure(EntityTypeBuilder<SuppressionEntry> b)
    {
        b.ToTable("suppressions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Address).HasMaxLength(320).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasIndex(x => new { x.TenantId, x.Channel, x.Address }).IsUnique();
    }
}

public sealed class ProviderHealthConfiguration : IEntityTypeConfiguration<ProviderHealth>
{
    public void Configure(EntityTypeBuilder<ProviderHealth> b)
    {
        b.ToTable("provider_health");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProviderName).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.ProviderName).IsUnique();
    }
}

public sealed class TenantUsageCounterConfiguration : IEntityTypeConfiguration<TenantUsageCounter>
{
    public void Configure(EntityTypeBuilder<TenantUsageCounter> b)
    {
        b.ToTable("tenant_usage");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.TenantId, x.YearMonth }).IsUnique();
    }
}

public sealed class RecipientFrequencyCounterConfiguration : IEntityTypeConfiguration<RecipientFrequencyCounter>
{
    public void Configure(EntityTypeBuilder<RecipientFrequencyCounter> b)
    {
        b.ToTable("recipient_frequency");
        b.HasKey(x => x.Id);
        b.Property(x => x.DateKey).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.RecipientId, x.Category, x.DateKey }).IsUnique();
    }
}
