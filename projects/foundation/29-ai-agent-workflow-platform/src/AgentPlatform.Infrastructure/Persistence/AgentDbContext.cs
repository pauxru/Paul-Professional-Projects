using AgentPlatform.Domain.Approvals;
using AgentPlatform.Domain.Budgets;
using AgentPlatform.Domain.Runs;
using AgentPlatform.Domain.Tracing;
using AgentPlatform.Domain.Workflows;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentPlatform.Infrastructure.Persistence;

/// <summary>
/// The single persistence context. It stores the durable agent aggregates (runs, step executions,
/// trace events, approvals, idempotency records) alongside the fictional business data the tools
/// operate on. Runs and their state/position are persisted after every step, which is what makes a
/// run resumable after a crash.
/// </summary>
public sealed class AgentDbContext : DbContext
{
    public AgentDbContext(DbContextOptions<AgentDbContext> options) : base(options) { }

    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();
    public DbSet<StepExecution> StepExecutions => Set<StepExecution>();
    public DbSet<TraceEvent> TraceEvents => Set<TraceEvent>();
    public DbSet<ApprovalTask> Approvals => Set<ApprovalTask>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<KnowledgeArticle> KnowledgeArticles => Set<KnowledgeArticle>();
    public DbSet<OrderRecord> Orders => Set<OrderRecord>();
    public DbSet<RefundRequest> RefundRequests => Set<RefundRequest>();
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<WorkflowRun>(e =>
        {
            e.ToTable("Runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(64);
            e.Property(r => r.WorkflowName).HasMaxLength(128);
            e.Property(r => r.TenantId).HasMaxLength(64);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(r => r.Outcome).HasConversion<string?>().HasMaxLength(32);
            e.Property(r => r.HaltReason).HasConversion<string>().HasMaxLength(48);
            e.Property(r => r.IdempotencyKey).HasMaxLength(200);
            e.HasIndex(r => new { r.TenantId, r.IdempotencyKey });
            e.HasIndex(r => r.Status);
            e.Property(r => r.Version).IsConcurrencyToken();

            e.HasMany(r => r.StepExecutions).WithOne().HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(r => r.StepExecutions).HasField("_stepExecutions").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<StepExecution>(e =>
        {
            e.ToTable("StepExecutions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasMaxLength(64);
            e.Property(s => s.RunId).HasMaxLength(64);
            e.Property(s => s.StepId).HasMaxLength(128);
            e.Property(s => s.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<TraceEvent>(e =>
        {
            e.ToTable("TraceEvents");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasMaxLength(64);
            e.Property(t => t.RunId).HasMaxLength(64);
            e.Property(t => t.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(t => t.StepId).HasMaxLength(128);
            e.Property(t => t.ToolName).HasMaxLength(64);
            e.Property(t => t.PromptVersion).HasMaxLength(64);
            e.HasIndex(t => new { t.RunId, t.Ordinal });
        });

        modelBuilder.Entity<ApprovalTask>(e =>
        {
            e.ToTable("Approvals");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasMaxLength(64);
            e.Property(a => a.RunId).HasMaxLength(64);
            e.Property(a => a.StepId).HasMaxLength(128);
            e.Property(a => a.TenantId).HasMaxLength(64);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(a => a.RiskLevel).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(a => new { a.TenantId, a.Status });
            e.HasIndex(a => a.RunId);
        });

        modelBuilder.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("IdempotencyRecords");
            e.HasKey(r => r.Key);
            e.Property(r => r.Key).HasMaxLength(400);
            e.Property(r => r.RunId).HasMaxLength(64);
            e.Property(r => r.ToolName).HasMaxLength(64);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable("Customers");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasMaxLength(64);
            e.Property(c => c.Email).HasMaxLength(256);
            e.HasIndex(c => c.Email);
        });

        modelBuilder.Entity<Ticket>(e =>
        {
            e.ToTable("Tickets");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasMaxLength(64);
            e.Property(t => t.CustomerId).HasMaxLength(64);
            e.HasIndex(t => t.CustomerId);
        });

        modelBuilder.Entity<KnowledgeArticle>(e =>
        {
            e.ToTable("KnowledgeArticles");
            e.HasKey(k => k.Id);
            e.Property(k => k.Id).HasMaxLength(64);
        });

        modelBuilder.Entity<OrderRecord>(e =>
        {
            e.ToTable("Orders");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).HasMaxLength(64);
            e.Property(o => o.CustomerId).HasMaxLength(64);
            e.HasIndex(o => o.CustomerId);
        });

        modelBuilder.Entity<RefundRequest>(e =>
        {
            e.ToTable("RefundRequests");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(64);
            e.Property(r => r.CustomerId).HasMaxLength(64);
            e.HasIndex(r => r.CustomerId);
        });

        modelBuilder.Entity<EmailOutboxMessage>(e =>
        {
            e.ToTable("EmailOutbox");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasMaxLength(64);
            e.Property(m => m.To).HasMaxLength(256);
        });

        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasMaxLength(64);
            e.HasIndex(a => a.RunId);
        });

        // SQLite (the default provider) stores DateTimeOffset as TEXT and cannot ORDER BY / compare
        // it in SQL. Persisting UTC ticks as an INTEGER keeps ordering and range filters correct on
        // every provider while round-tripping losslessly (all timestamps are produced in UTC).
        var dateTimeOffsetToTicks = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
        var nullableDateTimeOffsetToTicks = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                    property.SetValueConverter(dateTimeOffsetToTicks);
                else if (property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(nullableDateTimeOffsetToTicks);
            }
        }
    }
}
