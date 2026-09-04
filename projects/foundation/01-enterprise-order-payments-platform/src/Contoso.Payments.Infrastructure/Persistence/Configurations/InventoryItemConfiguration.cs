using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Contoso.Payments.Domain.Inventory;

namespace Contoso.Payments.Infrastructure.Persistence.Configurations;

public sealed class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> b)
    {
        b.ToTable("Inventory");
        b.HasKey(i => i.ProductId);
        b.Property(i => i.Sku).HasMaxLength(64).IsRequired();
        b.Property(i => i.OnHand).IsRequired();
        b.Property(i => i.Version).IsConcurrencyToken();
        b.HasIndex(i => i.Sku).IsUnique();

        b.HasMany(i => i.Reservations)
            .WithOne()
            .HasForeignKey("InventoryProductId")
            .OnDelete(DeleteBehavior.Cascade);

        // Auto-include reservations whenever an InventoryItem is loaded so business methods
        // that inspect Available and Reserved see up-to-date state without callers having to
        // remember to .Include().
        b.Navigation(i => i.Reservations).AutoInclude();
    }
}

public sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> b)
    {
        b.ToTable("InventoryReservations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.OrderId).IsRequired();
        b.Property(x => x.Quantity).IsRequired();
        b.Property(x => x.State).HasConversion<int>().IsRequired();
        b.HasIndex(x => x.OrderId);
    }
}
