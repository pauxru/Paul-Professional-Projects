using Contoso.Storefront.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Contoso.Storefront.Infrastructure.Persistence;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.HasKey(product => product.Id);
        builder.Property(product => product.Id).HasColumnName("id");
        builder.Property(product => product.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        builder.Property(product => product.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(product => product.Description).HasColumnName("description_v2").HasMaxLength(2_000).IsRequired();
        builder.Property(product => product.PriceAmount).HasColumnName("price_amount").HasPrecision(18, 2);
        builder.Property(product => product.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        builder.Property(product => product.IsActive).HasColumnName("is_active");
        builder.Property(product => product.CreatedAt).HasColumnName("created_at");
        builder.Property(product => product.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Ignore(product => product.Price);
        builder.HasIndex(product => product.Sku).IsUnique().HasDatabaseName("ux_products_sku");
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(order => order.Id);
        builder.Property(order => order.Id).HasColumnName("id");
        builder.Property(order => order.CustomerReference).HasColumnName("customer_reference").HasMaxLength(100).IsRequired();
        builder.Property(order => order.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100).IsRequired();
        builder.Property(order => order.CreatedAt).HasColumnName("created_at");
        builder.Property(order => order.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(order => order.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Ignore(order => order.Total);
        builder.HasIndex(order => order.IdempotencyKey).IsUnique().HasDatabaseName("ux_orders_idempotency_key");
        builder.HasMany(order => order.Items)
            .WithOne()
            .HasForeignKey(item => item.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(order => order.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.OrderId).HasColumnName("order_id");
        builder.Property(item => item.ProductId).HasColumnName("product_id");
        builder.Property(item => item.ProductName).HasColumnName("product_name").HasMaxLength(200).IsRequired();
        builder.Property(item => item.Quantity).HasColumnName("quantity");
        builder.Property(item => item.UnitPriceAmount).HasColumnName("unit_price_amount").HasPrecision(18, 2);
        builder.Property(item => item.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        builder.Ignore(item => item.LineTotal);
        builder.HasIndex(item => item.OrderId).HasDatabaseName("ix_order_items_order_id");
        builder.HasIndex(item => item.ProductId).HasDatabaseName("ix_order_items_product_id");
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).HasColumnName("id");
        builder.Property(message => message.Type).HasColumnName("type").HasMaxLength(200).IsRequired();
        builder.Property(message => message.Payload).HasColumnName("payload").IsRequired();
        builder.Property(message => message.OccurredAt).HasColumnName("occurred_at");
        builder.Property(message => message.ProcessedAt).HasColumnName("processed_at");
        builder.Property(message => message.DeliveryAttempts).HasColumnName("delivery_attempts");
        builder.Property(message => message.LastError).HasColumnName("last_error").HasMaxLength(2_000);
        builder.HasIndex(message => new { message.ProcessedAt, message.OccurredAt })
            .HasDatabaseName("ix_outbox_pending");
    }
}

internal sealed class WorkerCheckpointConfiguration : IEntityTypeConfiguration<WorkerCheckpoint>
{
    public void Configure(EntityTypeBuilder<WorkerCheckpoint> builder)
    {
        builder.ToTable("worker_checkpoints");
        builder.HasKey(checkpoint => checkpoint.WorkerName);
        builder.Property(checkpoint => checkpoint.WorkerName).HasColumnName("worker_name").HasMaxLength(100);
        builder.Property(checkpoint => checkpoint.LastMessageId).HasColumnName("last_message_id");
        builder.Property(checkpoint => checkpoint.UpdatedAt).HasColumnName("updated_at");
    }
}
