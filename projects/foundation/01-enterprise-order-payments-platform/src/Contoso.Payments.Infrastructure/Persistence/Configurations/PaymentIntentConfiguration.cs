using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Contoso.Payments.Domain.Payments;

namespace Contoso.Payments.Infrastructure.Persistence.Configurations;

public sealed class PaymentIntentConfiguration : IEntityTypeConfiguration<PaymentIntent>
{
    public void Configure(EntityTypeBuilder<PaymentIntent> b)
    {
        b.ToTable("PaymentIntents");
        b.HasKey(p => p.Id);
        b.Property(p => p.OrderId).IsRequired();
        b.Property(p => p.Status).HasConversion<int>().IsRequired();
        b.Property(p => p.IdempotencyKey).HasMaxLength(128).IsRequired();
        b.Property(p => p.ProviderReference).HasMaxLength(128);
        b.Property(p => p.CreatedAtUtc).IsRequired();
        b.Property(p => p.UpdatedAtUtc).IsRequired();
        b.Property(p => p.Version).IsConcurrencyToken();
        b.OwnsOne(p => p.Amount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("Amount").HasColumnType("decimal(18,4)");
            m.Property(x => x.Currency).HasColumnName("Currency").HasMaxLength(3);
        });
        b.OwnsOne(p => p.CapturedAmount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("CapturedAmount").HasColumnType("decimal(18,4)");
            m.Property(x => x.Currency).HasColumnName("CapturedCurrency").HasMaxLength(3);
        });
        b.HasIndex(p => new { p.OrderId, p.IdempotencyKey }).IsUnique();
        b.HasIndex(p => p.Status);
        b.HasMany(p => p.Attempts).WithOne().HasForeignKey("PaymentIntentId").OnDelete(DeleteBehavior.Cascade);
        b.Metadata.FindNavigation(nameof(PaymentIntent.Attempts))!.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> b)
    {
        b.ToTable("PaymentAttempts");
        b.HasKey(a => a.Id);
        b.Property(a => a.AttemptNumber).IsRequired();
        b.Property(a => a.Outcome).HasMaxLength(32).IsRequired();
        b.Property(a => a.ProviderReference).HasMaxLength(128);
        b.Property(a => a.AttemptedAtUtc).IsRequired();
        b.Property(a => a.LatencyMs).IsRequired();
    }
}
