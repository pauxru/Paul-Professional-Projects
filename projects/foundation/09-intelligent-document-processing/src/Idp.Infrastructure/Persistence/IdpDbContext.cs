using Idp.Domain.Audit;
using Idp.Domain.Documents;
using Idp.Domain.Exports;
using Idp.Domain.Review;
using Idp.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace Idp.Infrastructure.Persistence;

/// <summary>EF Core context for the intelligent-document-processing platform (SQLite by default).</summary>
public sealed class IdpDbContext : DbContext
{
    public IdpDbContext(DbContextOptions<IdpDbContext> options) : base(options) { }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<ExtractedField> ExtractedFields => Set<ExtractedField>();
    public DbSet<LineItem> LineItems => Set<LineItem>();
    public DbSet<PipelineTransition> PipelineTransitions => Set<PipelineTransition>();
    public DbSet<DocumentValidation> DocumentValidations => Set<DocumentValidation>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierHint> SupplierHints => Set<SupplierHint>();
    public DbSet<ReviewTask> ReviewTasks => Set<ReviewTask>();
    public DbSet<Correction> Corrections => Set<Correction>();
    public DbSet<ExportRecord> ExportRecords => Set<ExportRecord>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdpDbContext).Assembly);
    }
}
