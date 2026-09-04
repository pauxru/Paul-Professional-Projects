using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public sealed class SearchEngine(
    SearchCluster cluster,
    QueryStringParser parser,
    IScorer scorer,
    FunctionScorer functionScorer,
    ISearchAnalytics analytics,
    IClock clock,
    QueryLimits limits)
{
    private sealed class MatchData
    {
        public MatchData(double score = 0d, IEnumerable<TermScoreExplanation>? terms = null)
        {
            Score = score;
            Terms = terms?.ToList() ?? [];
        }
        public double Score { get; set; }
        public List<TermScoreExplanation> Terms { get; }
        public MatchData Clone() => new(Score, Terms);
    }

    public SearchResponse Search(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var stopwatch = Stopwatch.StartNew();
        var clause = request.Query ?? parser.Parse(request.QueryText ?? string.Empty);
        var index = cluster.GetIndex(request.Index).Index;
        var matches = Evaluate(index, clause, cancellationToken);
        var groups = request.CallerGroups ?? Array.Empty<string>();
        var eligibleIds = index.GetDocuments().Where(document => document.IsAllowedFor(groups) && MatchesFilters(document, request.Filters)).Select(document => document.Id).ToHashSet(StringComparer.Ordinal);
        var authorizedMatches = matches.Where(pair => eligibleIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var lexical = authorizedMatches;
        var vector = BuildVectorMatches(index, request, eligibleIds, cancellationToken);
        var combined = Combine(request.Mode, lexical, vector);
        var queryLabel = request.QueryText ?? Describe(clause);

        var scored = new List<(SearchDocument Document, double Score, MatchData? Lexical, FunctionScoreBreakdown Function)>();
        foreach (var (documentId, baseScore) in combined)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!index.TryGetDocument(documentId, out var document) || document is null) continue;
            var function = functionScorer.Score(document, index.Name, queryLabel);
            var score = baseScore * function.Multiplier + function.ClickBoost;
            if (request.CrossFeatureRerank)
            {
                score += document.IsInStock ? 0.025d : 0d;
                if (!string.IsNullOrWhiteSpace(request.QueryText) && document.Fields.TryGetValue("title", out var title) && title.Contains(request.QueryText, StringComparison.OrdinalIgnoreCase)) score += 0.025d;
            }
            scored.Add((document, score, lexical.TryGetValue(documentId, out var detail) ? detail : null, function));
        }

        var ordered = scored.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Document.Id, StringComparer.Ordinal).ToArray();
        var after = DecodeSearchAfter(request.SearchAfter);
        IEnumerable<(SearchDocument Document, double Score, MatchData? Lexical, FunctionScoreBreakdown Function)> pageable = ordered;
        if (after is not null)
        {
            pageable = pageable.Where(hit => hit.Score < after.Value.Score || (Math.Abs(hit.Score - after.Value.Score) < 1e-12 && string.CompareOrdinal(hit.Document.Id, after.Value.DocumentId) > 0));
        }
        else if (request.From > 0)
        {
            pageable = pageable.Skip(request.From);
        }
        var page = pageable.Take(request.Size).ToArray();
        var terms = ExtractTerms(clause).ToHashSet(StringComparer.Ordinal);
        var hits = page.Select(hit => new SearchHit(
            hit.Document,
            hit.Score,
            request.Explain ? new ScoreExplanation(hit.Lexical?.Terms ?? [], hit.Function.Multiplier, hit.Function.ClickBoost, hit.Function.Summary) : null,
            BuildHighlights(index, hit.Document, request.HighlightFields, terms))).ToArray();
        var facets = BuildFacets(index, matches, request, groups);
        stopwatch.Stop();
        analytics.LogQuery(new QueryLog(index.Name, queryLabel, ordered.Length, clock.UtcNow, hits.Select(hit => hit.Document.Id).ToArray()));
        SearchTelemetry.RecordSearch(stopwatch.Elapsed, ordered.Length);
        var didYouMean = ordered.Length == 0 && !string.IsNullOrWhiteSpace(request.QueryText) ? index.DidYouMean(request.QueryText) : null;
        var remaining = pageable.Skip(request.Size).Any();
        var next = remaining && page.Length > 0 ? EncodeSearchAfter(page[^1].Score, page[^1].Document.Id) : null;
        return new SearchResponse(hits, ordered.Length, facets, didYouMean, next, stopwatch.Elapsed);
    }

    public IReadOnlyList<string> FindDocumentIds(string indexName, SearchClause clause, IReadOnlyCollection<string>? callerGroups = null, CancellationToken cancellationToken = default)
    {
        var index = cluster.GetIndex(indexName).Index;
        var groups = callerGroups ?? Array.Empty<string>();
        return Evaluate(index, clause, cancellationToken)
            .Where(pair => index.TryGetDocument(pair.Key, out var document) && document is not null && document.IsAllowedFor(groups))
            .Select(pair => pair.Key).ToArray();
    }

    public void LogClick(ClickEvent click) => analytics.LogClick(click);
    public AnalyticsSummary GetAnalytics() => analytics.GetSummary();

    private void ValidateRequest(SearchRequest request)
    {
        if (request.Size is < 1 or > 100 || request.Size > limits.MaxPageSize) throw new QueryValidationException($"size must be between 1 and {limits.MaxPageSize}.");
        if (request.From < 0 || request.From > limits.MaxFrom) throw new QueryValidationException($"from must be between 0 and {limits.MaxFrom}; use search_after for deep pagination.");
        if (request.SearchAfter is not null && request.From != 0) throw new QueryValidationException("from and search_after cannot be used together.");
        if (request.Query is not null && CountClauses(request.Query) > limits.MaxClauses) throw new QueryValidationException($"Query has more than {limits.MaxClauses} clauses.");
    }

    private Dictionary<string, MatchData> Evaluate(InvertedIndex index, SearchClause clause, CancellationToken cancellationToken) => clause switch
    {
        MatchAllClause => index.GetDocuments().ToDictionary(document => document.Id, _ => new MatchData(), StringComparer.Ordinal),
        TermClause term => EvaluateTerm(index, term.Field, term.Term, 1d, cancellationToken),
        PhraseClause phrase => EvaluatePhrase(index, phrase, cancellationToken),
        PrefixClause prefix => EvaluateExpanded(index, prefix.Field, ExpandPrefix(index, prefix.Field, prefix.Prefix), cancellationToken),
        WildcardClause wildcard => EvaluateExpanded(index, wildcard.Field, ExpandWildcard(index, wildcard.Field, wildcard.Pattern), cancellationToken),
        FuzzyClause fuzzy => EvaluateExpanded(index, fuzzy.Field, ExpandFuzzy(index, fuzzy.Field, fuzzy.Term, fuzzy.MaxEdits), cancellationToken),
        RangeClause range => EvaluateRange(index, range, cancellationToken),
        MultiFieldClause multiField => EvaluateMultiField(index, multiField, cancellationToken),
        BooleanClause boolean => EvaluateBoolean(index, boolean, cancellationToken),
        _ => throw new QueryValidationException("Unsupported query clause.")
    };

    private Dictionary<string, MatchData> EvaluateTerm(InvertedIndex index, string? requestedField, string rawTerm, double additionalBoost, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MatchData>(StringComparer.Ordinal);
        foreach (var field in FieldsFor(index, requestedField))
        {
            var analyzedTerms = index.Analyze(field, rawTerm).Where(token => token.Type is "WORD" or "SYNONYM" or null).Select(token => token.Term).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var term in analyzedTerms)
            {
                foreach (var (documentId, posting) in index.GetPostings(field, term))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var explanation = scorer.ScoreTerm(index, documentId, field, term, posting, additionalBoost);
                    Add(result, documentId, explanation.Contribution, explanation);
                }
            }
        }
        return result;
    }

    private Dictionary<string, MatchData> EvaluatePhrase(InvertedIndex index, PhraseClause clause, CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, MatchData>(StringComparer.Ordinal);
        foreach (var field in FieldsFor(index, clause.Field))
        {
            var analyzed = index.Analyze(field, string.Join(' ', clause.Terms)).Where(token => token.Type is "WORD" or null).ToArray();
            if (analyzed.Length == 0) continue;
            var terms = analyzed.Select(token => token.Term).ToArray();
            var expectedPositions = clause.Positions?.ToArray() ?? analyzed.Select(token => token.Position).ToArray();
            var postings = terms.Select(term => index.GetPostings(field, term)).ToArray();
            if (postings.Any(posting => posting.Count == 0)) continue;
            var candidates = postings.Select(posting => (IEnumerable<string>)posting.Keys).Aggregate((current, next) => current.Intersect(next, StringComparer.Ordinal)).ToArray();
            foreach (var documentId in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var positionLists = postings.Select(posting => posting[documentId].Positions).ToArray();
                if (!MatchesPhrase(positionLists, expectedPositions, clause.Slop)) continue;
                foreach (var term in terms.Distinct(StringComparer.Ordinal))
                {
                    var posting = index.GetPostings(field, term)[documentId];
                    var termExplanation = scorer.ScoreTerm(index, documentId, field, term, posting);
                    var explanation = termExplanation with { Contribution = termExplanation.Contribution * 1.15d };
                    Add(output, documentId, explanation.Contribution, explanation);
                }
            }
        }
        return output;
    }

    private Dictionary<string, MatchData> EvaluateExpanded(InvertedIndex index, string? field, IReadOnlyList<string> terms, CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, MatchData>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            foreach (var (id, detail) in EvaluateTerm(index, field, term, 1d, cancellationToken))
            {
                Add(output, id, detail.Score, detail.Terms);
            }
        }
        return output;
    }

    private Dictionary<string, MatchData> EvaluateRange(InvertedIndex index, RangeClause clause, CancellationToken cancellationToken)
    {
        return index.GetDocuments().Where(document =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!document.NumericFields.TryGetValue(clause.Field, out var value)) return false;
            var lower = clause.Lower is null || (clause.IncludeLower ? value >= clause.Lower : value > clause.Lower);
            var upper = clause.Upper is null || (clause.IncludeUpper ? value <= clause.Upper : value < clause.Upper);
            return lower && upper;
        }).ToDictionary(document => document.Id, _ => new MatchData(), StringComparer.Ordinal);
    }

    private Dictionary<string, MatchData> EvaluateMultiField(InvertedIndex index, MultiFieldClause clause, CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, MatchData>(StringComparer.Ordinal);
        foreach (var (field, boost) in clause.Fields)
        {
            foreach (var (documentId, detail) in EvaluateTerm(index, field, clause.Text, boost, cancellationToken)) Add(output, documentId, detail.Score, detail.Terms);
        }
        return output;
    }

    private Dictionary<string, MatchData> EvaluateBoolean(InvertedIndex index, BooleanClause clause, CancellationToken cancellationToken)
    {
        Dictionary<string, MatchData>? current = null;
        if (clause.Must is not null)
        {
            foreach (var child in clause.Must)
            {
                var matches = Evaluate(index, child, cancellationToken);
                current = current is null ? Clone(matches) : Intersect(current, matches, true);
            }
        }

        var shouldMatches = new Dictionary<string, (MatchData Detail, int Count)>(StringComparer.Ordinal);
        if (clause.Should is not null)
        {
            foreach (var child in clause.Should)
            {
                foreach (var (documentId, detail) in Evaluate(index, child, cancellationToken))
                {
                    if (!shouldMatches.TryGetValue(documentId, out var existing)) shouldMatches[documentId] = (detail.Clone(), 1);
                    else
                    {
                        Add(existing.Detail, detail.Score, detail.Terms);
                        shouldMatches[documentId] = (existing.Detail, existing.Count + 1);
                    }
                }
            }
            var required = clause.MinimumShouldMatch > 0 ? clause.MinimumShouldMatch : current is null ? 1 : 0;
            if (current is null)
            {
                current = shouldMatches.Where(pair => pair.Value.Count >= required).ToDictionary(pair => pair.Key, pair => pair.Value.Detail, StringComparer.Ordinal);
            }
            else
            {
                if (required > 0) current = current.Where(pair => shouldMatches.TryGetValue(pair.Key, out var candidate) && candidate.Count >= required).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                foreach (var (documentId, detail) in current)
                {
                    if (shouldMatches.TryGetValue(documentId, out var optional)) Add(detail, optional.Detail.Score, optional.Detail.Terms);
                }
            }
        }

        if (current is null) current = Evaluate(index, new MatchAllClause(), cancellationToken);
        if (clause.Filter is not null)
        {
            foreach (var child in clause.Filter) current = Intersect(current, Evaluate(index, child, cancellationToken), false);
        }
        if (clause.MustNot is not null)
        {
            foreach (var child in clause.MustNot)
            {
                foreach (var documentId in Evaluate(index, child, cancellationToken).Keys) current.Remove(documentId);
            }
        }
        return current;
    }

    private IReadOnlyList<string> ExpandPrefix(InvertedIndex index, string? field, string prefix)
    {
        if (prefix.Length < 2) throw new QueryValidationException("Prefix queries must contain at least two non-wildcard characters.");
        return Expand(index, field, term => term.StartsWith(prefix, StringComparison.Ordinal));
    }

    private IReadOnlyList<string> ExpandWildcard(InvertedIndex index, string? field, string pattern)
    {
        if (pattern.Length > 64) throw new QueryValidationException("Wildcard pattern exceeds the 64-character limit.");
        if (pattern.Count(character => character is '*' or '?') > 8) throw new QueryValidationException("Wildcard query has too many wildcard operators.");
        return Expand(index, field, term => WildcardMatches(pattern, term));
    }

    private IReadOnlyList<string> ExpandFuzzy(InvertedIndex index, string? field, string term, int maxEdits)
    {
        if (maxEdits is < 0 or > 2) throw new QueryValidationException("Fuzzy edit distance must be between 0 and 2.");
        if (term.Length > 64) throw new QueryValidationException("Fuzzy term exceeds the 64-character limit.");
        var fields = FieldsFor(index, field);
        var candidates = fields.SelectMany(index.GetTerms).Distinct(StringComparer.Ordinal).Take(limits.MaxFuzzyCandidates).Where(candidate => InvertedIndex.EditDistance(term, candidate, maxEdits) <= maxEdits).ToArray();
        return candidates;
    }

    private IReadOnlyList<string> Expand(InvertedIndex index, string? field, Func<string, bool> predicate)
    {
        var terms = FieldsFor(index, field).SelectMany(index.GetTerms).Distinct(StringComparer.Ordinal).Where(predicate).Take(limits.MaxWildcardExpansion + 1).ToArray();
        if (terms.Length > limits.MaxWildcardExpansion) throw new QueryValidationException($"Query expands to more than {limits.MaxWildcardExpansion} terms.");
        return terms;
    }

    private static bool MatchesPhrase(IReadOnlyList<int>[] positions, IReadOnlyList<int> expected, int slop)
    {
        if (positions.Length == 0 || expected.Count != positions.Length) return false;
        foreach (var first in positions[0])
        {
            var previous = first;
            var matched = true;
            for (var index = 1; index < positions.Length; index++)
            {
                var candidate = positions[index].Where(position => position > previous).DefaultIfEmpty(-1).First();
                if (candidate <= previous) { matched = false; break; }
                previous = candidate;
            }
            if (!matched) continue;
            var expectedSpan = expected[^1] - expected[0];
            var extraDistance = previous - first - expectedSpan;
            if (extraDistance >= 0 && extraDistance <= slop) return true;
        }
        return false;
    }

    private static Dictionary<string, MatchData> Clone(IReadOnlyDictionary<string, MatchData> source) => source.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);

    private static Dictionary<string, MatchData> Intersect(Dictionary<string, MatchData> left, IReadOnlyDictionary<string, MatchData> right, bool score)
    {
        var result = new Dictionary<string, MatchData>(StringComparer.Ordinal);
        foreach (var (documentId, detail) in left)
        {
            if (!right.TryGetValue(documentId, out var matching)) continue;
            var merged = detail.Clone();
            if (score) Add(merged, matching.Score, matching.Terms);
            result[documentId] = merged;
        }
        return result;
    }

    private static void Add(Dictionary<string, MatchData> map, string documentId, double score, TermScoreExplanation explanation)
    {
        if (!map.TryGetValue(documentId, out var detail)) map[documentId] = detail = new MatchData();
        Add(detail, score, [explanation]);
    }

    private static void Add(Dictionary<string, MatchData> map, string documentId, double score, IEnumerable<TermScoreExplanation> explanations)
    {
        if (!map.TryGetValue(documentId, out var detail)) map[documentId] = detail = new MatchData();
        Add(detail, score, explanations);
    }

    private static void Add(MatchData destination, double score, IEnumerable<TermScoreExplanation> explanations)
    {
        destination.Score += score;
        destination.Terms.AddRange(explanations);
    }

    private static IEnumerable<string> FieldsFor(InvertedIndex index, string? field)
    {
        if (!string.IsNullOrWhiteSpace(field)) return [field];
        var configured = index.Definition.Fields.Values.Where(definition => definition.Searchable).Select(definition => definition.Name).ToArray();
        return configured.Length > 0 ? configured : index.GetDocuments().SelectMany(document => document.Fields.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private Dictionary<string, double> BuildVectorMatches(InvertedIndex index, SearchRequest request, IEnumerable<string> authorizedIds, CancellationToken cancellationToken)
    {
        if (request.Mode == RetrievalMode.Keyword) return new Dictionary<string, double>(StringComparer.Ordinal);
        var text = request.QueryText ?? string.Join(' ', ExtractTerms(request.Query ?? new MatchAllClause()));
        if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, double>(StringComparer.Ordinal);
        var vector = new DeterministicEmbeddingModel(new TextAnalyzer()).Embed(text);
        var candidates = request.UseApproximateVector ? index.ApproximateVectorIndex : index.ExactVectorIndex;
        var allowed = authorizedIds.ToHashSet(StringComparer.Ordinal);
        return candidates.Search(vector, Math.Max(request.Size * 8, 100), cancellationToken)
            .Where(hit => allowed.Contains(hit.DocumentId)).ToDictionary(hit => hit.DocumentId, hit => hit.Score, StringComparer.Ordinal);
    }

    private static Dictionary<string, double> Combine(RetrievalMode mode, IReadOnlyDictionary<string, MatchData> lexical, IReadOnlyDictionary<string, double> vector)
    {
        return mode switch
        {
            RetrievalMode.Keyword => lexical.ToDictionary(pair => pair.Key, pair => pair.Value.Score, StringComparer.Ordinal),
            RetrievalMode.Vector => vector.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            RetrievalMode.HybridRrf => ReciprocalRankFusion(lexical.ToDictionary(pair => pair.Key, pair => pair.Value.Score, StringComparer.Ordinal), vector),
            RetrievalMode.HybridLinear => LinearFusion(lexical.ToDictionary(pair => pair.Key, pair => pair.Value.Score, StringComparer.Ordinal), vector),
            _ => throw new QueryValidationException("Unsupported retrieval mode.")
        };
    }

    public static Dictionary<string, double> ReciprocalRankFusion(IReadOnlyDictionary<string, double> lexical, IReadOnlyDictionary<string, double> vector, int k = 60)
    {
        var output = new Dictionary<string, double>(StringComparer.Ordinal);
        AddRanks(lexical, 2d);
        AddRanks(vector, 1d);
        return output;

        void AddRanks(IReadOnlyDictionary<string, double> ranked, double weight)
        {
            var rank = 1;
            foreach (var (documentId, _) in ranked.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                output[documentId] = output.TryGetValue(documentId, out var score) ? score + weight / (k + rank) : weight / (k + rank);
                rank++;
            }
        }
    }

    public static Dictionary<string, double> LinearFusion(IReadOnlyDictionary<string, double> lexical, IReadOnlyDictionary<string, double> vector, double lexicalWeight = 0.6d)
    {
        var output = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (documentId, score) in Normalize(lexical)) output[documentId] = score * lexicalWeight;
        foreach (var (documentId, score) in Normalize(vector)) output[documentId] = output.TryGetValue(documentId, out var current) ? current + score * (1d - lexicalWeight) : score * (1d - lexicalWeight);
        return output;
    }

    private static IEnumerable<KeyValuePair<string, double>> Normalize(IReadOnlyDictionary<string, double> scores)
    {
        if (scores.Count == 0) return [];
        var min = scores.Values.Min();
        var max = scores.Values.Max();
        return scores.Select(pair => new KeyValuePair<string, double>(pair.Key, max - min < 1e-12 ? 1d : (pair.Value - min) / (max - min)));
    }

    private IReadOnlyDictionary<string, FacetResult> BuildFacets(InvertedIndex index, IReadOnlyDictionary<string, MatchData> queryMatches, SearchRequest request, IReadOnlyCollection<string> groups)
    {
        if (request.Facets is null || request.Facets.Count == 0) return new Dictionary<string, FacetResult>(StringComparer.OrdinalIgnoreCase);
        var facets = new Dictionary<string, FacetResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var facet in request.Facets)
        {
            var withoutOwn = request.Filters?.Where(filter => !string.Equals(filter.Key, facet.Field, StringComparison.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var documents = queryMatches.Keys
                .Select(id => index.TryGetDocument(id, out var document) ? document : null)
                .Where(document => document is not null && document.IsAllowedFor(groups) && MatchesFilters(document, withoutOwn))
                .Cast<SearchDocument>().ToArray();
            facets[facet.Name] = new FacetResult(facet.Name, BuildBuckets(facet, documents));
        }
        return facets;
    }

    private static IReadOnlyList<FacetBucket> BuildBuckets(FacetRequest facet, IEnumerable<SearchDocument> documents)
    {
        return facet.Kind switch
        {
            FacetKind.Range => (facet.Ranges ?? []).Select(range => new FacetBucket(range.Label, documents.Count(document => document.NumericFields.TryGetValue(facet.Field, out var value) && IsInRange(value, range)))).ToArray(),
            FacetKind.Hierarchical => documents.SelectMany(document => document.GetValues(facet.Field)).SelectMany(value => Hierarchy(value)).GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Select(group => new FacetBucket(group.Key, group.LongCount())).OrderByDescending(bucket => bucket.Count).ThenBy(bucket => bucket.Key, StringComparer.Ordinal).Take(facet.Size).ToArray(),
            _ => documents.SelectMany(document => document.GetValues(facet.Field).Distinct(StringComparer.OrdinalIgnoreCase)).GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Select(group => new FacetBucket(group.Key, group.LongCount())).OrderByDescending(bucket => bucket.Count).ThenBy(bucket => bucket.Key, StringComparer.Ordinal).Take(facet.Size).ToArray()
        };
    }

    private static bool IsInRange(decimal value, NumericRange range) => (range.From is null || (range.IncludeFrom ? value >= range.From : value > range.From)) && (range.To is null || (range.IncludeTo ? value <= range.To : value < range.To));
    private static IEnumerable<string> Hierarchy(string value)
    {
        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var level = 1; level <= parts.Length; level++) yield return string.Join('/', parts.Take(level));
    }

    private static bool MatchesFilters(SearchDocument document, IReadOnlyDictionary<string, IReadOnlyList<string>>? filters)
    {
        if (filters is null || filters.Count == 0) return true;
        foreach (var (field, values) in filters)
        {
            if (values.Count == 0) continue;
            var textMatch = document.GetValues(field).Any(value => values.Contains(value, StringComparer.OrdinalIgnoreCase));
            var numericMatch = document.NumericFields.TryGetValue(field, out var numeric) && values.Any(value => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var expected) && expected == numeric);
            if (!textMatch && !numericMatch) return false;
        }
        return true;
    }

    private static IReadOnlyDictionary<string, string>? BuildHighlights(InvertedIndex index, SearchDocument document, IReadOnlyList<string>? requestedFields, IReadOnlySet<string> terms)
    {
        if (terms.Count == 0) return null;
        var fields = requestedFields is { Count: > 0 } ? requestedFields : document.Fields.Keys.ToArray();
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!document.Fields.TryGetValue(field, out var text)) continue;
            var offsets = index.Analyze(field, text).Where(token => terms.Contains(token.Term)).Select(token => (token.StartOffset, token.EndOffset)).Distinct().OrderBy(offset => offset.StartOffset).ToArray();
            if (offsets.Length == 0) continue;
            var builder = new StringBuilder();
            var cursor = 0;
            foreach (var (start, end) in offsets)
            {
                if (start < cursor || end > text.Length) continue;
                builder.Append(HtmlEncoder.Default.Encode(text[cursor..start]));
                builder.Append("<em>").Append(HtmlEncoder.Default.Encode(text[start..end])).Append("</em>");
                cursor = end;
            }
            builder.Append(HtmlEncoder.Default.Encode(text[cursor..]));
            output[field] = builder.ToString();
        }
        return output.Count == 0 ? null : output;
    }

    private static IReadOnlyList<string> ExtractTerms(SearchClause clause) => clause switch
    {
        TermClause term => [term.Term],
        PhraseClause phrase => phrase.Terms,
        PrefixClause prefix => [prefix.Prefix],
        WildcardClause wildcard => [wildcard.Pattern.Trim('*', '?')],
        FuzzyClause fuzzy => [fuzzy.Term],
        MultiFieldClause multi => [multi.Text],
        BooleanClause boolean => (boolean.Must ?? []).Concat(boolean.Should ?? []).Concat(boolean.MustNot ?? []).Concat(boolean.Filter ?? []).SelectMany(ExtractTerms).Where(term => !string.IsNullOrWhiteSpace(term)).ToArray(),
        _ => []
    };

    private static int CountClauses(SearchClause clause) => clause switch
    {
        BooleanClause boolean => 1 + (boolean.Must?.Sum(CountClauses) ?? 0) + (boolean.Should?.Sum(CountClauses) ?? 0) + (boolean.MustNot?.Sum(CountClauses) ?? 0) + (boolean.Filter?.Sum(CountClauses) ?? 0),
        _ => 1
    };

    private static string Describe(SearchClause clause) => string.Join(' ', ExtractTerms(clause));

    private static bool WildcardMatches(string pattern, string value)
    {
        var previous = new bool[value.Length + 1];
        previous[0] = true;
        for (var row = 1; row <= pattern.Length; row++)
        {
            var current = new bool[value.Length + 1];
            if (pattern[row - 1] == '*') current[0] = previous[0];
            for (var column = 1; column <= value.Length; column++)
            {
                current[column] = pattern[row - 1] switch
                {
                    '*' => previous[column] || current[column - 1],
                    '?' => previous[column - 1],
                    _ => previous[column - 1] && pattern[row - 1] == value[column - 1]
                };
            }
            previous = current;
        }
        return previous[value.Length];
    }

    private static string EncodeSearchAfter(double score, string documentId) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{score:R}|{documentId}"));
    private static (double Score, string DocumentId)? DecodeSearchAfter(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var score) || string.IsNullOrWhiteSpace(parts[1])) throw new FormatException();
            return (score, parts[1]);
        }
        catch (FormatException)
        {
            throw new QueryValidationException("search_after is not a valid cursor.");
        }
    }
}
