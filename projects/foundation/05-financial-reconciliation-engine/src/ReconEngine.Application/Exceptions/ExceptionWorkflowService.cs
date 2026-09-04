using Microsoft.Extensions.Options;
using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Domain.Abstractions;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Application.Exceptions;

/// <summary>
/// Drives the manual-resolution workflow. It delegates every state transition to the
/// <see cref="ReconciliationException"/> aggregate (which owns the invariants and the four-eyes gate)
/// and, once a transition succeeds, reflects the outcome onto the underlying records so that carry-forward
/// behaves correctly: a <see cref="ResolutionReasonCode.Reprocess"/> returns records to the working set,
/// while any other resolution removes them from it.
/// </summary>
public sealed class ExceptionWorkflowService
{
    private readonly IExceptionStore _exceptions;
    private readonly IRecordStore _records;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly ReconciliationOptions _options;

    public ExceptionWorkflowService(
        IExceptionStore exceptions,
        IRecordStore records,
        IUnitOfWork uow,
        IClock clock,
        IOptions<ReconciliationOptions> options)
    {
        _exceptions = exceptions;
        _records = records;
        _uow = uow;
        _clock = clock;
        _options = options.Value;
    }

    public Task<PagedResult<ReconciliationException>> QueryAsync(ExceptionQuery query, CancellationToken ct = default)
        => _exceptions.QueryAsync(query, ct);

    public Task<ReconciliationException?> GetAsync(Guid id, CancellationToken ct = default)
        => _exceptions.GetAsync(id, ct);

    public async Task<ReconciliationException> AssignAsync(Guid id, string assignee, string actor, CancellationToken ct = default)
    {
        var ex = await Load(id, ct);
        ex.Assign(assignee, actor, _clock);
        await _uow.SaveChangesAsync(ct);
        return ex;
    }

    public async Task<ReconciliationException> CommentAsync(Guid id, string author, string text, CancellationToken ct = default)
    {
        var ex = await Load(id, ct);
        ex.AddComment(author, text, _clock);
        await _uow.SaveChangesAsync(ct);
        return ex;
    }

    public async Task<ReconciliationException> ResolveAsync(
        Guid id, ResolutionReasonCode reason, string actor, string? note, CancellationToken ct = default)
    {
        var ex = await Load(id, ct);
        ex.Resolve(reason, actor, note, _options.WriteOffApprovalThresholdMinor, _clock);

        // Only mutate record state once the exception has actually reached a resolved state.
        if (ex.Status == ExceptionStatus.Resolved)
            await ApplyResolutionToRecordsAsync(ex, reason, ct);

        await _uow.SaveChangesAsync(ct);
        return ex;
    }

    public async Task<ReconciliationException> ApproveAsync(Guid id, string approver, CancellationToken ct = default)
    {
        var ex = await Load(id, ct);
        ex.Approve(approver, _clock);

        // Approval only ever completes a write-off proposal.
        await ApplyResolutionToRecordsAsync(ex, ResolutionReasonCode.WriteOff, ct);

        await _uow.SaveChangesAsync(ct);
        return ex;
    }

    public async Task<ReconciliationException> RejectApprovalAsync(Guid id, string approver, string? note, CancellationToken ct = default)
    {
        var ex = await Load(id, ct);
        ex.RejectApproval(approver, note, _clock);
        await _uow.SaveChangesAsync(ct);
        return ex;
    }

    public async Task<ReconciliationException> ReopenAsync(Guid id, string actor, string? note, CancellationToken ct = default)
    {
        var ex = await Load(id, ct);
        ex.Reopen(actor, note, _clock);

        // Bring the records back into the working set so the next run re-evaluates them.
        var records = await _records.GetByIdsAsync(ex.GetRecordIds(), ct);
        foreach (var r in records)
            r.ReconStatus = ReconStatus.Exception;

        await _uow.SaveChangesAsync(ct);
        return ex;
    }

    private async Task ApplyResolutionToRecordsAsync(ReconciliationException ex, ResolutionReasonCode reason, CancellationToken ct)
    {
        var records = await _records.GetByIdsAsync(ex.GetRecordIds(), ct);
        foreach (var r in records)
        {
            if (reason == ResolutionReasonCode.Reprocess)
            {
                // Re-open for matching on the next run.
                r.ReconStatus = ReconStatus.Pending;
                r.LastRunId = null;
            }
            else
            {
                // Manually matched / written off / ignored: remove from the working set for good.
                r.ReconStatus = ReconStatus.Matched;
            }
        }
    }

    private async Task<ReconciliationException> Load(Guid id, CancellationToken ct)
        => await _exceptions.GetAsync(id, ct)
           ?? throw new NotFoundException($"Exception {id} not found.");
}
