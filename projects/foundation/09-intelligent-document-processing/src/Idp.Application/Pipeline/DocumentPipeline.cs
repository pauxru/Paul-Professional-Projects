using Idp.Application.Abstractions;
using Idp.Application.Ai;
using Idp.Application.Classification;
using Idp.Application.Configuration;
using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Application.Review;
using Idp.Application.Suppliers;
using Idp.Application.Validation;
using Idp.Domain.Audit;
using Idp.Domain.Documents;
using Idp.Domain.Review;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Idp.Application.Pipeline;

/// <summary>
/// Orchestrates a document through the full pipeline: classify -> extract (with per-supplier hints)
/// -> validate (deterministic guardrails) -> aggregate confidence -> route. A review task is created
/// when the document is routed to human review. The pipeline mutates the aggregate but does not
/// persist; callers own the unit-of-work boundary.
/// </summary>
public sealed class DocumentPipeline
{
    private readonly IObjectStore _objectStore;
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly IDocumentClassifier _classifier;
    private readonly IFieldExtractor _extractor;
    private readonly IValidationEngine _validation;
    private readonly IDocumentRepository _documents;
    private readonly ISupplierRepository _suppliers;
    private readonly IReviewRepository _reviews;
    private readonly IAuditRepository _audit;
    private readonly IClock _clock;
    private readonly PipelineOptions _pipeline;
    private readonly ValidationOptions _validationOptions;
    private readonly ReviewOptions _review;
    private readonly ILogger<DocumentPipeline> _logger;

    public DocumentPipeline(
        IObjectStore objectStore,
        IEnumerable<IDocumentParser> parsers,
        IDocumentClassifier classifier,
        IFieldExtractor extractor,
        IValidationEngine validation,
        IDocumentRepository documents,
        ISupplierRepository suppliers,
        IReviewRepository reviews,
        IAuditRepository audit,
        IClock clock,
        IOptions<PipelineOptions> pipeline,
        IOptions<ValidationOptions> validationOptions,
        IOptions<ReviewOptions> review,
        ILogger<DocumentPipeline> logger)
    {
        _objectStore = objectStore;
        _parsers = parsers;
        _classifier = classifier;
        _extractor = extractor;
        _validation = validation;
        _documents = documents;
        _suppliers = suppliers;
        _reviews = reviews;
        _audit = audit;
        _clock = clock;
        _pipeline = pipeline.Value;
        _validationOptions = validationOptions.Value;
        _review = review.Value;
        _logger = logger;
    }

