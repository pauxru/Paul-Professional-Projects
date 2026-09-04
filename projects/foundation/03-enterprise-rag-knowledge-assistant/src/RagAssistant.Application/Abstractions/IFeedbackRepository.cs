using RagAssistant.Domain.Feedback;

namespace RagAssistant.Application.Abstractions;

public interface IFeedbackRepository
{
    Task AddAsync(FeedbackRecord record, CancellationToken ct);
    Task<IReadOnlyList<FeedbackRecord>> ListRecentAsync(int limit, CancellationToken ct);
    Task<int> CountByRatingAsync(FeedbackRating rating, CancellationToken ct);
}
