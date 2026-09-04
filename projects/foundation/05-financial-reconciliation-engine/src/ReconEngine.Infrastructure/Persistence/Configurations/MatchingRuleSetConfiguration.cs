using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence.Configurations;

public sealed class MatchingRuleSetConfiguration : IEntityTypeConfiguration<MatchingRuleSet>
{
    public void Configure(EntityTypeBuilder<MatchingRuleSet> b)
    {
        b.ToTable("rule_sets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.DefinitionJson).IsRequired();
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.Ignore(x => x.VersionTag);

        // A name may have many versions, but a given (name, version) is unique.
        b.HasIndex(x => new { x.Name, x.Version }).IsUnique();
        b.HasIndex(x => x.IsActive);
    }
}
