using Idp.Application.Abstractions;
using Idp.Application.Configuration;
using Idp.Application.Documents;
using Idp.Domain.Audit;
using Idp.Domain.Documents;
using Idp.Domain.Exports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Idp.Application.Exporting;

public sealed record ExportResultDto(
    ExportStatus Status, string? Reference, string? Error, int Attempts);

/// <summary>
/// Drives ERP export with bounded retries, an idempotency key and a dead-letter terminal state.
/// A successful export writes to the outbox and moves the document to Exported; a run that exhausts
/// its retries writes to the dead-letter folder and leaves the document for operator intervention.
/// </summary>
public sealed class ExportService
{
    private readonly IErpExportClient _erp;
    private readonly IExportOutbox _outbox;
    private readonly IExportRepository _exports;
    private readonly IDocumentRepository _documents;
    private readonly IAuditRepository _audit;
    private readonly IClock _clock;
    private readonly ExportOptions _options;
    private readonly ILogger<ExportService> _logger;

    public ExportService(
        IErpExportClient erp,
        IExportOutbox outbox,
        IExportRepository exports,
        IDocumentRepository documents,
        IAuditRepository audit,
        IClock clock,
        IOptions<ExportOptions> options,
        ILogger<ExportService> logger)
    {
        _erp = erp;
        _outbox = outbox;
        _exports = exports;
        _documents = documents;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ExportResultDto> ExportAsync(Document document, CancellationToken ct = default)
    {
        var record = await _exports.GetByDocumentAsync(document.Id, ct);
        if (record is null)
        {
            record = new ExportRecord(
                document.Id, IdempotencyKey(document), _options.Format, _options.MaxAttempts,
                _clock.UtcNow);
            _exports.Add(record);
        }

        if (record.Status == ExportStatus.Succeeded)
            return new ExportResultDto(record.Status, record.ErpReference, null, record.Attempts);

        var payload = ErpPayloadBuilder.Build(document, record.Format);

        while (record.CanAttempt)
        {
            ct.ThrowIfCancellationRequested();
            record.RecordAttempt(_clock.UtcNow);
            var result = await _erp.SendAsync(
                new ErpExportRequest(document.Id, record.IdempotencyKey, record.Format, payload), ct);

            if (result.Success)
            {
                var name = $"{document.Id}-v{document.Version}.{Ext(record.Format)}";
                var outboxPath = await _outbox.WriteOutboxAsync(name, payload, ct);
                record.MarkSucceeded(result.Reference, outboxPath, _clock.UtcNow);
                MoveDocumentToExported(document);
                _audit.Add(new AuditEntry("system", "export.succeeded", $"document:{document.Id}",
                    document.CorrelationId, _clock.UtcNow, detail: result.Reference));
                return new ExportResultDto(record.Status, result.Reference, null, record.Attempts);
            }

            if (!result.Transient)
            {
                record.ForceDeadLetter(result.Error ?? "permanent failure", _clock.UtcNow);
                break;
            }

            record.MarkAttemptFailed(result.Error ?? "transient failure", _clock.UtcNow);
            _logger.LogWarning(
                "Export attempt {Attempt} for {DocumentId} failed: {Error}",
                record.Attempts, document.Id, result.Error);
        }

        if (record.Status == ExportStatus.DeadLettered)
        {
            var name = $"{document.Id}-v{document.Version}.deadletter.json";
            await _outbox.WriteDeadLetterAsync(name, payload, ct);
            _audit.Add(new AuditEntry("system", "export.deadlettered", $"document:{document.Id}",
                document.CorrelationId, _clock.UtcNow, detail: record.LastError));
        }

        return new ExportResultDto(record.Status, null, record.LastError, record.Attempts);
    }

    public async Task<ExportResultDto?> ReexportAsync(Guid documentId, CancellationToken ct = default)
    {
        var record = await _exports.GetByDocumentAsync(documentId, ct);
        var document = await _documents.GetAsync(documentId, ct);
        if (document is null) return null;
        record?.ResetForReexport(_clock.UtcNow);
        return await ExportAsync(document, ct);
    }

    private void MoveDocumentToExported(Document document)
    {
        if (document.State is PipelineState.AutoApproved or PipelineState.InReview
            or PipelineState.Corrected)
            document.MarkExported("system", _clock.UtcNow);
    }

    private static string IdempotencyKey(Document document) => $"{document.Id:N}:v{document.Version}";

    private static string Ext(string format) =>
        format.Equals("Csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "json";
}
