namespace RagAssistant.Domain.Chat;

public sealed class ChatSession
{
    private readonly List<ChatMessage> _messages = [];

    private ChatSession()
    {
        Id = Guid.Empty;
        UserId = string.Empty;
        Title = string.Empty;
    }

    public ChatSession(Guid id, string userId, string title, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Session id must be provided.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User id must be provided.", nameof(userId));
        }

        Id = id;
        UserId = userId.Trim();
        Title = string.IsNullOrWhiteSpace(title) ? "New session" : title.Trim();
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string UserId { get; private set; }
    public string Title { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<ChatMessage> Messages => _messages;

    public ChatMessage AppendMessage(Guid messageId, ChatMessageRole role, string content, DateTimeOffset timestamp, string? promptVersion = null)
    {
        var message = new ChatMessage(messageId, Id, role, content, timestamp, promptVersion);
        _messages.Add(message);
        UpdatedAt = timestamp;
        return message;
    }
}
