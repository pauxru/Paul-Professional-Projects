using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Fees;
using ExampleBank.Ledger.Domain.Holds;
using ExampleBank.Ledger.Domain.Interest;
using ExampleBank.Ledger.Domain.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExampleBank.Ledger.Infrastructure.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("accounts");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.Code).IsRequired().HasMaxLength(64);
        b.Property(a => a.Name).IsRequired().HasMaxLength(200);
        b.Property(a => a.Type).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.NormalBalance).HasConversion<string>().HasMaxLength(8);
        b.Property(a => a.Currency).IsRequired().HasMaxLength(3);
        b.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.Version).IsConcurrencyToken();

        b.HasIndex(a => a.Code).IsUnique();
        b.HasIndex(a => a.ParentId);
        b.HasIndex(a => new { a.Type, a.Currency });

        b.Ignore(a => a.BalanceMinor);
        b.Ignore(a => a.AvailableMinor);
        b.Ignore(a => a.DebitSignedBalanceMinor);
        b.Ignore(a => a.IncreaseDirection);
        b.Ignore(a => a.DecreaseDirection);
        b.Ignore(a => a.Balance);
        b.Ignore(a => a.Available);
        b.Ignore(a => a.CurrencyRef);
    }
}

public sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> b)
    {
        b.ToTable("journal_entries");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.SequenceNumber);
        b.Property(e => e.Type).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Description).IsRequired().HasMaxLength(512);
        b.Property(e => e.Reference).HasMaxLength(200);
        b.Property(e => e.SourceSystem).IsRequired().HasMaxLength(64);
        b.Property(e => e.CorrelationId).IsRequired().HasMaxLength(64);
        b.Property(e => e.IdempotencyKey).HasMaxLength(200);
        b.Property(e => e.PreviousHash).IsRequired().HasMaxLength(64);
        b.Property(e => e.Hash).IsRequired().HasMaxLength(64);

        b.HasIndex(e => e.SequenceNumber).IsUnique();
        b.HasIndex(e => e.Hash).IsUnique();
        b.HasIndex(e => e.ValueDate);
        b.HasIndex(e => e.ReversalOfEntryId);
        b.HasIndex(e => e.CorrelationId);

        var postings = b.Metadata.FindNavigation(nameof(JournalEntry.Postings))!;
        postings.SetPropertyAccessMode(PropertyAccessMode.Field);
        b.HasMany(e => e.Postings)
            .WithOne()
            .HasForeignKey(p => p.JournalEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Ignore(e => e.IsSealed);
    }
}

public sealed class PostingConfiguration : IEntityTypeConfiguration<Posting>
{
    public void Configure(EntityTypeBuilder<Posting> b)
    {
        b.ToTable("postings");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.Direction).HasConversion<string>().HasMaxLength(8);
        b.Property(p => p.Currency).IsRequired().HasMaxLength(3);

        b.HasIndex(p => p.JournalEntryId);
        b.HasIndex(p => p.AccountId);
        b.HasIndex(p => new { p.AccountId, p.Currency });

        b.HasOne<Account>()
            .WithMany()
            .HasForeignKey(p => p.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Ignore(p => p.Amount);
    }
}

public sealed class HoldConfiguration : IEntityTypeConfiguration<Hold>
{
    public void Configure(EntityTypeBuilder<Hold> b)
    {
        b.ToTable("holds");
        b.HasKey(h => h.Id);
        b.Property(h => h.Id).ValueGeneratedNever();
        b.Property(h => h.Currency).IsRequired().HasMaxLength(3);
        b.Property(h => h.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(h => h.Reference).HasMaxLength(200);
        b.Property(h => h.IdempotencyKey).HasMaxLength(200);
        b.Property(h => h.Version).IsConcurrencyToken();

        b.HasIndex(h => h.AccountId);
        b.HasIndex(h => new { h.Status, h.ExpiresAt });

        b.HasOne<Account>()
            .WithMany()
            .HasForeignKey(h => h.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Ignore(h => h.RemainingMinor);
        b.Ignore(h => h.IsActive);
        b.Ignore(h => h.Amount);
    }
}

public sealed class FeeScheduleConfiguration : IEntityTypeConfiguration<FeeSchedule>
{
    public void Configure(EntityTypeBuilder<FeeSchedule> b)
    {
        b.ToTable("fee_schedules");
        b.HasKey(f => f.Id);
        b.Property(f => f.Id).ValueGeneratedNever();
        b.Property(f => f.Code).IsRequired().HasMaxLength(64);
        b.Property(f => f.Name).IsRequired().HasMaxLength(200);
        b.Property(f => f.Type).HasConversion<string>().HasMaxLength(16);
        b.Property(f => f.Currency).IsRequired().HasMaxLength(3);

        b.HasIndex(f => f.Code).IsUnique();

        var tiers = b.Metadata.FindNavigation(nameof(FeeSchedule.Tiers))!;
        tiers.SetPropertyAccessMode(PropertyAccessMode.Field);
        b.HasMany(f => f.Tiers)
            .WithOne()
            .HasForeignKey(t => t.FeeScheduleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FeeTierConfiguration : IEntityTypeConfiguration<FeeTier>
{
    public void Configure(EntityTypeBuilder<FeeTier> b)
    {
        b.ToTable("fee_tiers");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.HasIndex(t => new { t.FeeScheduleId, t.Ordinal }).IsUnique();
    }
}

public sealed class InterestAccrualConfiguration : IEntityTypeConfiguration<InterestAccrual>
{
    public void Configure(EntityTypeBuilder<InterestAccrual> b)
    {
        b.ToTable("interest_accruals");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.Currency).IsRequired().HasMaxLength(3);
        b.Property(a => a.Convention).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.Version).IsConcurrencyToken();

        b.HasIndex(a => a.AccountId).IsUnique();

        b.Ignore(a => a.Accrued);
    }
}

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency_records");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.Key).IsRequired().HasMaxLength(200);
        b.Property(r => r.Scope).IsRequired().HasMaxLength(64);
        b.Property(r => r.RequestHash).HasMaxLength(64);
        b.Property(r => r.ResponseJson).IsRequired();

        b.HasIndex(r => r.Key).IsUnique();
    }
}
