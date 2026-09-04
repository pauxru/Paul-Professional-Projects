using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence.Configurations;

public sealed class ReconciliationRunConfiguration : IEntityTypeConfiguration<ReconciliationRun>
{
    public void Configure(EntityTypeBuilder<ReconciliationRun> b)
    {
        b.ToTable("runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.RuleSetVersionTag).HasMaxLength(150);
        b.Property(x => x.InputChecksum).HasMaxLength(64);
        b.Property(x => x.TriggeredBy).HasMaxLength(100);
        b.Property(x => x.TotalsJson).IsRequired();
        b.Property(x => x.ExceptionBreakdownJson).IsRequired();
        b.Property(x => x.BalanceAssertionDetail).HasMaxLength(2000);
        b.Property(x => x.Notes).HasMaxLength(2000);

        b.HasIndex(x => x.StartedAtUtc);
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.InputChecksum);
    }
}
