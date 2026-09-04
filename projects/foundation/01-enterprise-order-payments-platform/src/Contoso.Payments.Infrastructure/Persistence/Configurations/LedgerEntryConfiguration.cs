using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Contoso.Payments.Domain.Ledger;

namespace Contoso.Payments.Infrastructure.Persistence.Configurations;

public sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> b)
    {
        b.ToTable("LedgerEntries");
        b.HasKey(l => l.Id);
        b.Property(l => l.OrderId).IsRequired();
        b.Property(l => l.Kind).HasConversion<int>().IsRequired();
        b.Property(l => l.MinorUnits).IsRequired();
        b.Property(l => l.Currency).HasMaxLength(3).IsRequired();
        b.Property(l => l.ReferenceType).HasMaxLength(32).IsRequired();
        b.Property(l => l.PostedAtUtc).IsRequired();
        b.HasIndex(l => l.OrderId);
        b.HasIndex(l => l.PaymentIntentId);
        b.HasIndex(l => l.PostedAtUtc);
        b.Ignore("_asMoney");
    }
}
