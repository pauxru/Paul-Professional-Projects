using Idp.Domain.Review;

namespace Idp.Application.Review;

public interface IReviewRepository
{
    void Add(ReviewTask task);
    void AddCorrection(Correction correction);
    Task<ReviewTask?> GetAsync(Guid id, CancellationToken ct = default);
    Task<ReviewTask?> GetByDocumentAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>Open review tasks ordered by priority then age (highest priority / oldest first).</summary>
    Task<IReadOnlyList<ReviewTask>> GetQueueAsync(int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Correction>> GetCorrectionsAsync(
        Guid documentId, CancellationToken ct = default);
    Task<int> CountOpenAsync(CancellationToken ct = default);
}
