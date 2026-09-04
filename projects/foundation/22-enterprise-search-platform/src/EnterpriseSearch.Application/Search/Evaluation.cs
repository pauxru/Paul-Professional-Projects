using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public sealed class RelevanceEvaluationHarness(SearchEngine engine, IClock clock)
{
    public EvaluationReport Run(string index, IReadOnlyList<RelevanceJudgement> judgements, IReadOnlyCollection<string>? groups = null)
    {
        var metrics = new Dictionary<RetrievalMode, EvaluationMetrics>();
        foreach (var mode in new[] { RetrievalMode.Keyword, RetrievalMode.Vector, RetrievalMode.HybridRrf, RetrievalMode.HybridLinear })
        {
            metrics[mode] = EvaluateMode(index, judgements, mode, groups);
        }
        return new EvaluationReport(metrics, judgements.Select(judgement => judgement.Query).Distinct(StringComparer.OrdinalIgnoreCase).Count(), clock.UtcNow);
    }

    private EvaluationMetrics EvaluateMode(string index, IReadOnlyList<RelevanceJudgement> judgements, RetrievalMode mode, IReadOnlyCollection<string>? groups)
    {
        var perQuery = judgements.GroupBy(judgement => judgement.Query, StringComparer.OrdinalIgnoreCase).ToArray();
        var ndcg = 0d;
        var mrr = 0d;
        var precision = 0d;
        var recall = 0d;
        foreach (var group in perQuery)
        {
            var result = engine.Search(new SearchRequest(index, QueryText: group.Key, Mode: mode, CallerGroups: groups, Size: 10));
            var relevance = group.ToDictionary(judgement => judgement.DocumentId, judgement => judgement.Grade, StringComparer.Ordinal);
            var returned = result.Hits.Select(hit => hit.Document.Id).ToArray();
            var dcg = returned.Select((id, offset) => relevance.TryGetValue(id, out var grade) ? (Math.Pow(2d, grade) - 1d) / Math.Log2(offset + 2d) : 0d).Sum();
            var ideal = relevance.Values.OrderByDescending(grade => grade).Take(10).Select((grade, offset) => (Math.Pow(2d, grade) - 1d) / Math.Log2(offset + 2d)).Sum();
            ndcg += ideal == 0d ? 0d : dcg / ideal;
            var first = returned.Select((id, offset) => (id, offset)).FirstOrDefault(item => relevance.ContainsKey(item.id));
            if (first.id is not null) mrr += 1d / (first.offset + 1d);
            var relevantReturned = returned.Count(relevance.ContainsKey);
            precision += relevantReturned / 10d;
            recall += relevance.Count == 0 ? 0d : (double)relevantReturned / relevance.Count;
        }
        var count = Math.Max(1, perQuery.Length);
        return new EvaluationMetrics(ndcg / count, mrr / count, precision / count, recall / count);
    }
}

public static class GoldenEvaluationSet
{
    public static IReadOnlyList<RelevanceJudgement> Create() => Enumerable.Range(1, 30)
        .Select(number => new RelevanceJudgement($"qtoken{number:D2}", $"catalogue-golden-{number:D2}", 3, "catalogue"))
        .ToArray();
}
