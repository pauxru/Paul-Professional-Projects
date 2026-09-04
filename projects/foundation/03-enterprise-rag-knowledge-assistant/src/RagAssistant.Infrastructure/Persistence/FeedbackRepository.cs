using Microsoft.EntityFrameworkCore;
using RagAssistant.Application.Abstractions;
using RagAssistant.Domain.Feedback;

namespace RagAssistant.Infrastructure.Persistence;

public sealed class FeedbackRepository : IFeedbackRepository
{
    private readonly RagDbContext _db;

    public FeedbackRepository(RagDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(FeedbackRecord record, CancellationToken ct)
    {
        await _db.Feedback.AddAsync(record, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FeedbackRecord>> ListRecentAsync(int limit, CancellationToken ct)
    {
        return await _db.Feedback
            .OrderByDescending(f => f.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> CountByRatingAsync(FeedbackRating rating, CancellationToken ct)
    {
        return await _db.Feedback.CountAsync(f => f.Rating == rating, ct).ConfigureAwait(false);
    }
}
