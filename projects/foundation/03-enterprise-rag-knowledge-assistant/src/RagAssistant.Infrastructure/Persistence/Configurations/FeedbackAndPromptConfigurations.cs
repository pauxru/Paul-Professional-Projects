using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Domain.Feedback;
using RagAssistant.Domain.Prompts;

namespace RagAssistant.Infrastructure.Persistence.Configurations;

public sealed class FeedbackConfiguration : IEntityTypeConfiguration<FeedbackRecord>
{
    public void Configure(EntityTypeBuilder<FeedbackRecord> builder)
    {
        builder.ToTable("feedback");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.UserId).IsRequired().HasMaxLength(128);
        builder.Property(f => f.Query).IsRequired().HasMaxLength(1000);
        builder.Property(f => f.Answer).IsRequired();
        builder.Property(f => f.Reason).HasMaxLength(1000);
        builder.Property(f => f.PromptVersion).HasMaxLength(200);
        builder.Property(f => f.Rating).HasConversion<int>();
        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.CitedChunkIdsCsv).HasMaxLength(4000);
        builder.HasIndex(f => f.CreatedAt);
    }
}

public sealed class PromptTemplateConfiguration : IEntityTypeConfiguration<PromptTemplate>
{
    public void Configure(EntityTypeBuilder<PromptTemplate> builder)
    {
        builder.ToTable("prompts");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Version).IsRequired().HasMaxLength(50);
        builder.Property(p => p.Body).IsRequired();
        builder.Property(p => p.Hash).IsRequired().HasMaxLength(128);
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.HasIndex(p => new { p.Name, p.Version }).IsUnique();
        builder.HasIndex(p => new { p.Name, p.IsActive });
    }
}
