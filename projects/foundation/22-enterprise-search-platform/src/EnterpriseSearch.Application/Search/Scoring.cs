using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public sealed class Bm25Scorer(Bm25Options options) : IScorer
{
    public TermScoreExplanation ScoreTerm(InvertedIndex index, string documentId, string field, string term, Posting posting, double additionalBoost = 1d)
    {
        var statistics = index.GetFieldStatistics(field);
        var documentFrequency = index.GetDocumentFrequency(field, term);
        var fieldLength = index.GetFieldLength(documentId, field);
        var averageLength = statistics.AverageLength <= 0d ? 1d : statistics.AverageLength;
        var idf = Math.Log(1d + ((statistics.DocumentCount - documentFrequency + 0.5d) / (documentFrequency + 0.5d)));
        var denominator = posting.TermFrequency + options.K1 * (1d - options.B + options.B * fieldLength / averageLength);
        var raw = idf * (posting.TermFrequency * (options.K1 + 1d) / denominator);
        var contribution = raw * index.Definition.GetBoost(field) * additionalBoost;
        return new TermScoreExplanation(field, term, posting.TermFrequency, documentFrequency, fieldLength, averageLength, idf, contribution);
    }
}

public sealed class FunctionScorer(FunctionScoreOptions options, IClock clock, ISearchAnalytics analytics)
{
    public FunctionScoreBreakdown Score(SearchDocument document, string index, string? query)
    {
        var ageDays = Math.Max(0d, (clock.UtcNow - document.CreatedAt).TotalDays);
        var recency = Math.Exp(-Math.Log(2d) * ageDays / options.RecencyHalfLifeDays);
        var popularity = Math.Log(1d + Math.Max(0d, document.Popularity));
        var multiplier = 1d + (options.RecencyWeight * recency) + (options.PopularityWeight * popularity);
        if (document.IsInStock) multiplier *= options.InStockMultiplier;
        var clickBoost = string.IsNullOrWhiteSpace(query) ? 0d : analytics.GetClickBoost(index, query, document.Id) * options.ClickWeight;
        return new FunctionScoreBreakdown(multiplier, clickBoost, $"recency={recency:F3}; popularity={popularity:F3}; inStock={document.IsInStock}; click={clickBoost:F3}");
    }
}
