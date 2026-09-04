using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Contoso.Payments.Domain.Refunds;

namespace Contoso.Payments.Infrastructure.Persistence.Configurations;

public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.ToTable("Refunds");
        b.HasKey(r => r.Id);
        b.Property(r => r.OrderId).IsRequired();
        b.Property(r => r.PaymentIntentId).IsRequired();
        b.Property(r => r.Reason).HasMaxLength(256).IsRequired();
        b.Property(r => r.IssuedAtUtc).IsRequired();
        b.Property(r => r.Version).IsConcurrencyToken();
        b.OwnsOne(r => r.Amount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("Amount").HasColumnType("decimal(18,4)");
            m.Property(x => x.Currency).HasColumnName("Currency").HasMaxLength(3);
        });
        b.HasIndex(r => r.OrderId);
        b.HasIndex(r => r.PaymentIntentId);
    }
}
