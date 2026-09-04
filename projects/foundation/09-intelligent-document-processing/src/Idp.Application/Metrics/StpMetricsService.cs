using Idp.Application.Documents;
using Idp.Application.Review;
using Idp.Domain.Documents;

namespace Idp.Application.Metrics;

public sealed class StpMetricsService : IStpMetricsService
{
    private readonly IDocumentRepository _documents;
    private readonly IReviewRepository _reviews;

    public StpMetricsService(IDocumentRepository documents, IReviewRepository reviews)
    {
        _documents = documents;
        _reviews = reviews;
    }

    public async Task<StpSnapshot> ComputeAsync(CancellationToken ct = default)
    {
        var docs = await _documents.GetAllAsync(ct);
        var queueDepth = await _reviews.CountOpenAsync(ct);

        var autoApproved = docs.Count(d =>
            d.Routing == RoutingDecision.AutoApprove);
        var inReview = docs.Count(d => d.Routing == RoutingDecision.Review);
        var rejected = docs.Count(d => d.Routing == RoutingDecision.Reject
            || d.State == PipelineState.Rejected);
        var exported = docs.Count(d => d.State == PipelineState.Exported);
        var failed = docs.Count(d => d.State == PipelineState.Failed);
        var processed = docs.Count(d => d.Routing is not null);

        return new StpSnapshot(
            TotalDocuments: docs.Count,
            Processed: processed,
            AutoApproved: autoApproved,
            InReview: inReview,
            Rejected: rejected,
            Exported: exported,
            Failed: failed,
            ReviewQueueDepth: queueDepth);
    }
}
