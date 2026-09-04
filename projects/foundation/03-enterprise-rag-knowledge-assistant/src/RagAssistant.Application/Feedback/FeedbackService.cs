using RagAssistant.Application.Abstractions;
using RagAssistant.Domain.Common;
using RagAssistant.Domain.Feedback;

namespace RagAssistant.Application.Feedback;

public sealed record FeedbackSubmission(
    string UserId,
    string Query,
    string Answer,
    FeedbackRating Rating,
    string Reason,
    string PromptVersion,
    IReadOnlyList<Guid> CitedChunkIds);

public sealed class FeedbackService
{
    private readonly IFeedbackRepository _repository;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public FeedbackService(IFeedbackRepository repository, IClock clock, IIdGenerator ids)
    {
        _repository = repository;
        _clock = clock;
        _ids = ids;
    }

    public async Task<FeedbackRecord> SubmitAsync(FeedbackSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var record = new FeedbackRecord(
            _ids.NewId(),
            submission.UserId,
            submission.Query,
            submission.Answer,
            submission.Rating,
            submission.Reason ?? string.Empty,
            submission.PromptVersion ?? string.Empty,
            submission.CitedChunkIds ?? [],
            _clock.UtcNow);

        await _repository.AddAsync(record, ct).ConfigureAwait(false);
        return record;
    }

    public Task<IReadOnlyList<FeedbackRecord>> ListRecentAsync(int limit, CancellationToken ct)
        => _repository.ListRecentAsync(limit, ct);
}
