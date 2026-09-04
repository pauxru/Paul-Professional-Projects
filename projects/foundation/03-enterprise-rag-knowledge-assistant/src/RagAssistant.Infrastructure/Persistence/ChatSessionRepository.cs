using Microsoft.EntityFrameworkCore;
using RagAssistant.Application.Abstractions;
using RagAssistant.Domain.Chat;

namespace RagAssistant.Infrastructure.Persistence;

public sealed class ChatSessionRepository : IChatSessionRepository
{
    private readonly RagDbContext _db;

    public ChatSessionRepository(RagDbContext db)
    {
        _db = db;
    }

    public async Task<ChatSession?> GetByIdAsync(Guid id, string userId, CancellationToken ct)
    {
        return await _db.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, CancellationToken ct)
    {
        return await _db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Timestamp)
            .AsNoTracking()
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatSession>> ListForUserAsync(string userId, CancellationToken ct)
    {
        return await _db.ChatSessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.UpdatedAt)
            .AsNoTracking()
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(ChatSession session, CancellationToken ct)
    {
        await _db.ChatSessions.AddAsync(session, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AppendMessagesAsync(Guid sessionId, IEnumerable<ChatMessage> messages, DateTimeOffset updatedAt, CancellationToken ct)
    {
        var toInsert = messages.ToArray();
        if (toInsert.Length == 0)
        {
            return;
        }

        await _db.ChatMessages.AddRangeAsync(toInsert, ct).ConfigureAwait(false);
        await _db.ChatSessions
            .Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.UpdatedAt, updatedAt), ct)
            .ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
