using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence.Configurations;

public sealed class MatchConfiguration : IEntityTypeConfiguration<Match>
{
    public void Configure(EntityTypeBuilder<Match> b)
    {
        b.ToTable("matches");
        b.HasKey(x => x.Id);
        b.Property(x => x.RuleId).HasMaxLength(100).IsRequired();
        b.Property(x => x.RuleSetVersionTag).HasMaxLength(150);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Explanation).HasMaxLength(2000);
        b.Property(x => x.Confidence).HasColumnType("decimal(5,4)");

        b.HasIndex(x => x.RunId);

        b.HasMany(x => x.Entries)
            .WithOne()
            .HasForeignKey(e => e.MatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MatchEntryConfiguration : IEntityTypeConfiguration<MatchEntry>
{
    public void Configure(EntityTypeBuilder<MatchEntry> b)
    {
        b.ToTable("match_entries");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.MatchId);
        b.HasIndex(x => x.RecordId);
    }
}
