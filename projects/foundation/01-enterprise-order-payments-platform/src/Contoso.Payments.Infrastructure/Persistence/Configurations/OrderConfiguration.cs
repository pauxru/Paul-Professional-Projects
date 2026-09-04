using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Contoso.Payments.Domain.Orders;

namespace Contoso.Payments.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("Orders");
        b.HasKey(o => o.Id);
        b.Property(o => o.CustomerRef).HasMaxLength(128).IsRequired();
        b.Property(o => o.Currency).HasMaxLength(3).IsRequired();
        b.Property(o => o.Status).HasConversion<int>().IsRequired();
        b.Property(o => o.CreatedAtUtc).IsRequired();
        b.Property(o => o.UpdatedAtUtc).IsRequired();
        b.Property(o => o.PaymentIntentId);
        b.Property(o => o.Version).IsConcurrencyToken();
        b.OwnsOne(o => o.RefundedTotal, m =>
        {
            m.Property(x => x.Amount).HasColumnName("RefundedAmount").HasColumnType("decimal(18,4)");
            m.Property(x => x.Currency).HasColumnName("RefundedCurrency").HasMaxLength(3);
        });

        b.HasMany(o => o.Lines).WithOne().HasForeignKey("OrderId").OnDelete(DeleteBehavior.Cascade);
        b.Metadata.FindNavigation(nameof(Order.Lines))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(o => o.Lines).AutoInclude();
        b.HasIndex(o => o.CreatedAtUtc);
        b.HasIndex(o => o.CustomerRef);
        b.HasIndex(o => o.Status);
    }
}

public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> b)
    {
        b.ToTable("OrderLines");
        b.HasKey(l => l.Id);
        b.Property(l => l.Sku).HasMaxLength(64).IsRequired();
        b.Property(l => l.Quantity).IsRequired();
        b.OwnsOne(l => l.UnitPrice, m =>
        {
            m.Property(x => x.Amount).HasColumnName("UnitPriceAmount").HasColumnType("decimal(18,4)");
            m.Property(x => x.Currency).HasColumnName("UnitPriceCurrency").HasMaxLength(3);
        });
        b.Property(l => l.ReservationId);
        b.Ignore(l => l.LineTotal);
    }
}
