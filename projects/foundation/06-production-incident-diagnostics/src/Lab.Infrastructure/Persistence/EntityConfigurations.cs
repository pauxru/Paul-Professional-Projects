using Lab.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lab.Infrastructure.Persistence;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(254).IsRequired();
        builder.HasIndex(x => x.Email).IsUnique();
    }
}

public sealed class LogisticsOrderConfiguration : IEntityTypeConfiguration<LogisticsOrder>
{
    public void Configure(EntityTypeBuilder<LogisticsOrder> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Reference).HasMaxLength(48).IsRequired();
        builder.Property(x => x.Destination).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.CreatedAt)
            .HasConversion(value => value.ToUnixTimeMilliseconds(), value => DateTimeOffset.FromUnixTimeMilliseconds(value))
            .HasColumnType("INTEGER");
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.Reference).IsUnique();
        builder.HasIndex(x => x.CustomerId);
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Shipment)
            .WithOne()
            .HasForeignKey<Shipment>(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> builder)
    {
        builder.ToTable("Shipments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TrackingNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DispatchedAt)
            .HasConversion(value => value.ToUnixTimeMilliseconds(), value => DateTimeOffset.FromUnixTimeMilliseconds(value))
            .HasColumnType("INTEGER");
        builder.Property(x => x.DeliveredAt)
            .HasConversion(new ValueConverter<DateTimeOffset?, long?>(
                value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
                value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null))
            .HasColumnType("INTEGER");
        builder.HasIndex(x => x.TrackingNumber).IsUnique();
        builder.HasIndex(x => x.OrderId).IsUnique();
    }
}
