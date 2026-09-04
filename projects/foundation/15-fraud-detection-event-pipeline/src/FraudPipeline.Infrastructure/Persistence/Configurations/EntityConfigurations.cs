using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FraudPipeline.Infrastructure.Persistence.Configurations;

public sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> b)
    {
        b.ToTable("transactions");
        b.HasKey(t => t.Id);
        b.Property(t => t.TransactionRef).HasMaxLength(64).IsRequired();
        b.HasIndex(t => t.TransactionRef).IsUnique();
        b.Property(t => t.CardId).HasMaxLength(64).IsRequired();
        b.Property(t => t.CustomerId).HasMaxLength(64).IsRequired();
        b.Property(t => t.DeviceId).HasMaxLength(64).IsRequired();
        b.Property(t => t.IpAddress).HasMaxLength(64).IsRequired();
        b.Property(t => t.MerchantId).HasMaxLength(64).IsRequired();
        b.Property(t => t.MerchantCategoryCode).HasMaxLength(4).IsRequired();
        b.Property(t => t.Type).HasConversion<int>();
        b.Property(t => t.Outcome).HasConversion<int>();
        b.Property(t => t.OccurredAt);
        b.Property(t => t.ReceivedAt);
        b.Property(t => t.GroundTruthFraud);
        b.Property(t => t.GroundTruthPattern).HasMaxLength(64);
        b.HasIndex(t => t.CardId);
        b.HasIndex(t => t.CustomerId);
        b.HasIndex(t => t.OccurredAt);
        b.OwnsOne(t => t.Amount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("amount").HasColumnType("TEXT");
            m.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(4);
        });
        b.OwnsOne(t => t.Location, l =>
        {
            l.Property(x => x.LatitudeDeg).HasColumnName("latitude");
            l.Property(x => x.LongitudeDeg).HasColumnName("longitude");
            l.Property(x => x.CountryIso2).HasColumnName("country_iso2").HasMaxLength(2);
        });
    }
}

public sealed class ScoringDecisionConfiguration : IEntityTypeConfiguration<ScoringDecision>
{
    public void Configure(EntityTypeBuilder<ScoringDecision> b)
    {
        b.ToTable("scoring_decisions");
        b.HasKey(d => d.Id);
        b.Property(d => d.TransactionRef).HasMaxLength(64).IsRequired();
        b.Property(d => d.RulesetVersion).HasMaxLength(64).IsRequired();
        b.Property(d => d.Reasons).HasMaxLength(4000);
        b.Property(d => d.RulesFiredJson);
        b.Property(d => d.FeatureVectorJson);
        b.Property(d => d.Decision).HasConversion<int>();
        b.HasIndex(d => d.TransactionRef);
        b.HasIndex(d => new { d.Shadow, d.DecidedAt });
    }
}

public sealed class RulesetConfiguration : IEntityTypeConfiguration<Ruleset>
{
    public void Configure(EntityTypeBuilder<Ruleset> b)
    {
        b.ToTable("rulesets");
        b.HasKey(r => r.Id);
        b.Property(r => r.Version).HasMaxLength(64).IsRequired();
        b.HasIndex(r => r.Version).IsUnique();
        b.Property(r => r.Name).HasMaxLength(200).IsRequired();
        b.Property(r => r.DefinitionJson).IsRequired();
    }
}

public sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> b)
    {
        b.ToTable("alerts");
        b.HasKey(a => a.Id);
        b.Property(a => a.PrimaryEntityKey).HasMaxLength(200).IsRequired();
        b.Property(a => a.ReasonSummary).HasMaxLength(2000);
        b.Property(a => a.Status).HasConversion<int>();
        b.HasIndex(a => a.PrimaryEntityKey);
        b.HasIndex(a => a.CaseId);
    }
}

public sealed class CaseConfiguration : IEntityTypeConfiguration<Case>
{
    public void Configure(EntityTypeBuilder<Case> b)
    {
        b.ToTable("cases");
        b.HasKey(c => c.Id);
        b.Property(c => c.PrimaryEntityKey).HasMaxLength(200).IsRequired();
        b.Property(c => c.ExposureCurrency).HasMaxLength(4).IsRequired();
        b.Property(c => c.ExposureAmount).HasColumnType("TEXT");
        b.Property(c => c.Status).HasConversion<int>();
        b.Property(c => c.Disposition).HasConversion<int>();
        b.Property(c => c.AssignedTo).HasMaxLength(120);
        b.Property(c => c.DispositionReason).HasMaxLength(2000);
        b.Property(c => c.DispositionBy).HasMaxLength(120);
        b.Property(c => c.ApprovedBy).HasMaxLength(120);
        b.HasIndex(c => c.PrimaryEntityKey);
        b.Ignore(c => c.Notes);
        b.Ignore(c => c.AlertIds);
    }
}

public sealed class CaseNoteConfiguration : IEntityTypeConfiguration<CaseNote>
{
    public void Configure(EntityTypeBuilder<CaseNote> b)
    {
        b.ToTable("case_notes");
        b.HasKey(n => n.Id);
        b.Property(n => n.Author).HasMaxLength(120).IsRequired();
        b.Property(n => n.Text).HasMaxLength(4000);
        b.HasIndex(n => n.CaseId);
    }
}

public sealed class ListEntryConfiguration : IEntityTypeConfiguration<ListEntry>
{
    public void Configure(EntityTypeBuilder<ListEntry> b)
    {
        b.ToTable("list_entries");
        b.HasKey(l => l.Id);
        b.Property(l => l.Value).HasMaxLength(128).IsRequired();
        b.Property(l => l.Reason).HasMaxLength(2000);
        b.Property(l => l.Type).HasConversion<int>();
        b.Property(l => l.Subject).HasConversion<int>();
        b.HasIndex(l => new { l.Type, l.Subject, l.Value }).IsUnique();
    }
}

public sealed class DeadLetterConfiguration : IEntityTypeConfiguration<DeadLetterEvent>
{
    public void Configure(EntityTypeBuilder<DeadLetterEvent> b)
    {
        b.ToTable("dead_letter_events");
        b.HasKey(d => d.Id);
        b.Property(d => d.Source).HasMaxLength(200);
        b.Property(d => d.Reason).HasMaxLength(2000);
        b.Property(d => d.ExceptionType).HasMaxLength(200);
    }
}
