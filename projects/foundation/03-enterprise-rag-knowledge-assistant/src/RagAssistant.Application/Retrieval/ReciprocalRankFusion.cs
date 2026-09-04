namespace RagAssistant.Application.Retrieval;

public static class ReciprocalRankFusion
{
    public static IReadOnlyList<(TKey Id, double Score)> Fuse<TKey>(
        IEnumerable<IReadOnlyList<TKey>> rankings,
        int k = 60)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rankings);
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k));
        }

        var scores = new Dictionary<TKey, double>();
        foreach (var ranking in rankings)
        {
            if (ranking is null)
            {
                continue;
            }

            for (var rank = 0; rank < ranking.Count; rank++)
            {
                var id = ranking[rank];
                var contribution = 1.0 / (k + rank + 1);
                scores[id] = scores.TryGetValue(id, out var s) ? s + contribution : contribution;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToArray();
    }
}
