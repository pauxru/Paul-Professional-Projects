namespace Idp.Domain.Documents;

/// <summary>
/// Aggregate root for an ingested document and its full processing lifecycle. All state changes go
/// through <see cref="TransitionTo"/> which enforces <see cref="PipelineStateMachine"/> and appends
/// an audited <see cref="PipelineTransition"/>. Child collections (fields, line items, validations)
/// are replaced atomically as each pipeline stage completes.
/// </summary>
public sealed class Document
{
    private readonly List<ExtractedField> _fields = new();
    private readonly List<LineItem> _lineItems = new();
    private readonly List<PipelineTransition> _transitions = new();
    private readonly List<DocumentValidation> _validations = new();

    public Guid Id { get; private set; }
    public string FileName { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public string ContentHash { get; private set; } = default!;
    public string StorageKey { get; private set; } = default!;
    public long SizeBytes { get; private set; }
    public int Version { get; private set; }

    public DocumentType DocumentType { get; private set; }
    public double ClassificationConfidence { get; private set; }
    public string? ClassificationExplanation { get; private set; }

    public PipelineState State { get; private set; }
    public Guid? SupplierId { get; private set; }
    public string? SupplierNameRaw { get; private set; }
    public string? Currency { get; private set; }

    public double DocumentConfidence { get; private set; }
    public RoutingDecision? Routing { get; private set; }
    public decimal? DocumentValue { get; private set; }

    public string CorrelationId { get; private set; } = default!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public IReadOnlyCollection<ExtractedField> Fields => _fields;
    public IReadOnlyCollection<LineItem> LineItems => _lineItems;
    public IReadOnlyCollection<PipelineTransition> Transitions => _transitions;
    public IReadOnlyCollection<DocumentValidation> Validations => _validations;

    private Document() { }

    public static Document Receive(
        string fileName,
        string contentType,
        string contentHash,
        string storageKey,
        long sizeBytes,
        string correlationId,
        DateTime nowUtc,
        int version = 1)
    {
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            FileName = fileName,
            ContentType = contentType,
            ContentHash = contentHash,
            StorageKey = storageKey,
            SizeBytes = sizeBytes,
            Version = version,
            State = PipelineState.Received,
            DocumentType = DocumentType.Unknown,
            CorrelationId = correlationId,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        doc._transitions.Add(new PipelineTransition(
            doc.Id, PipelineState.Received, PipelineState.Received,
            "Document received", "system", nowUtc));
        return doc;
    }

    public ExtractedField? Field(string key) =>
        _fields.FirstOrDefault(f => f.FieldKey == key);

    public void TransitionTo(PipelineState newState, string reason, string actor, DateTime nowUtc)
    {
        PipelineStateMachine.EnsureCanTransition(State, newState);
        var from = State;
        State = newState;
        UpdatedAtUtc = nowUtc;
        _transitions.Add(new PipelineTransition(Id, from, newState, reason, actor, nowUtc));
    }

    public void ApplyClassification(
        DocumentType type, double confidence, string? explanation, DateTime nowUtc,
        string actor = "classifier")
    {
        DocumentType = type;
        ClassificationConfidence = ConfidenceScoring.Clamp01(confidence);
        ClassificationExplanation = explanation;
        TransitionTo(PipelineState.Classified, $"Classified as {type}", actor, nowUtc);
    }

    public void ApplyExtraction(
        IEnumerable<ExtractedField> fields,
        IEnumerable<LineItem> lineItems,
        string? currency,
        decimal? documentValue,
        DateTime nowUtc,
        string actor = "extractor")
    {
        _fields.Clear();
        foreach (var f in fields)
        {
            f.AttachTo(Id);
            _fields.Add(f);
        }
        _lineItems.Clear();
        var n = 1;
        foreach (var li in lineItems.OrderBy(l => l.LineNumber))
        {
            li.AttachTo(Id);
            _lineItems.Add(li);
            n++;
        }
        Currency = currency;
        DocumentValue = documentValue;
        TransitionTo(PipelineState.Extracted, "Fields extracted", actor, nowUtc);
    }

    public void ApplyValidation(
        IEnumerable<DocumentValidation> validations, DateTime nowUtc, string actor = "validator")
    {
        _validations.Clear();
        foreach (var v in validations)
        {
            v.AttachTo(Id);
            _validations.Add(v);
        }
        TransitionTo(PipelineState.Validated, "Validation complete", actor, nowUtc);
    }

    public void ApplyRouting(
        RoutingDecision decision, double documentConfidence, DateTime nowUtc,
        string actor = "router")
    {
        DocumentConfidence = ConfidenceScoring.Clamp01(documentConfidence);
        Routing = decision;
        var target = decision switch
        {
            RoutingDecision.AutoApprove => PipelineState.AutoApproved,
            RoutingDecision.Review => PipelineState.InReview,
            RoutingDecision.Reject => PipelineState.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(decision))
        };
        TransitionTo(target, $"Routed to {decision} (confidence {DocumentConfidence:0.00})",
            actor, nowUtc);
    }

    public void AssignSupplier(Guid? supplierId, string? supplierNameRaw)
    {
        SupplierId = supplierId;
        SupplierNameRaw = supplierNameRaw;
    }

    public bool HasHardValidationFailure =>
        _validations.Any(v => v.Outcome == ValidationOutcome.Fail);

    /// <summary>Apply a reviewer correction to a field, moving the document to Corrected.</summary>
    public void ApplyFieldCorrection(
        string fieldKey, string? newValue, string? normalizedValue, string reviewer,
        DateTime nowUtc)
    {
        var field = Field(fieldKey);
        if (field is null)
        {
            field = new ExtractedField(fieldKey, newValue, normalizedValue, 1.0,
                ExtractionStrategy.Derived, isRequired: false);
            field.AttachTo(Id);
            _fields.Add(field);
        }
        else
        {
            field.ApplyCorrection(newValue, normalizedValue);
        }

        if (State == PipelineState.InReview)
            TransitionTo(PipelineState.Corrected, $"Field '{fieldKey}' corrected", reviewer, nowUtc);
        else
            UpdatedAtUtc = nowUtc;
    }

    public void MarkExported(string actor, DateTime nowUtc) =>
        TransitionTo(PipelineState.Exported, "Exported to ERP", actor, nowUtc);

    public void MarkRejected(string reason, string actor, DateTime nowUtc) =>
        TransitionTo(PipelineState.Rejected, reason, actor, nowUtc);

    public void MarkFailed(string reason, string actor, DateTime nowUtc)
    {
        // Failure is reachable from any non-terminal state.
        if (PipelineStateMachine.IsTerminal(State)) return;
        TransitionTo(PipelineState.Failed, reason, actor, nowUtc);
    }

    /// <summary>Reset a terminal document back to Received to re-run the pipeline (new version).</summary>
    public void Reprocess(string actor, DateTime nowUtc)
    {
        if (!PipelineStateMachine.IsTerminal(State))
            throw new InvalidOperationException(
                $"Only terminal documents can be reprocessed (current state {State}).");
        var from = State;
        State = PipelineState.Received;
        Version += 1;
        _fields.Clear();
        _lineItems.Clear();
        _validations.Clear();
        Routing = null;
        DocumentConfidence = 0;
        UpdatedAtUtc = nowUtc;
        _transitions.Add(new PipelineTransition(
            Id, from, PipelineState.Received, "Reprocess requested", actor, nowUtc));
    }
}
