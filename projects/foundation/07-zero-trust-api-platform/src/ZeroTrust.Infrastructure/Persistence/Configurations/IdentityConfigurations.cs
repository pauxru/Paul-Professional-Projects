using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZeroTrust.Domain.Identity;

namespace ZeroTrust.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Subject).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Subject).IsUnique();
        b.Property(x => x.Email).HasMaxLength(256).IsRequired();
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        b.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
        b.Property(x => x.PasswordSalt).HasMaxLength(64).IsRequired();
        b.Property(x => x.Roles).HasMaxLength(256).IsRequired();
    }
}

public sealed class PartnerConfiguration : IEntityTypeConfiguration<Partner>
{
    public void Configure(EntityTypeBuilder<Partner> b)
    {
        b.ToTable("partners");
        b.HasKey(x => x.Id);
        b.Property(x => x.PartnerCode).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.PartnerCode).IsUnique();
        b.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        b.Property(x => x.ClientId).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.ClientId).IsUnique();
        b.Property(x => x.ClientSecretHash).HasMaxLength(256).IsRequired();
        b.Property(x => x.ClientSecretSalt).HasMaxLength(64).IsRequired();
        b.Property(x => x.AllowedScopes).HasMaxLength(512).IsRequired();
        b.Property(x => x.AllowedIps).HasMaxLength(512).IsRequired();
        b.Property(x => x.ClientCertThumbprint).HasMaxLength(128).IsRequired();
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.Subject).HasMaxLength(128).IsRequired();
        b.Property(x => x.Audience).HasMaxLength(128).IsRequired();
        b.Property(x => x.Scopes).HasMaxLength(512).IsRequired();
        b.HasIndex(x => x.FamilyId);
    }
}

public sealed class RevokedTokenConfiguration : IEntityTypeConfiguration<RevokedToken>
{
    public void Configure(EntityTypeBuilder<RevokedToken> b)
    {
        b.ToTable("revoked_tokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.Jti).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Jti).IsUnique();
        b.Property(x => x.Subject).HasMaxLength(128).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(256).IsRequired();
    }
}

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("api_keys");
        b.HasKey(x => x.Id);
        b.Property(x => x.KeyId).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.KeyId).IsUnique();
        b.Property(x => x.KeyHash).HasMaxLength(256).IsRequired();
        b.Property(x => x.KeySalt).HasMaxLength(64).IsRequired();
        b.Property(x => x.OwnerPartnerCode).HasMaxLength(64).IsRequired();
        b.Property(x => x.AllowedScopes).HasMaxLength(512).IsRequired();
    }
}

public sealed class BreakGlassConfiguration : IEntityTypeConfiguration<BreakGlassGrant>
{
    public void Configure(EntityTypeBuilder<BreakGlassGrant> b)
    {
        b.ToTable("break_glass_grants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Subject).HasMaxLength(128).IsRequired();
        b.Property(x => x.Requestor).HasMaxLength(128).IsRequired();
        b.Property(x => x.Approver).HasMaxLength(128).IsRequired();
        b.Property(x => x.Justification).HasMaxLength(1024).IsRequired();
    }
}

public sealed class SigningKeyConfiguration : IEntityTypeConfiguration<SigningKey>
{
    public void Configure(EntityTypeBuilder<SigningKey> b)
    {
        b.ToTable("signing_keys");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kid).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Kid).IsUnique();
        b.Property(x => x.Algorithm).HasMaxLength(16).IsRequired();
        b.Property(x => x.PublicKeyPem).IsRequired();
        b.Property(x => x.PrivateKeyPem).IsRequired();
    }
}
