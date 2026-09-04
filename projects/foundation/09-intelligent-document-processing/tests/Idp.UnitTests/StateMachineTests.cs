using Idp.Domain.Documents;

namespace Idp.UnitTests;

/// <summary>The pipeline state machine transition matrix, terminals, and the end-to-end happy path.</summary>
public class StateMachineTests
{
    private static readonly DateTime Now = new(2024, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(PipelineState.Received, PipelineState.Classified)]
    [InlineData(PipelineState.Received, PipelineState.Failed)]
    [InlineData(PipelineState.Classified, PipelineState.Extracted)]
    [InlineData(PipelineState.Extracted, PipelineState.Validated)]
    [InlineData(PipelineState.Validated, PipelineState.AutoApproved)]
    [InlineData(PipelineState.Validated, PipelineState.InReview)]
    [InlineData(PipelineState.Validated, PipelineState.Rejected)]
    [InlineData(PipelineState.AutoApproved, PipelineState.Exported)]
    [InlineData(PipelineState.InReview, PipelineState.Corrected)]
    [InlineData(PipelineState.InReview, PipelineState.Exported)]
    [InlineData(PipelineState.InReview, PipelineState.Rejected)]
    [InlineData(PipelineState.Corrected, PipelineState.Exported)]
    [InlineData(PipelineState.Corrected, PipelineState.InReview)]
    public void Valid_transitions_are_allowed(PipelineState from, PipelineState to)
    {
        Assert.True(PipelineStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(PipelineState.Received, PipelineState.Extracted)]
    [InlineData(PipelineState.Received, PipelineState.Exported)]
    [InlineData(PipelineState.Classified, PipelineState.Validated)]
    [InlineData(PipelineState.Extracted, PipelineState.AutoApproved)]
    [InlineData(PipelineState.Validated, PipelineState.Exported)]
    [InlineData(PipelineState.AutoApproved, PipelineState.InReview)]
    [InlineData(PipelineState.Exported, PipelineState.Received)]
    [InlineData(PipelineState.Rejected, PipelineState.Exported)]
    public void Invalid_transitions_are_rejected(PipelineState from, PipelineState to)
    {
        Assert.False(PipelineStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidPipelineTransitionException>(
            () => PipelineStateMachine.EnsureCanTransition(from, to));
    }

    [Theory]
    [InlineData(PipelineState.Exported, true)]
    [InlineData(PipelineState.Rejected, true)]
    [InlineData(PipelineState.Failed, true)]
    [InlineData(PipelineState.Received, false)]
    [InlineData(PipelineState.InReview, false)]
    public void Terminal_states_are_identified(PipelineState state, bool terminal)
    {
        Assert.Equal(terminal, PipelineStateMachine.IsTerminal(state));
    }

    [Fact]
    public void Happy_path_drives_document_to_exported_and_audits_every_transition()
    {
        var doc = Document.Receive("f.ocr.json", "application/vnd.idp.ocr+json", "h", "k", 100, "corr", Now);
        doc.ApplyClassification(DocumentType.Invoice, 1.0, "why", Now);
        doc.ApplyExtraction(Array.Empty<ExtractedField>(), Array.Empty<LineItem>(), "KES", 100m, Now);
        doc.ApplyValidation(Array.Empty<DocumentValidation>(), Now);
        doc.ApplyRouting(RoutingDecision.AutoApprove, 0.9, Now);
        doc.MarkExported("system", Now);

        Assert.Equal(PipelineState.Exported, doc.State);
        // Received(seed) + Received->Classified->Extracted->Validated->AutoApproved->Exported = 6.
        Assert.Equal(6, doc.Transitions.Count);
        Assert.All(doc.Transitions, t => Assert.False(string.IsNullOrWhiteSpace(t.Reason)));
    }

    [Fact]
    public void Review_path_supports_correction_then_export()
    {
        var doc = Document.Receive("f.ocr.json", "application/vnd.idp.ocr+json", "h", "k", 100, "corr", Now);
        doc.ApplyClassification(DocumentType.Invoice, 1.0, "why", Now);
        doc.ApplyExtraction(Array.Empty<ExtractedField>(), Array.Empty<LineItem>(), "KES", 100m, Now);
        doc.ApplyValidation(Array.Empty<DocumentValidation>(), Now);
        doc.ApplyRouting(RoutingDecision.Review, 0.6, Now);
        doc.ApplyFieldCorrection(FieldKeys(), "INV-1", "INV-1", "alice", Now);
        Assert.Equal(PipelineState.Corrected, doc.State);
        doc.MarkExported("alice", Now);
        Assert.Equal(PipelineState.Exported, doc.State);
    }

    [Fact]
    public void Reprocess_resets_a_terminal_document_and_bumps_version()
    {
        var doc = Document.Receive("f.ocr.json", "application/vnd.idp.ocr+json", "h", "k", 100, "corr", Now);
        doc.ApplyClassification(DocumentType.Invoice, 1.0, "why", Now);
        doc.ApplyExtraction(new[] { new ExtractedField("x", "1", "1", 0.9, ExtractionStrategy.Anchor, true) },
            Array.Empty<LineItem>(), "KES", 1m, Now);
        doc.ApplyValidation(Array.Empty<DocumentValidation>(), Now);
        doc.ApplyRouting(RoutingDecision.Reject, 0.1, Now);
        Assert.Equal(PipelineState.Rejected, doc.State);

        doc.Reprocess("system", Now);
        Assert.Equal(PipelineState.Received, doc.State);
        Assert.Equal(2, doc.Version);
        Assert.Empty(doc.Fields);
    }

    [Fact]
    public void Reprocess_on_non_terminal_document_throws()
    {
        var doc = Document.Receive("f.ocr.json", "application/vnd.idp.ocr+json", "h", "k", 100, "corr", Now);
        doc.ApplyClassification(DocumentType.Invoice, 1.0, "why", Now);
        Assert.Throws<InvalidOperationException>(() => doc.Reprocess("system", Now));
    }

    private static string FieldKeys() => Idp.Application.Extraction.FieldKeys.InvoiceNumber;
}
