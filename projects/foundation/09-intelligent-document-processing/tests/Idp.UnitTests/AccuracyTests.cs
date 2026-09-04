using Idp.Application.Extraction;
using Idp.Domain.Documents;
using Idp.Infrastructure.Classification;
using Idp.Infrastructure.Extraction;
using Idp.Infrastructure.Extraction.Parsing;
using Idp.Infrastructure.Generation;

namespace Idp.UnitTests;

/// <summary>
/// Real, measured accuracy of the deterministic classifier and extractor over the generated corpus,
/// plus a check that classification is explainable. Thresholds are set below observed numbers so the
/// test is a genuine regression guard, not a tautology.
/// </summary>
public class AccuracyTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public AccuracyTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static IReadOnlyList<SyntheticDocument> Corpus() => new DocumentGenerator().GenerateCorpus();

    [Fact]
    public void Prints_measured_accuracy_report()
    {
        var report = new AccuracyEvaluator().Evaluate(Corpus());
        _output.WriteLine($"MEASURE docs={report.Documents} " +
            $"classification={report.ClassificationAccuracy:P2} " +
            $"extraction={report.ExtractionAccuracy:P2} " +
            $"extractionCorrect={report.ExtractionCorrect}/{report.ExtractionTotal}");
        foreach (var f in report.PerField)
            _output.WriteLine($"MEASURE_FIELD {f.FieldKey}={f.Correct}/{f.Total} ({f.Rate:P1})");
        Assert.True(report.Documents > 0);
    }

    [Fact]
    public void Corpus_is_deterministic_and_non_trivial()
    {
        var a = Corpus();
        var b = Corpus();
        Assert.Equal(a.Count, b.Count);
        Assert.True(a.Count >= 15, $"corpus size {a.Count}");
        Assert.Contains(a, d => d.Type == DocumentType.Invoice);
        Assert.Contains(a, d => d.Type == DocumentType.PurchaseOrder);
        Assert.Contains(a, d => d.Type == DocumentType.DeliveryNote);
        Assert.Contains(a, d => d.Degraded);
    }

    [Fact]
    public void Classification_accuracy_meets_threshold()
    {
        var report = new AccuracyEvaluator().Evaluate(Corpus());
        Assert.True(report.ClassificationAccuracy >= 0.90,
            $"classification accuracy {report.ClassificationAccuracy:P1}");
    }

    [Fact]
    public void Extraction_accuracy_meets_threshold()
    {
        var report = new AccuracyEvaluator().Evaluate(Corpus());
        Assert.True(report.ExtractionAccuracy >= 0.95,
            $"extraction accuracy {report.ExtractionAccuracy:P1} ({report.ExtractionCorrect}/{report.ExtractionTotal})");
        Assert.All(report.PerField, f => Assert.True(f.Total > 0));
    }

    [Fact]
    public void Extraction_records_evidence_and_strategy_for_every_field()
    {
        var parser = new OcrJsonParser();
        var extractor = new DeterministicFieldExtractor();
        var invoice = Corpus().First(d => d.Type == DocumentType.Invoice && !d.Degraded);
        var content = parser.Parse(invoice.FileName, invoice.ContentType, invoice.Bytes);

        var result = extractor.Extract(DocumentType.Invoice, content, Array.Empty<ExtractionHint>());
        Assert.NotEmpty(result.Fields);
        Assert.All(result.Fields, f =>
        {
            Assert.NotEqual(ExtractionStrategy.None, f.Strategy);
            Assert.True(f.Confidence > 0);
        });
        // The clean invoice should carry a spatial line-item table.
        Assert.NotEmpty(result.LineItems);
    }

    [Fact]
    public void Classification_is_explainable()
    {
        var parser = new OcrJsonParser();
        var classifier = new RulesDocumentClassifier();
        var invoice = Corpus().First(d => d.Type == DocumentType.Invoice);
        var content = parser.Parse(invoice.FileName, invoice.ContentType, invoice.Bytes);

        var result = classifier.Classify(content);
        Assert.Equal(DocumentType.Invoice, result.Type);
        Assert.InRange(result.Confidence, 0.0, 1.0);
        Assert.NotEmpty(result.Features);
        Assert.False(string.IsNullOrWhiteSpace(result.Explanation));
        Assert.True(result.Scores.ContainsKey(DocumentType.Invoice));
    }
}
