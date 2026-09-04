using RagAssistant.Domain.Chat;

namespace RagAssistant.Application.Abstractions;

public interface IChatSessionRepository
{
    Task<ChatSession?> GetByIdAsync(Guid id, string userId, CancellationToken ct);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyList<ChatSession>> ListForUserAsync(string userId, CancellationToken ct);
    Task AddAsync(ChatSession session, CancellationToken ct);
    Task AppendMessagesAsync(Guid sessionId, IEnumerable<ChatMessage> messages, DateTimeOffset updatedAt, CancellationToken ct);
}
