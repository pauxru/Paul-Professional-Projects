using System.Security.Cryptography;
using Idp.Application.Abstractions;
using Idp.Application.Configuration;
using Idp.Application.Exporting;
using Idp.Application.Pipeline;
using Idp.Domain.Audit;
using Idp.Domain.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Idp.Application.Documents;

public enum IntakeStatus { Created, Duplicate, Invalid }

public sealed record IntakeResult(IntakeStatus Status, Document? Document, string? Message = null);

/// <summary>
/// Ingestion use case: validate the upload, hash it for duplicate detection, store the original bytes
/// in the object store, create the document aggregate, run the pipeline and (when configured)
/// auto-export straight-through-processed documents. Also drives reprocessing.
/// </summary>
public sealed class DocumentIntakeService
{
    private readonly IObjectStore _objectStore;
    private readonly IDocumentRepository _documents;
    private readonly DocumentPipeline _pipeline;
    private readonly ExportService _export;
    private readonly IAuditRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IngestionOptions _ingestion;
    private readonly PipelineOptions _pipelineOptions;
    private readonly ILogger<DocumentIntakeService> _logger;

    public DocumentIntakeService(
        IObjectStore objectStore,
        IDocumentRepository documents,
        DocumentPipeline pipeline,
        ExportService export,
        IAuditRepository audit,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOptions<IngestionOptions> ingestion,
        IOptions<PipelineOptions> pipelineOptions,
        ILogger<DocumentIntakeService> logger)
    {
        _objectStore = objectStore;
        _documents = documents;
        _pipeline = pipeline;
        _export = export;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _ingestion = ingestion.Value;
        _pipelineOptions = pipelineOptions.Value;
        _logger = logger;
    }

    public async Task<IntakeResult> IngestAsync(
        string fileName, string contentType, byte[] bytes, string correlationId, string submittedBy,
        CancellationToken ct = default)
    {
        var validation = Validate(fileName, contentType, bytes);
        if (validation is not null)
            return new IntakeResult(IntakeStatus.Invalid, null, validation);

        var hash = Sha256Hex(bytes);
        var duplicate = await _documents.GetByContentHashAsync(hash, ct);
        if (duplicate is not null)
        {
            _logger.LogInformation("Duplicate upload {FileName} matches {DocumentId}",
                fileName, duplicate.Id);
            return new IntakeResult(IntakeStatus.Duplicate, duplicate,
                $"Identical content already ingested as document {duplicate.Id}.");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var storageKey = await _objectStore.PutAsync(fileName, stream, ct);

        var document = Document.Receive(
            fileName, contentType, hash, storageKey, bytes.LongLength, correlationId, _clock.UtcNow);
        _documents.Add(document);
        _audit.Add(new AuditEntry(submittedBy, "document.received", $"document:{document.Id}",
            correlationId, _clock.UtcNow, afterHash: hash, detail: fileName));

        await _pipeline.ProcessAsync(document, ct);
        await AutoExportIfEligibleAsync(document, ct);

        await _unitOfWork.SaveChangesAsync(ct);
        return new IntakeResult(IntakeStatus.Created, document);
    }

    public async Task<IntakeResult?> ReprocessAsync(
        Guid documentId, string actor, CancellationToken ct = default)
    {
        var document = await _documents.GetAsync(documentId, ct);
        if (document is null) return null;

        if (!PipelineStateMachine.IsTerminal(document.State))
            return new IntakeResult(IntakeStatus.Invalid, document,
                $"Document is in state {document.State}; only terminal documents can be reprocessed.");

        document.Reprocess(actor, _clock.UtcNow);
        await _pipeline.ProcessAsync(document, ct);
        await AutoExportIfEligibleAsync(document, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return new IntakeResult(IntakeStatus.Created, document);
    }

    private async Task AutoExportIfEligibleAsync(Document document, CancellationToken ct)
    {
        if (_pipelineOptions.AutoExportOnApprove &&
            document.Routing == RoutingDecision.AutoApprove &&
            document.State == PipelineState.AutoApproved)
        {
            await _export.ExportAsync(document, ct);
        }
    }

    private string? Validate(string fileName, string contentType, byte[] bytes)
    {
        if (bytes.LongLength == 0) return "Uploaded file is empty.";
        if (bytes.LongLength > _ingestion.MaxUploadBytes)
            return $"File exceeds the maximum size of {_ingestion.MaxUploadBytes} bytes.";

        var extensionOk = _ingestion.AllowedExtensions.Any(
            ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var contentTypeOk = _ingestion.AllowedContentTypes.Contains(
            contentType, StringComparer.OrdinalIgnoreCase);
        if (!extensionOk && !contentTypeOk)
            return $"Content type '{contentType}' / file '{fileName}' is not an accepted document type.";

        return null;
    }

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
