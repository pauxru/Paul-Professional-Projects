using Idp.Application.Abstractions;
using Idp.Application.Configuration;
using Idp.Application.Documents;
using Idp.Application.Exporting;
using Idp.Application.Extraction;
using Idp.Application.Suppliers;
using Idp.Domain.Audit;
using Idp.Domain.Documents;
using Idp.Domain.Review;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Idp.Application.Review;

/// <summary>
/// Human-review use cases: prioritised queue, claim/lock with expiry, side-by-side field correction
/// (which captures the correction and feeds the per-supplier learning loop), approve (export) and
/// reject. All mutations run inside a single unit of work.
/// </summary>
public sealed class ReviewService
{
    private readonly IReviewRepository _reviews;
    private readonly IDocumentRepository _documents;
    private readonly ISupplierRepository _suppliers;
    private readonly IObjectStore _objectStore;
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly ExportService _export;
    private readonly IAuditRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ReviewOptions _options;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(
        IReviewRepository reviews,
        IDocumentRepository documents,
        ISupplierRepository suppliers,
        IObjectStore objectStore,
        IEnumerable<IDocumentParser> parsers,
        ExportService export,
        IAuditRepository audit,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOptions<ReviewOptions> options,
        ILogger<ReviewService> logger)
    {
        _reviews = reviews;
        _documents = documents;
        _suppliers = suppliers;
        _objectStore = objectStore;
        _parsers = parsers;
        _export = export;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ReviewQueueItemDto>> GetQueueAsync(
        int limit, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var tasks = await _reviews.GetQueueAsync(limit, ct);
        var items = new List<ReviewQueueItemDto>(tasks.Count);
        foreach (var t in tasks)
        {
            var doc = await _documents.GetAsync(t.DocumentId, ct);
            items.Add(new ReviewQueueItemDto(
                t.Id, t.DocumentId, doc?.FileName ?? "(unknown)",
                doc?.DocumentType ?? DocumentType.Unknown, t.Priority, t.DocumentValue,
                t.DocumentConfidence, t.Status, t.ClaimedBy, t.ClaimExpiresUtc, t.SlaDueUtc,
                t.IsOverdue(now), (int)t.Age(now).TotalMinutes));
        }
        return items;
    }

    public async Task<ReviewActionResult> ClaimAsync(
        Guid taskId, string reviewer, CancellationToken ct = default)
    {
        var task = await _reviews.GetAsync(taskId, ct);
        if (task is null) return ReviewActionResult.NotFound($"Review task {taskId} not found.");

        try
        {
            task.Claim(reviewer, _clock.UtcNow, TimeSpan.FromMinutes(_options.ClaimLeaseMinutes));
        }
        catch (ReviewClaimConflictException ex)
        {
            return ReviewActionResult.Conflict(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ReviewActionResult.Invalid(ex.Message);
        }

        _audit.Add(new AuditEntry(reviewer, "review.claimed", $"review:{taskId}",
            task.DocumentId.ToString(), _clock.UtcNow));
        await _unitOfWork.SaveChangesAsync(ct);
        return ReviewActionResult.Ok();
    }

    public async Task<ReviewActionResult> CorrectAsync(
        Guid taskId, IReadOnlyList<FieldCorrectionInput> corrections, string reviewer,
        CancellationToken ct = default)
    {
        var task = await _reviews.GetAsync(taskId, ct);
        if (task is null) return ReviewActionResult.NotFound($"Review task {taskId} not found.");
        if (task.IsClaimActive(_clock.UtcNow) &&
            !string.Equals(task.ClaimedBy, reviewer, StringComparison.OrdinalIgnoreCase))
            return ReviewActionResult.Conflict($"Task is claimed by '{task.ClaimedBy}'.");

        var document = await _documents.GetAsync(task.DocumentId, ct);
        if (document is null) return ReviewActionResult.NotFound("Document not found.");

        var content = await TryLoadContentAsync(document, ct);
        var now = _clock.UtcNow;

        foreach (var correction in corrections)
        {
            var existing = document.Field(correction.FieldKey);
            var oldValue = existing?.NormalizedValue ?? existing?.RawValue;
            _reviews.AddCorrection(new Correction(
                document.Id, correction.FieldKey, oldValue, correction.NewValue,
                correction.Reason, reviewer, now));
            document.ApplyFieldCorrection(
                correction.FieldKey, correction.NewValue, correction.NewValue, reviewer, now);

            await LearnAnchorAsync(document, content, correction, now, ct);
        }

        _audit.Add(new AuditEntry(reviewer, "review.corrected", $"document:{document.Id}",
            document.CorrelationId, now, detail: string.Join(",", corrections.Select(c => c.FieldKey))));
        await _unitOfWork.SaveChangesAsync(ct);
        return ReviewActionResult.Ok();
    }

    public async Task<ReviewActionResult> ApproveAsync(
        Guid taskId, string reviewer, CancellationToken ct = default)
    {
        var task = await _reviews.GetAsync(taskId, ct);
        if (task is null) return ReviewActionResult.NotFound($"Review task {taskId} not found.");
        if (task.IsClaimActive(_clock.UtcNow) &&
            !string.Equals(task.ClaimedBy, reviewer, StringComparison.OrdinalIgnoreCase))
            return ReviewActionResult.Conflict($"Task is claimed by '{task.ClaimedBy}'.");

        var document = await _documents.GetAsync(task.DocumentId, ct);
        if (document is null) return ReviewActionResult.NotFound("Document not found.");

        task.Complete(ReviewResolution.Approved, reviewer, _clock.UtcNow);
        var export = await _export.ExportAsync(document, ct);
        _audit.Add(new AuditEntry(reviewer, "review.approved", $"document:{document.Id}",
            document.CorrelationId, _clock.UtcNow, detail: export.Status.ToString()));
        await _unitOfWork.SaveChangesAsync(ct);
        return ReviewActionResult.Ok(export.Status.ToString());
    }

    public async Task<ReviewActionResult> RejectAsync(
        Guid taskId, string reason, string reviewer, CancellationToken ct = default)
    {
        var task = await _reviews.GetAsync(taskId, ct);
        if (task is null) return ReviewActionResult.NotFound($"Review task {taskId} not found.");

        var document = await _documents.GetAsync(task.DocumentId, ct);
        if (document is null) return ReviewActionResult.NotFound("Document not found.");

        task.Complete(ReviewResolution.Rejected, reviewer, _clock.UtcNow);
        if (document.State is PipelineState.InReview or PipelineState.Corrected)
            document.MarkRejected(reason, reviewer, _clock.UtcNow);
        _audit.Add(new AuditEntry(reviewer, "review.rejected", $"document:{document.Id}",
            document.CorrelationId, _clock.UtcNow, detail: reason));
        await _unitOfWork.SaveChangesAsync(ct);
        return ReviewActionResult.Ok();
    }

    private async Task LearnAnchorAsync(
        Document document, DocumentContent? content, FieldCorrectionInput correction,
        DateTime now, CancellationToken ct)
    {
        if (document.SupplierId is not { } supplierId || content is null) return;
        var anchor = AnchorLearner.FindAnchor(content, correction.NewValue);
        if (anchor is null) return;

        var supplier = await _suppliers.GetAsync(supplierId, ct);
        if (supplier is null) return;
        supplier.LearnHint(correction.FieldKey, anchor, document.Id, now);
        _logger.LogInformation(
            "Learned anchor '{Anchor}' for supplier {SupplierId} field {Field}",
            anchor, supplierId, correction.FieldKey);
    }

    private async Task<DocumentContent?> TryLoadContentAsync(
        Document document, CancellationToken ct)
    {
        try
        {
            await using var stream = await _objectStore.GetAsync(document.StorageKey, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var parser = _parsers.FirstOrDefault(
                p => p.CanParse(document.FileName, document.ContentType));
            return parser?.Parse(document.FileName, document.ContentType, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load content for anchor learning ({DocumentId})",
                document.Id);
            return null;
        }
    }
}
