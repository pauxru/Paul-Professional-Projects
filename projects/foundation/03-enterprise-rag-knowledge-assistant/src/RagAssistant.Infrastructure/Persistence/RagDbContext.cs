using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RagAssistant.Application.Abstractions;
using RagAssistant.Domain.Chat;
using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Feedback;
using RagAssistant.Domain.Prompts;
using RagAssistant.Infrastructure.Persistence.Configurations;

namespace RagAssistant.Infrastructure.Persistence;

public sealed class RagDbContext : DbContext
{
    public RagDbContext(DbContextOptions<RagDbContext> options) : base(options)
    {
    }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> Chunks => Set<DocumentChunk>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<FeedbackRecord> Feedback => Set<FeedbackRecord>();
    public DbSet<PromptTemplate> Prompts => Set<PromptTemplate>();
    public DbSet<UsageEntryRecord> Usage => Set<UsageEntryRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new DocumentConfiguration());
        modelBuilder.ApplyConfiguration(new DocumentChunkConfiguration());
        modelBuilder.ApplyConfiguration(new ChatSessionConfiguration());
        modelBuilder.ApplyConfiguration(new ChatMessageConfiguration());
        modelBuilder.ApplyConfiguration(new FeedbackConfiguration());
        modelBuilder.ApplyConfiguration(new PromptTemplateConfiguration());
        modelBuilder.ApplyConfiguration(new UsageEntryConfiguration());
    }
}

public sealed class UsageEntryRecord
{
    public long Id { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public decimal Cost { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string PromptVersion { get; set; } = string.Empty;
}
