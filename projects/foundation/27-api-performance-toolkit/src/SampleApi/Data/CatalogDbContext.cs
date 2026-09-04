using Microsoft.EntityFrameworkCore;
using SampleApi.Models;

namespace SampleApi.Data;

public sealed class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public bool UseSkuIndex { get; set; } = true;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        var product = builder.Entity<Product>();
        product.HasKey(p => p.Id);
        product.Property(p => p.Sku).IsRequired().HasMaxLength(64);
        product.Property(p => p.Name).IsRequired().HasMaxLength(200);
        product.Property(p => p.Price).HasColumnType("decimal(18,2)");
        product.Property(p => p.Category).HasMaxLength(64);
        if (UseSkuIndex) product.HasIndex(p => p.Sku).IsUnique();
        product.HasIndex(p => p.Category);

        var order = builder.Entity<Order>();
        order.HasKey(o => o.Id);
        order.Property(o => o.CustomerRef).IsRequired().HasMaxLength(64);
        order.Property(o => o.Status).IsRequired().HasMaxLength(24);
        order.Property(o => o.Total).HasColumnType("decimal(18,2)");
        order.HasIndex(o => o.CustomerRef);

        var line = builder.Entity<OrderLine>();
        line.HasKey(l => l.Id);
        line.Property(l => l.UnitPrice).HasColumnType("decimal(18,2)");
        line.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        line.HasIndex(l => l.OrderId);
    }
}
