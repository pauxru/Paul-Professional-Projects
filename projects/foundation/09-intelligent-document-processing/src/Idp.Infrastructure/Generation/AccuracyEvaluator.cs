using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Domain.Documents;
using Idp.Infrastructure.Classification;
using Idp.Infrastructure.Extraction;
using Idp.Infrastructure.Extraction.Parsing;

namespace Idp.Infrastructure.Generation;

/// <summary>Per-field extraction accuracy over the corpus.</summary>
public sealed record FieldAccuracy(string FieldKey, int Total, int Correct)
{
    public double Rate => Total == 0 ? 0 : (double)Correct / Total;
}

/// <summary>Measured accuracy of the deterministic classifier and extractor over a corpus.</summary>
public sealed record AccuracyReport(
    int Documents,
    int ClassifiedCorrect,
    int ExtractionTotal,
    int ExtractionCorrect,
    IReadOnlyList<FieldAccuracy> PerField)
{
    public double ClassificationAccuracy => Documents == 0 ? 0 : (double)ClassifiedCorrect / Documents;
    public double ExtractionAccuracy => ExtractionTotal == 0 ? 0 : (double)ExtractionCorrect / ExtractionTotal;
}

/// <summary>
/// Runs the default classifier and extractor over a generated corpus and compares the results with
/// the corpus ground truth, producing real, reproducible accuracy numbers. Used by the accuracy tests
/// and to populate <c>docs/accuracy-report.md</c>. Pure and offline: no database, no network.
/// </summary>
public sealed class AccuracyEvaluator
{
    private readonly IDocumentParser _parser = new OcrJsonParser();
    private readonly RulesDocumentClassifier _classifier = new();
    private readonly DeterministicFieldExtractor _extractor = new();

    public AccuracyReport Evaluate(IReadOnlyList<SyntheticDocument> corpus)
    {
        var classifiedCorrect = 0;
        var perField = new Dictionary<string, (int Total, int Correct)>();

        foreach (var doc in corpus)
        {
            var content = _parser.Parse(doc.FileName, doc.ContentType, doc.Bytes);

            var classification = _classifier.Classify(content);
            if (classification.Type == doc.Type) classifiedCorrect++;

            var extraction = _extractor.Extract(doc.Type, content, Array.Empty<ExtractionHint>());
            foreach (var (key, expected) in doc.Expected)
            {
                var field = extraction.Fields.FirstOrDefault(f => f.FieldKey == key);
                var correct = FieldMatches(key, expected, field);
                var acc = perField.GetValueOrDefault(key);
                perField[key] = (acc.Total + 1, acc.Correct + (correct ? 1 : 0));
            }
        }

        var perFieldList = perField
            .Select(kv => new FieldAccuracy(kv.Key, kv.Value.Total, kv.Value.Correct))
            .OrderBy(f => f.FieldKey)
            .ToList();

        return new AccuracyReport(
            corpus.Count,
            classifiedCorrect,
            perFieldList.Sum(f => f.Total),
            perFieldList.Sum(f => f.Correct),
            perFieldList);
    }

    private static bool FieldMatches(string key, string expected, ExtractedField? field)
    {
        if (field is null) return false;
        var value = field.NormalizedValue ?? field.RawValue;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (IsAmountKey(key))
        {
            var a = DocumentFieldReader.ParseAmount(expected);
            var b = DocumentFieldReader.ParseAmount(value);
            return a is not null && b is not null && Math.Abs(a.Value - b.Value) < 0.005m;
        }
        if (IsDateKey(key))
        {
            var a = DocumentFieldReader.ParseDate(expected);
            var b = DocumentFieldReader.ParseDate(value);
            return a is not null && b == a;
        }
        return NormalizeText(expected) == NormalizeText(value);
    }

    private static bool IsAmountKey(string key) =>
        key is FieldKeys.Total or FieldKeys.Subtotal or FieldKeys.Tax;

    private static bool IsDateKey(string key) =>
        key is FieldKeys.InvoiceDate or FieldKeys.DueDate or FieldKeys.PoDate or FieldKeys.DnDate;

    private static string NormalizeText(string text) =>
        new(text.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
