using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Infrastructure.Persistence.Configurations;

public sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.ToTable("document_chunks");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.DocumentId).IsRequired();
        builder.Property(c => c.Sequence).IsRequired();
        builder.Property(c => c.StartChar).IsRequired();
        builder.Property(c => c.EndChar).IsRequired();
        builder.Property(c => c.Content).IsRequired();
        builder.Property(c => c.Embedding).IsRequired();
        builder.Property(c => c.EmbeddingModelId).IsRequired().HasMaxLength(128);
        builder.Property(c => c.EmbeddingDimensions).IsRequired();
        builder.HasIndex(c => new { c.DocumentId, c.Sequence }).IsUnique();
    }
}
