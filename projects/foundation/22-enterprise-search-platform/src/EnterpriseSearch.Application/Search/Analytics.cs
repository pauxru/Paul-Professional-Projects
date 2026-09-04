using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public sealed class InMemorySearchAnalytics : ISearchAnalytics
{
    private readonly object _gate = new();
    private readonly List<QueryLog> _queries = [];
    private readonly List<ClickEvent> _clicks = [];

    public void LogQuery(QueryLog query)
    {
        lock (_gate) _queries.Add(query);
    }

    public void LogClick(ClickEvent click)
    {
        lock (_gate) _clicks.Add(click);
    }

    public double GetClickBoost(string index, string query, string documentId)
    {
        lock (_gate)
        {
            var clicks = _clicks.Count(click => string.Equals(click.Index, index, StringComparison.OrdinalIgnoreCase)
                && string.Equals(click.Query, query, StringComparison.OrdinalIgnoreCase)
                && string.Equals(click.DocumentId, documentId, StringComparison.Ordinal));
            return Math.Log(1d + clicks);
        }
    }

    public AnalyticsSummary GetSummary()
    {
        lock (_gate)
        {
            var total = _queries.Count;
            var zeroRate = total == 0 ? 0d : (double)_queries.Count(query => query.ResultCount == 0) / total;
            var ctr = Enumerable.Range(1, 10).Select(position =>
            {
                var impressions = _queries.Sum(query => query.ResultsShown is { } shown
                    ? shown.Count >= position ? 1 : 0
                    : Math.Clamp(query.ResultCount - position + 1, 0, 1));
                var clicks = _clicks.Count(click => click.Position == position);
                return new PositionCtr(position, impressions, clicks, impressions == 0 ? 0d : (double)clicks / impressions);
            }).Where(row => row.Impressions > 0 || row.Clicks > 0).ToArray();
            var top = _queries.GroupBy(query => query.Query, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Query: group.Key, Count: (long)group.Count()))
                .OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Query, StringComparer.Ordinal).Take(10).ToArray();
            return new AnalyticsSummary(zeroRate, ctr, top);
        }
    }
}
