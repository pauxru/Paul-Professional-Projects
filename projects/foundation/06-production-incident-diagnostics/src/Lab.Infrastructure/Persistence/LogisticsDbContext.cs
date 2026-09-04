using Lab.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lab.Infrastructure.Persistence;

public sealed class LogisticsDbContext(DbContextOptions<LogisticsDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<LogisticsOrder> Orders => Set<LogisticsOrder>();

    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LogisticsDbContext).Assembly);
    }
}
