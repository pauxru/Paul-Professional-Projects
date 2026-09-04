using Healthcare.Domain.Audit;
using Healthcare.Domain.Referrals;
using Healthcare.Domain.Reminders;
using Healthcare.Domain.Waitlist;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Healthcare.Infrastructure.Persistence.Configurations;

public sealed class ReferralConfiguration : IEntityTypeConfiguration<Referral>
{
    public void Configure(EntityTypeBuilder<Referral> b)
    {
        b.ToTable("referrals");
        b.HasKey(r => r.Id);
        b.Property(r => r.Speciality).HasMaxLength(128);
        b.Property(r => r.ReasonForReferral).HasMaxLength(2048);
        b.Property(r => r.ExternalDestination).HasMaxLength(256);
        b.Property(r => r.RejectionReason).HasMaxLength(1024);
        b.HasIndex(r => new { r.PatientId, r.CreatedAtUtc });
        b.HasIndex(r => r.Status);
        b.HasIndex(r => r.SlaBreached);
        b.Ignore(r => r.DomainEvents);
    }
}

public sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> b)
    {
        b.ToTable("waitlist_entries");
        b.HasKey(w => w.Id);
        b.HasIndex(w => new { w.FacilityId, w.AppointmentTypeId, w.Status, w.Priority });
        b.Ignore(w => w.DomainEvents);
    }
}

public sealed class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> b)
    {
        b.ToTable("reminders");
        b.HasKey(r => r.Id);
        b.Property(r => r.IdempotencyKey).HasMaxLength(96).IsRequired();
        b.HasIndex(r => r.IdempotencyKey).IsUnique();
        b.HasIndex(r => new { r.Status, r.ScheduledSendAtUtc });
        b.Property(r => r.LastError).HasMaxLength(1024);
        b.Ignore(r => r.DomainEvents);
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.HasKey(a => a.Id);
        b.Property(a => a.ActorId).HasMaxLength(128).IsRequired();
        b.Property(a => a.ActorRole).HasMaxLength(64).IsRequired();
        b.Property(a => a.PatientId).HasMaxLength(32);
        b.Property(a => a.Resource).HasMaxLength(256);
        b.Property(a => a.Action).HasMaxLength(64);
        b.Property(a => a.Purpose).HasMaxLength(128);
        b.Property(a => a.CorrelationId).HasMaxLength(96);
        b.Property(a => a.SourceIp).HasMaxLength(64);
        b.Property(a => a.UserAgent).HasMaxLength(512);
        b.Property(a => a.Justification).HasMaxLength(1024);
        b.HasIndex(a => a.OccurredAtUtc);
        b.HasIndex(a => a.ActorId);
        b.HasIndex(a => a.PatientId);
        b.HasIndex(a => a.Kind);
        b.Ignore(a => a.DomainEvents);
    }
}
