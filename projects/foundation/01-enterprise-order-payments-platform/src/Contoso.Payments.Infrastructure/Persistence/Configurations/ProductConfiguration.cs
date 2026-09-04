using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Contoso.Payments.Domain.Catalog;
using Contoso.Payments.Domain.Common;

namespace Contoso.Payments.Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("Products");
        b.HasKey(p => p.Id);
        b.Property(p => p.Sku).HasMaxLength(64).IsRequired();
        b.Property(p => p.Name).HasMaxLength(256).IsRequired();
        b.Property(p => p.IsActive).IsRequired();
        b.Property(p => p.Version).IsConcurrencyToken();
        b.OwnsOne(p => p.Price, price =>
        {
            price.Property(m => m.Amount).HasColumnName("PriceAmount").HasColumnType("decimal(18,4)").IsRequired();
            price.Property(m => m.Currency).HasColumnName("PriceCurrency").HasMaxLength(3).IsRequired();
        });
        b.HasIndex(p => p.Sku).IsUnique();
    }
}
