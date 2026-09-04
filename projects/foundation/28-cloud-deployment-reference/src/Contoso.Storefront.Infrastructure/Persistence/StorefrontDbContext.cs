using Contoso.Storefront.Domain;
using Microsoft.EntityFrameworkCore;

namespace Contoso.Storefront.Infrastructure.Persistence;

public sealed class StorefrontDbContext(DbContextOptions<StorefrontDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<WorkerCheckpoint> WorkerCheckpoints => Set<WorkerCheckpoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StorefrontDbContext).Assembly);
    }
}
