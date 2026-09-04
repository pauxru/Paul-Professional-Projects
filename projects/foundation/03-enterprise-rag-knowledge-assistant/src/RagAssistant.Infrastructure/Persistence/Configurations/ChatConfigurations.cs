using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Domain.Chat;

namespace RagAssistant.Infrastructure.Persistence.Configurations;

public sealed class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> builder)
    {
        builder.ToTable("chat_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.UserId).IsRequired().HasMaxLength(128);
        builder.Property(s => s.Title).IsRequired().HasMaxLength(200);
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();
        builder.HasIndex(s => s.UserId);

        builder.HasMany(s => s.Messages)
            .WithOne()
            .HasForeignKey(m => m.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        var nav = builder.Metadata.FindNavigation(nameof(ChatSession.Messages))
            ?? throw new InvalidOperationException("Messages nav missing");
        nav.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("chat_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Content).IsRequired();
        builder.Property(m => m.Role).HasConversion<int>().IsRequired();
        builder.Property(m => m.Timestamp).IsRequired();
        builder.Property(m => m.PromptVersion).HasMaxLength(200);
        builder.HasIndex(m => new { m.SessionId, m.Timestamp });
    }
}
