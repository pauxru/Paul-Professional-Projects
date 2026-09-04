using Idp.Application.Classification;
using Idp.Application.Documents;
using Idp.Domain.Documents;

namespace Idp.Infrastructure.Classification;

/// <summary>
/// Default, fully explainable document classifier. It scores each candidate type with a transparent
/// linear model over keyword features (weighted regex/keyword hits) and layout cues (header-band
/// keyword position and table presence). The winning score is turned into a confidence via a softmax,
/// and the contributing features are returned so a human can see exactly why a decision was made.
/// Weights are data, not code paths, so they can be tuned without changing logic.
/// </summary>
public sealed class RulesDocumentClassifier : IDocumentClassifier
{
    private sealed record KeywordFeature(string Phrase, double Weight);

    // Transparent, tunable weights. Header-band hits are additionally boosted (see Classify).
    private static readonly IReadOnlyDictionary<DocumentType, KeywordFeature[]> Model =
        new Dictionary<DocumentType, KeywordFeature[]>
        {
            [DocumentType.Invoice] = new[]
            {
                new KeywordFeature("tax invoice", 3.0),
                new KeywordFeature("invoice", 2.2),
                new KeywordFeature("invoice number", 1.6),
                new KeywordFeature("invoice no", 1.2),
                new KeywordFeature("amount due", 1.4),
                new KeywordFeature("bill to", 1.0),
                new KeywordFeature("subtotal", 0.8),
                new KeywordFeature("vat", 0.7),
                new KeywordFeature("due date", 0.7),
                new KeywordFeature("bank details", 0.6)
            },
            [DocumentType.PurchaseOrder] = new[]
            {
                new KeywordFeature("purchase order", 3.0),
                new KeywordFeature("po number", 1.8),
                new KeywordFeature("po no", 1.2),
                new KeywordFeature("order date", 1.2),
                new KeywordFeature("ship to", 1.0),
                new KeywordFeature("buyer", 1.0),
                new KeywordFeature("vendor", 0.6),
                new KeywordFeature("order total", 0.8)
            },
            [DocumentType.DeliveryNote] = new[]
            {
                new KeywordFeature("delivery note", 3.0),
                new KeywordFeature("goods received", 1.6),
                new KeywordFeature("delivery date", 1.4),
                new KeywordFeature("quantity delivered", 1.2),
                new KeywordFeature("received by", 1.0),
                new KeywordFeature("consignment", 0.9),
                new KeywordFeature("dispatch", 0.7)
            }
        };

    public ClassificationResult Classify(DocumentContent content)
    {
        var text = (content.RawText ?? string.Empty).ToLowerInvariant();
        var headerBand = HeaderBandText(content);

        var scores = new Dictionary<DocumentType, double>();
        var featuresByType = new Dictionary<DocumentType, List<ClassificationFeature>>();

        foreach (var (type, keywords) in Model)
        {
            double score = 0;
            var feats = new List<ClassificationFeature>();
            foreach (var kw in keywords)
            {
                if (!text.Contains(kw.Phrase, StringComparison.Ordinal)) continue;
                var inHeader = headerBand.Contains(kw.Phrase, StringComparison.Ordinal);
                var value = inHeader ? 1.5 : 1.0; // header-band presence boosts the signal
                score += kw.Weight * value;
                feats.Add(new ClassificationFeature(
                    inHeader ? $"{kw.Phrase} (header)" : kw.Phrase, kw.Weight, value));
            }
            scores[type] = score;
            featuresByType[type] = feats;
        }

        // Table presence is a mild positive signal for all structured document types.
        var tableRows = CountTableRows(content);
        if (tableRows >= 2)
        {
            foreach (var type in Model.Keys)
            {
                scores[type] += 0.5;
                featuresByType[type].Add(new ClassificationFeature("table detected", 0.5, 1.0));
            }
        }

        var best = scores.OrderByDescending(kv => kv.Value).First();
        var confidence = Softmax(scores, best.Key);

        if (best.Value <= 0)
        {
            return new ClassificationResult(
                DocumentType.Unknown, 0.0,
                "No classifying keywords matched.",
                Array.Empty<ClassificationFeature>(),
                scores);
        }

        var winning = featuresByType[best.Key]
            .OrderByDescending(f => f.Contribution)
            .ToList();
        var explanation =
            $"Classified as {best.Key} (score {best.Value:0.00}, confidence {confidence:0.00}). " +
            $"Top signals: {string.Join(", ", winning.Take(3).Select(f => f.Name))}.";

        return new ClassificationResult(best.Key, confidence, explanation, winning, scores);
    }

    private static string HeaderBandText(DocumentContent content)
    {
        if (!content.HasLayout)
        {
            // First two lines of raw text act as the header band for non-spatial documents.
            var lines = (content.RawText ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", lines.Take(2)).ToLowerInvariant();
        }

        var page1 = content.Pages.FirstOrDefault(p => p.Words.Count > 0);
        if (page1 is null) return string.Empty;
        var topY = page1.Words.Min(w => w.Y);
        var band = page1.Height > 0 ? page1.Height * 0.18 : 60;
        var headerWords = page1.Words
            .Where(w => w.Y <= topY + band)
            .OrderBy(w => w.Y).ThenBy(w => w.X)
            .Select(w => w.Text);
        return string.Join(" ", headerWords).ToLowerInvariant();
    }

    /// <summary>Count rows that look like table lines: two or more numeric tokens on the same line.</summary>
    private static int CountTableRows(DocumentContent content)
    {
        var lines = (content.RawText ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        foreach (var line in lines)
        {
            var numerics = line.Split(new[] { ' ', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(t => t.Any(char.IsDigit) && t.Count(char.IsDigit) >= t.Length - 3);
            if (numerics >= 2) count++;
        }
        return count;
    }

    private static double Softmax(IReadOnlyDictionary<DocumentType, double> scores, DocumentType target)
    {
        // Numerically stable softmax over the candidate scores.
        var max = scores.Values.Max();
        var exps = scores.ToDictionary(kv => kv.Key, kv => Math.Exp(kv.Value - max));
        var sum = exps.Values.Sum();
        return sum <= 0 ? 0 : exps[target] / sum;
    }
}
