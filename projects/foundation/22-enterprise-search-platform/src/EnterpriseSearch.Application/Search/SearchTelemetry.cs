using System.Diagnostics.Metrics;

namespace EnterpriseSearch.Application.Search;

public static class SearchTelemetry
{
    public const string MeterName = "EnterpriseSearch.Engine";
    private static readonly Meter Meter = new(MeterName);
    private static long _indexDocuments;
    public static readonly Histogram<double> QueryLatencyMilliseconds = Meter.CreateHistogram<double>("search.query.latency", "ms");
    public static readonly Counter<long> ZeroResultQueries = Meter.CreateCounter<long>("search.zero_result.count");
    public static readonly Counter<long> IndexedDocuments = Meter.CreateCounter<long>("search.indexed_documents.count");
    public static readonly ObservableGauge<long> IndexSize = Meter.CreateObservableGauge("search.index.document_count", () => Interlocked.Read(ref _indexDocuments));

    public static void RecordSearch(TimeSpan took, long results)
    {
        QueryLatencyMilliseconds.Record(took.TotalMilliseconds);
        if (results == 0) ZeroResultQueries.Add(1);
    }

    public static void SetIndexSize(long documentCount) => Interlocked.Exchange(ref _indexDocuments, documentCount);
}
