using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Domain.Customer;
using ZeroTrust.Domain.Partner;

namespace ZeroTrust.Infrastructure.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("accounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.AccountNumber).HasMaxLength(32).IsRequired();
        b.HasIndex(x => x.AccountNumber).IsUnique();
        b.Property(x => x.OwnerSubject).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.OwnerSubject);
        b.Property(x => x.Nickname).HasMaxLength(128).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.BalanceMinorUnits).HasPrecision(18, 0);
    }
}

public sealed class StatementConfiguration : IEntityTypeConfiguration<Statement>
{
    public void Configure(EntityTypeBuilder<Statement> b)
    {
        b.ToTable("statements");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.AccountId, x.Year, x.Month }).IsUnique();
        b.Property(x => x.OwnerSubject).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.OwnerSubject);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.OpeningBalanceMinorUnits).HasPrecision(18, 0);
        b.Property(x => x.ClosingBalanceMinorUnits).HasPrecision(18, 0);
    }
}

public sealed class PaymentInitiationConfiguration : IEntityTypeConfiguration<PaymentInitiationRequest>
{
    public void Configure(EntityTypeBuilder<PaymentInitiationRequest> b)
    {
        b.ToTable("payment_initiations");
        b.HasKey(x => x.Id);
        b.Property(x => x.ExternalReference).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.PartnerCode, x.ExternalReference }).IsUnique();
        b.Property(x => x.PartnerCode).HasMaxLength(64).IsRequired();
        b.Property(x => x.DebtorAccountNumber).HasMaxLength(32).IsRequired();
        b.Property(x => x.CreditorAccountNumber).HasMaxLength(32).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Status).HasMaxLength(32).IsRequired();
        b.Property(x => x.SchemeSignatureVersion).HasMaxLength(8).IsRequired();
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.AmountMinorUnits).HasPrecision(18, 0);
    }
}

public sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> b)
    {
        b.ToTable("audit_records");
        b.HasKey(x => x.Id);
        b.Property(x => x.Sequence).ValueGeneratedNever();
        b.HasIndex(x => x.Sequence).IsUnique();
        b.Property(x => x.Actor).HasMaxLength(128).IsRequired();
        b.Property(x => x.Action).HasMaxLength(128).IsRequired();
        b.Property(x => x.Resource).HasMaxLength(256).IsRequired();
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.SourceIp).HasMaxLength(64).IsRequired();
        b.Property(x => x.UserAgent).HasMaxLength(256).IsRequired();
        b.Property(x => x.Detail).HasMaxLength(2048).IsRequired();
        b.Property(x => x.PreviousHash).HasMaxLength(128).IsRequired();
        b.Property(x => x.Hash).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.CorrelationId);
        b.HasIndex(x => x.CreatedAtUtc);
    }
}
