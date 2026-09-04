using Microsoft.EntityFrameworkCore;
using Northstar.Domain.Claims;
using Northstar.Domain.Policies;

namespace Northstar.Infrastructure.Persistence;

public sealed class NorthstarDbContext(DbContextOptions<NorthstarDbContext> options) : DbContext(options)
{
    public DbSet<Policyholder> Policyholders => Set<Policyholder>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimDocument> ClaimDocuments => Set<ClaimDocument>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NorthstarDbContext).Assembly);
    }
}
