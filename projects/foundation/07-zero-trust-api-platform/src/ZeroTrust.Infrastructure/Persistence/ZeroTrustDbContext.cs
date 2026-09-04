using Microsoft.EntityFrameworkCore;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Domain.Customer;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Domain.Partner;

namespace ZeroTrust.Infrastructure.Persistence;

public sealed class ZeroTrustDbContext : DbContext
{
    public ZeroTrustDbContext(DbContextOptions<ZeroTrustDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<BreakGlassGrant> BreakGlassGrants => Set<BreakGlassGrant>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Statement> Statements => Set<Statement>();
    public DbSet<PaymentInitiationRequest> PaymentInitiations => Set<PaymentInitiationRequest>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ZeroTrustDbContext).Assembly);
    }
}
