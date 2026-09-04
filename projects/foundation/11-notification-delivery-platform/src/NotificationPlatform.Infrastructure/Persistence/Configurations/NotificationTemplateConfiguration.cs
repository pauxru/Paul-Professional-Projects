namespace NotificationPlatform.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationPlatform.Domain.Templates;

public sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> b)
    {
        b.ToTable("templates");
        b.HasKey(x => x.Id);
        b.Property(x => x.TemplateKey).HasMaxLength(150).IsRequired();
        b.Property(x => x.Locale).HasMaxLength(20).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(300);
        b.Property(x => x.Body).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.TemplateKey, x.Channel, x.Locale, x.Version }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.TemplateKey, x.Channel, x.Locale, x.IsActive });
    }
}
