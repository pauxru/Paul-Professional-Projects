using Idp.Domain.Documents;

namespace Idp.Application.Classification;

/// <summary>A single contributing feature in the transparent linear classifier.</summary>
public sealed record ClassificationFeature(string Name, double Weight, double Value)
{
    public double Contribution => Weight * Value;
}

/// <summary>Explainable classification result: class, confidence and the features that drove it.</summary>
public sealed class ClassificationResult
{
    public DocumentType Type { get; }
    public double Confidence { get; }
    public string Explanation { get; }
    public IReadOnlyList<ClassificationFeature> Features { get; }
    public IReadOnlyDictionary<DocumentType, double> Scores { get; }

    public ClassificationResult(
        DocumentType type,
        double confidence,
        string explanation,
        IReadOnlyList<ClassificationFeature> features,
        IReadOnlyDictionary<DocumentType, double> scores)
    {
        Type = type;
        Confidence = confidence;
        Explanation = explanation;
        Features = features;
        Scores = scores;
    }
}

/// <summary>
/// Classifies a document into invoice / purchase-order / delivery-note (or unknown). The default
/// adapter is a transparent rules+features linear model; an optional LLM-backed adapter sits behind
/// configuration and is never used by default.
/// </summary>
public interface IDocumentClassifier
{
    ClassificationResult Classify(Documents.DocumentContent content);
}