    public async Task<PipelineOutcome> ProcessAsync(Document document, CancellationToken ct = default)
    {
        try
        {
            var content = await LoadContentAsync(document, ct);
            return await RunAsync(document, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for document {DocumentId}", document.Id);
            document.MarkFailed(ex.Message, "system", _clock.UtcNow);
            return new PipelineOutcome(document.State, document.Routing, document.DocumentConfidence);
        }
    }

    /// <summary>Runs the pipeline against already-parsed content (used directly by tests).</summary>
    public async Task<PipelineOutcome> RunAsync(
        Document document, DocumentContent content, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        var classification = _classifier.Classify(content);
        document.ApplyClassification(
            classification.Type, classification.Confidence, classification.Explanation, now);

        var firstPass = _extractor.Extract(classification.Type, content, Array.Empty<ExtractionHint>());
        var supplierNameRaw = firstPass.Fields
            .FirstOrDefault(f => f.FieldKey == FieldKeys.SupplierName) is { } snf
            ? snf.NormalizedValue ?? snf.RawValue
            : null;

        var suppliers = await _suppliers.GetAllAsync(ct);
        var (match, score) = SupplierMatching.BestMatch(supplierNameRaw, suppliers);
        var supplier = match is not null && score >= _validationOptions.SupplierMatchThreshold
            ? match
            : null;

        var result = firstPass;
        if (supplier is { Hints.Count: > 0 })
        {
            var hints = supplier.Hints
                .Select(h => new ExtractionHint(h.FieldKey, h.AnchorText))
                .ToList();
            result = _extractor.Extract(classification.Type, content, hints);
        }

        document.AssignSupplier(supplier?.Id, supplierNameRaw);
        document.ApplyExtraction(
            result.Fields, result.LineItems, result.Currency, result.DocumentValue, now);

        var context = await BuildContextAsync(document, suppliers, ct);
        var findings = _validation.Validate(context);
        document.ApplyValidation(findings, now);

        var confidence = AggregateConfidence(document, classification);
        var decision = RoutingPolicy.Decide(
            confidence, document.HasHardValidationFailure, _pipeline);
        document.ApplyRouting(decision, confidence, now);

        if (decision == RoutingDecision.Review)
        {
            var existing = await _reviews.GetByDocumentAsync(document.Id, ct);
            if (existing is null)
            {
                var priority = ReviewPriority.Calculate(document.DocumentValue, confidence);
                _reviews.Add(new ReviewTask(
                    document.Id, priority, document.DocumentValue, confidence, now,
                    TimeSpan.FromHours(_review.SlaHours)));
            }
        }

        _audit.Add(new AuditEntry(
            "system", "pipeline.processed", $"document:{document.Id}", document.CorrelationId, now,
            detail: $"type={classification.Type};route={decision};confidence={confidence:0.00}"));

        return new PipelineOutcome(document.State, decision, confidence);
    }

    private async Task<DocumentContent> LoadContentAsync(Document document, CancellationToken ct)
    {
        await using var stream = await _objectStore.GetAsync(document.StorageKey, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var parser = _parsers.FirstOrDefault(p => p.CanParse(document.FileName, document.ContentType))
            ?? throw new NotSupportedException(
                $"No parser for '{document.FileName}' ({document.ContentType}).");
        return parser.Parse(document.FileName, document.ContentType, bytes);
    }

    private async Task<ValidationContext> BuildContextAsync(
        Document document, IReadOnlyList<Domain.Suppliers.Supplier> suppliers, CancellationToken ct)
    {
        var invoiceKeys = await _documents.GetInvoiceKeysAsync(ct);
        Document? po = null;
        Document? dn = null;

        if (document.DocumentType == DocumentType.Invoice)
        {
            var poRef = document.Text(FieldKeys.PoReference);
            if (!string.IsNullOrWhiteSpace(poRef))
            {
                po = await _documents.FindByReferenceAsync(
                    DocumentType.PurchaseOrder, FieldKeys.PoNumber, poRef, ct);
                dn = await _documents.FindByReferenceAsync(
                    DocumentType.DeliveryNote, FieldKeys.PoReference, poRef, ct);
            }
        }

        return new ValidationContext
        {
            Document = document,
            NowUtc = _clock.UtcNow,
            KnownSuppliers = suppliers,
            ExistingInvoices = invoiceKeys,
            MatchingPurchaseOrder = po,
            MatchingDeliveryNote = dn,
            Tolerances = new ValidationTolerances(
                _validationOptions.ArithmeticAbsoluteTolerance,
                _validationOptions.ArithmeticRelativeTolerance,
                _validationOptions.QuantityVariancePercent,
                _validationOptions.PriceVariancePercent,
                _validationOptions.SupplierMatchThreshold)
        };
    }

    private static double AggregateConfidence(Document document, ClassificationResult classification)
    {
        var required = DocumentSchemas.RequiredFields.TryGetValue(document.DocumentType, out var keys)
            ? keys
            : Array.Empty<string>();
        var confidences = required
            .Select(k => document.Field(k)?.Confidence ?? 0.0)
            .ToList();
        var hardFailures = document.Validations.Count(v => v.Outcome == ValidationOutcome.Fail);
        var warnings = document.Validations.Count(v => v.Outcome == ValidationOutcome.Warn);
        return ConfidenceScoring.AggregateDocumentConfidence(
            confidences, classification.Confidence, hardFailures, warnings);
    }
}

/// <summary>The result of a pipeline pass.</summary>
public sealed record PipelineOutcome(
    PipelineState State, RoutingDecision? Decision, double Confidence);
