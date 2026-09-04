namespace RagAssistant.Domain.Chat;

public sealed class ChatMessage
{
    private ChatMessage()
    {
        Id = Guid.Empty;
        SessionId = Guid.Empty;
        Content = string.Empty;
    }

    public ChatMessage(Guid id, Guid sessionId, ChatMessageRole role, string content, DateTimeOffset timestamp, string? promptVersion)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Message id must be provided.", nameof(id));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must be provided.", nameof(sessionId));
        }

        Id = id;
        SessionId = sessionId;
        Role = role;
        Content = content ?? string.Empty;
        Timestamp = timestamp;
        PromptVersion = promptVersion;
    }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public ChatMessageRole Role { get; private set; }
    public string Content { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public string? PromptVersion { get; private set; }
}
