namespace NotificationPlatform.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationPlatform.Domain.Recipients;

public sealed class RecipientConfiguration : IEntityTypeConfiguration<Recipient>
{
    public void Configure(EntityTypeBuilder<Recipient> b)
    {
        b.ToTable("recipients");
        b.HasKey(x => x.Id);
        b.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.PhoneE164).HasMaxLength(30);
        b.Property(x => x.PushToken).HasMaxLength(500);
        b.Property(x => x.WebhookUrl).HasMaxLength(500);
        b.Property(x => x.Locale).HasMaxLength(20);
        b.Property(x => x.TimeZoneId).HasMaxLength(80);
        b.Property(x => x.FirstName).HasMaxLength(120);
        b.Property(x => x.LastName).HasMaxLength(120);
        b.HasIndex(x => new { x.TenantId, x.ExternalId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Email });
    }
}
