using RagAssistant.Application.Answering;
using RagAssistant.Application.Retrieval;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Application.Evaluation;

public sealed record GoldenExample(
    string Id,
    string Question,
    IReadOnlyList<string> ExpectedDocumentTitles,
    bool ShouldRefuse,
    UserPrincipal AskedBy,
    IReadOnlyList<string>? AcceptableAlternates = null,
    string? Tag = null);

public sealed record RetrievalMetrics(
    string Mode,
    double RecallAtK,
    double MeanReciprocalRank,
    double NdcgAtK,
    int K);

public sealed record RetrievalSliceMetrics(
    string Slice,
    IReadOnlyList<RetrievalMetrics> RetrievalMetrics,
    int Examples);

public sealed record EvaluationSummary(
    IReadOnlyList<RetrievalMetrics> RetrievalMetrics,
    double CitationPrecision,
    double CitationRecall,
    double AnyCorrectCitationRate,
    double RefusalAccuracy,
    int Examples,
    IReadOnlyList<RetrievalSliceMetrics> Slices);

public sealed class EvaluationHarness
{
    private readonly IRetriever _retriever;
    private readonly AnsweringService _answering;
    private readonly Func<Guid, string?> _titleLookup;

    public EvaluationHarness(IRetriever retriever, AnsweringService answering, Func<Guid, string?> titleLookup)
    {
        _retriever = retriever;
        _answering = answering;
        _titleLookup = titleLookup;
    }

    public async Task<EvaluationSummary> RunAsync(
        IReadOnlyList<GoldenExample> examples,
        int k,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(examples);
        if (examples.Count == 0)
        {
            return new EvaluationSummary(
                Array.Empty<RetrievalMetrics>(), 0, 0, 0, 0, 0, Array.Empty<RetrievalSliceMetrics>());
        }

        var modes = new[] { RetrievalMode.Keyword, RetrievalMode.Dense, RetrievalMode.Hybrid };
        var metrics = new List<RetrievalMetrics>();
        foreach (var mode in modes)
        {
            metrics.Add(await MeasureRetrievalAsync(examples, mode, k, ct).ConfigureAwait(false));
        }

        var slices = new List<RetrievalSliceMetrics>();
        foreach (var tag in examples.Where(e => !string.IsNullOrEmpty(e.Tag)).Select(e => e.Tag!).Distinct())
        {
            var subset = examples.Where(e => e.Tag == tag).ToArray();
            var sliceMetrics = new List<RetrievalMetrics>();
            foreach (var mode in modes)
            {
                sliceMetrics.Add(await MeasureRetrievalAsync(subset, mode, k, ct).ConfigureAwait(false));
            }

            slices.Add(new RetrievalSliceMetrics(tag, sliceMetrics, subset.Length));
        }

        var precisionSum = 0.0;
        var recallSum = 0.0;
        var anyCorrect = 0;
        var refusalCorrect = 0;
        var answerableCount = 0;
        foreach (var example in examples)
        {
            var answer = await _answering.AnswerAsync(
                new AnswerRequest(example.Question, example.AskedBy, RetrievalMode.Hybrid, k),
                ct).ConfigureAwait(false);

            if (example.ShouldRefuse)
            {
                if (answer.Refused)
                {
                    refusalCorrect++;
                }
            }
            else
            {
                refusalCorrect += answer.Refused ? 0 : 1;
                if (!answer.Refused && answer.Citations.Count > 0)
                {
                    var citedTitles = answer.Citations
                        .Select(c => c.DocumentTitle)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var expectedSet = new HashSet<string>(example.ExpectedDocumentTitles, StringComparer.OrdinalIgnoreCase);
                    var acceptedSet = new HashSet<string>(example.ExpectedDocumentTitles, StringComparer.OrdinalIgnoreCase);
                    if (example.AcceptableAlternates is { Count: > 0 })
                    {
                        foreach (var alt in example.AcceptableAlternates)
                        {
                            acceptedSet.Add(alt);
                        }
                    }

                    var relevant = citedTitles.Count(acceptedSet.Contains);
                    var precision = (double)relevant / citedTitles.Length;
                    precisionSum += precision;

                    var expectedCovered = expectedSet.Count == 0
                        ? 0.0
                        : (double)expectedSet.Count(t => citedTitles.Contains(t, StringComparer.OrdinalIgnoreCase))
                          / expectedSet.Count;
                    recallSum += expectedCovered;
                    if (relevant > 0)
                    {
                        anyCorrect++;
                    }

                    answerableCount++;
                }
            }
        }

        var citationPrecision = answerableCount == 0 ? 0.0 : precisionSum / answerableCount;
        var citationRecall = answerableCount == 0 ? 0.0 : recallSum / answerableCount;
        var anyCorrectRate = answerableCount == 0 ? 0.0 : (double)anyCorrect / answerableCount;
        var refusalAccuracy = (double)refusalCorrect / examples.Count;

        return new EvaluationSummary(
            metrics,
            citationPrecision,
            citationRecall,
            anyCorrectRate,
            refusalAccuracy,
            examples.Count,
            slices);
    }

    private async Task<RetrievalMetrics> MeasureRetrievalAsync(
        IReadOnlyList<GoldenExample> examples,
        RetrievalMode mode,
        int k,
        CancellationToken ct)
    {
        double recallSum = 0, mrrSum = 0, ndcgSum = 0;
        var applicable = 0;
        foreach (var example in examples)
        {
            if (example.ShouldRefuse || example.ExpectedDocumentTitles.Count == 0)
            {
                continue;
            }

            applicable++;
            var result = await _retriever.RetrieveAsync(
                new RetrievalRequest(example.Question, example.AskedBy, mode, k, Math.Max(k * 4, 16)),
                ct).ConfigureAwait(false);
            var titles = result.Chunks
                .Select(c => c.DocumentTitle)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var hits = titles
                .Count(t => example.ExpectedDocumentTitles.Contains(t, StringComparer.OrdinalIgnoreCase));
            var recall = (double)hits / example.ExpectedDocumentTitles.Count;
            recallSum += recall;

            var rankIndex = -1;
            for (var i = 0; i < titles.Length; i++)
            {
                if (example.ExpectedDocumentTitles.Contains(titles[i], StringComparer.OrdinalIgnoreCase))
                {
                    rankIndex = i;
                    break;
                }
            }

            mrrSum += rankIndex >= 0 ? 1.0 / (rankIndex + 1) : 0.0;
            ndcgSum += ComputeNdcg(titles, example.ExpectedDocumentTitles, k);
        }

        if (applicable == 0)
        {
            return new RetrievalMetrics(mode.ToString(), 0, 0, 0, k);
        }

        return new RetrievalMetrics(
            mode.ToString(),
            recallSum / applicable,
            mrrSum / applicable,
            ndcgSum / applicable,
            k);
    }

    private static double ComputeNdcg(IReadOnlyList<string> retrievedTitles, IReadOnlyList<string> expected, int k)
    {
        var expectedSet = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
        double dcg = 0;
        for (var i = 0; i < Math.Min(retrievedTitles.Count, k); i++)
        {
            var rel = expectedSet.Contains(retrievedTitles[i]) ? 1.0 : 0.0;
            dcg += rel / Math.Log2(i + 2);
        }

        double idcg = 0;
        for (var i = 0; i < Math.Min(expectedSet.Count, k); i++)
        {
            idcg += 1.0 / Math.Log2(i + 2);
        }

        return idcg <= 0 ? 0 : dcg / idcg;
    }
}
