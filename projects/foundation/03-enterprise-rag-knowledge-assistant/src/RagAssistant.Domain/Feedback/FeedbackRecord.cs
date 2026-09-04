namespace RagAssistant.Domain.Feedback;

public sealed class FeedbackRecord
{
    private FeedbackRecord()
    {
        Id = Guid.Empty;
        Query = string.Empty;
        Answer = string.Empty;
        UserId = string.Empty;
        PromptVersion = string.Empty;
        CitedChunkIdsCsv = string.Empty;
        Reason = string.Empty;
    }

    public FeedbackRecord(
        Guid id,
        string userId,
        string query,
        string answer,
        FeedbackRating rating,
        string reason,
        string promptVersion,
        IEnumerable<Guid> citedChunkIds,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Feedback id must be provided.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User id must be provided.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query must be provided.", nameof(query));
        }

        Id = id;
        UserId = userId.Trim();
        Query = query.Trim();
        Answer = answer ?? string.Empty;
        Rating = rating;
        Reason = reason ?? string.Empty;
        PromptVersion = promptVersion ?? string.Empty;
        CitedChunkIdsCsv = string.Join(',', (citedChunkIds ?? []).Select(c => c.ToString("D")));
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string UserId { get; private set; }
    public string Query { get; private set; }
    public string Answer { get; private set; }
    public FeedbackRating Rating { get; private set; }
    public string Reason { get; private set; }
    public string PromptVersion { get; private set; }
    public string CitedChunkIdsCsv { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<Guid> DecodeChunkIds()
    {
        if (string.IsNullOrWhiteSpace(CitedChunkIdsCsv))
        {
            return Array.Empty<Guid>();
        }

        return CitedChunkIdsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Guid.Parse)
            .ToArray();
    }
}
