using Idp.Application.Configuration;
using Idp.Application.Pipeline;
using Idp.Domain.Documents;

namespace Idp.UnitTests;

/// <summary>Confidence aggregation and confidence-to-route mapping, including threshold boundaries.</summary>
public class ConfidenceRoutingTests
{
    private static readonly PipelineOptions Options = new(); // AutoApprove 0.85, Reject 0.30

    // ---- Aggregation -------------------------------------------------------------------------

    [Fact]
    public void Clean_high_confidence_document_aggregates_high()
    {
        var score = ConfidenceScoring.AggregateDocumentConfidence(
            new[] { 0.95, 0.95, 0.95 }, classificationConfidence: 1.0, hardFailures: 0, warnings: 0);
        Assert.True(score >= Options.AutoApproveThreshold, $"score {score}");
    }

    [Fact]
    public void Weakest_required_field_drags_confidence_down()
    {
        var strong = ConfidenceScoring.AggregateDocumentConfidence(
            new[] { 0.95, 0.95, 0.95 }, 1.0, 0, 0);
        var withWeak = ConfidenceScoring.AggregateDocumentConfidence(
            new[] { 0.95, 0.95, 0.30 }, 1.0, 0, 0);
        Assert.True(withWeak < strong);
        Assert.True(withWeak < Options.AutoApproveThreshold);
    }

    [Fact]
    public void Hard_validation_failure_applies_a_penalty()
    {
        var clean = ConfidenceScoring.AggregateDocumentConfidence(new[] { 0.95 }, 1.0, 0, 0);
        var failed = ConfidenceScoring.AggregateDocumentConfidence(new[] { 0.95 }, 1.0, 1, 0);
        Assert.Equal(clean - ConfidenceScoring.ValidationFailurePenalty, failed, 3);
    }

    [Fact]
    public void Warnings_apply_a_smaller_penalty()
    {
        var clean = ConfidenceScoring.AggregateDocumentConfidence(new[] { 0.95 }, 1.0, 0, 0);
        var oneWarn = ConfidenceScoring.AggregateDocumentConfidence(new[] { 0.95 }, 1.0, 0, 1);
        Assert.Equal(clean - ConfidenceScoring.ValidationWarningPenalty, oneWarn, 3);
        var twoWarn = ConfidenceScoring.AggregateDocumentConfidence(new[] { 0.95 }, 1.0, 0, 2);
        Assert.True(twoWarn < Options.AutoApproveThreshold);
    }

    [Fact]
    public void No_required_fields_yields_zero_confidence()
    {
        var score = ConfidenceScoring.AggregateDocumentConfidence(
            Array.Empty<double>(), 1.0, 0, 0);
        Assert.Equal(0.0, score);
    }

    // ---- Routing -----------------------------------------------------------------------------

    [Fact]
    public void Routes_to_auto_approve_when_high_and_clean()
    {
        Assert.Equal(RoutingDecision.AutoApprove, RoutingPolicy.Decide(0.90, false, Options));
    }

    [Fact]
    public void Hard_failure_forces_review_even_when_confident()
    {
        Assert.Equal(RoutingDecision.Review, RoutingPolicy.Decide(0.99, true, Options));
    }

    [Fact]
    public void Routes_to_reject_below_reject_threshold()
    {
        Assert.Equal(RoutingDecision.Reject, RoutingPolicy.Decide(0.20, false, Options));
    }

    [Fact]
    public void Routes_to_review_in_the_middle_band()
    {
        Assert.Equal(RoutingDecision.Review, RoutingPolicy.Decide(0.50, false, Options));
    }

    [Theory]
    [InlineData(0.85, false, RoutingDecision.AutoApprove)] // exactly the auto-approve threshold
    [InlineData(0.8499, false, RoutingDecision.Review)]    // just below auto-approve
    [InlineData(0.30, false, RoutingDecision.Review)]      // exactly the reject threshold -> not rejected
    [InlineData(0.2999, false, RoutingDecision.Reject)]    // just below reject threshold
    public void Threshold_boundaries_are_pinned(double confidence, bool hardFail, RoutingDecision expected)
    {
        Assert.Equal(expected, RoutingPolicy.Decide(confidence, hardFail, Options));
    }
}
