using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public sealed record IndexedDocumentSnapshot(SearchDocument Document, long Version);
public sealed record IndexSnapshot(IndexDefinition Definition, IReadOnlyList<IndexedDocumentSnapshot> Documents, long Generation, DateTimeOffset RefreshedAt);

public sealed class InvertedIndex
{
    private sealed record IndexedDocument(SearchDocument Document, long Version, IReadOnlyDictionary<string, int> FieldLengths, IReadOnlyDictionary<string, IReadOnlyCollection<string>> FieldTerms);
    private sealed class MutableFieldStatistics
    {
        public int DocumentCount { get; set; }
        public long TotalLength { get; set; }
        public FieldStatistics Freeze() => new(DocumentCount, TotalLength);
    }

    private readonly object _gate = new();
    private readonly ITextAnalyzer _analyzer;
    private readonly IEmbeddingModel _embeddingModel;
    private readonly Dictionary<string, IndexedDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, Posting>>> _postings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MutableFieldStatistics> _fieldStatistics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> _documentFrequencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, long>> _prefixTerms = new(StringComparer.Ordinal);
    private readonly ExactVectorIndex _exactVector = new();
    private readonly ClusteredVectorIndex _approximateVector = new();
    private long _obsoletePostingCount;
    private long _generation;
    private DateTimeOffset _refreshedAt = DateTimeOffset.UnixEpoch;

    public InvertedIndex(IndexDefinition definition, ITextAnalyzer analyzer, IEmbeddingModel embeddingModel)
    {
        Definition = definition;
        _analyzer = analyzer;
        _embeddingModel = embeddingModel;
    }

    public IndexDefinition Definition { get; }
    public string Name => Definition.Name;
    public long Generation { get { lock (_gate) return _generation; } }
    public DateTimeOffset RefreshedAt { get { lock (_gate) return _refreshedAt; } }
    public IVectorIndex ExactVectorIndex => _exactVector;
    public IVectorIndex ApproximateVectorIndex => _approximateVector;

    public IReadOnlyList<AnalyzedToken> Analyze(string field, string text) => _analyzer.Analyze(text, Definition.GetAnalyzer(field));

    public void Upsert(SearchDocument document, long? requestedVersion = null)
    {
        if (!string.Equals(document.IndexName, Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Document index '{document.IndexName}' does not match '{Name}'.", nameof(document));
        }

        lock (_gate)
        {
            var version = requestedVersion ?? (_documents.TryGetValue(document.Id, out var existing) ? existing.Version + 1 : 1);
            if (_documents.TryGetValue(document.Id, out var previous))
            {
                RemoveFieldLengths(previous);
                _obsoletePostingCount++;
            }
            AddDocument(document, version);
            _generation++;
        }
    }

    public bool Delete(string documentId)
    {
        lock (_gate)
        {
            if (!_documents.Remove(documentId, out var document)) return false;
            RemoveFieldLengths(document);
            _exactVector.Delete(documentId);
            _approximateVector.Delete(documentId);
            _obsoletePostingCount++;
            _generation++;
            return true;
        }
    }

    public bool TryGetDocument(string documentId, out SearchDocument? document)
    {
        lock (_gate)
        {
            if (_documents.TryGetValue(documentId, out var stored))
            {
                document = stored.Document;
                return true;
            }
            document = null;
            return false;
        }
    }

    public IReadOnlyList<SearchDocument> GetDocuments()
    {
        lock (_gate) return _documents.Values.Select(item => item.Document).ToArray();
    }

    public IReadOnlyDictionary<string, Posting> GetPostings(string field, string term)
    {
        lock (_gate)
        {
            if (!_postings.TryGetValue(field, out var byTerm) || !byTerm.TryGetValue(term, out var entries))
            {
                return new Dictionary<string, Posting>(StringComparer.Ordinal);
            }
            return entries.Where(pair => _documents.TryGetValue(pair.Key, out var document) && document.Version == pair.Value.DocumentVersion)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
    }

    public IReadOnlyList<string> GetTerms(string? field = null)
    {
        lock (_gate)
        {
            var terms = field is null
                ? _postings.SelectMany(pair => pair.Value.Select(term => (Field: pair.Key, Term: term.Key)))
                : _postings.TryGetValue(field, out var byTerm)
                    ? byTerm.Keys.Select(term => (Field: field, Term: term))
                    : Enumerable.Empty<(string Field, string Term)>();
            return terms.Where(item => ActiveDocumentFrequency(item.Field, item.Term) > 0)
                .Select(item => item.Term).Distinct(StringComparer.Ordinal).OrderBy(term => term, StringComparer.Ordinal).ToArray();
        }
    }

    public int GetDocumentFrequency(string field, string term)
    {
        lock (_gate) return ActiveDocumentFrequency(field, term);
    }

    public FieldStatistics GetFieldStatistics(string field)
    {
        lock (_gate) return _fieldStatistics.TryGetValue(field, out var statistics) ? statistics.Freeze() : new FieldStatistics(0, 0);
    }

    public int GetFieldLength(string documentId, string field)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(documentId, out var document) && document.FieldLengths.TryGetValue(field, out var length) ? length : 0;
        }
    }

    public IReadOnlyList<Suggestion> Suggest(string prefix, int size)
    {
        var normalized = _analyzer.Analyze(prefix, new AnalyzerDefinition("suggest", RemoveStopWords: false, Stem: false)).LastOrDefault()?.Term ?? string.Empty;
        if (normalized.Length == 0) return [];
        lock (_gate)
        {
            if (_prefixTerms.TryGetValue(normalized, out var candidates))
            {
                return candidates.Keys
                    .Select(term => new Suggestion(term, DocumentFrequencyAcrossFields(term)))
                    .Where(suggestion => suggestion.DocumentFrequency > 0)
                    .OrderByDescending(suggestion => suggestion.DocumentFrequency).ThenBy(suggestion => suggestion.Text, StringComparer.Ordinal)
                    .Take(size).ToArray();
            }

            return GetTerms().Where(term => term.StartsWith(normalized, StringComparison.Ordinal))
                .Select(term => new Suggestion(term, DocumentFrequencyAcrossFields(term)))
                .OrderByDescending(suggestion => suggestion.DocumentFrequency).ThenBy(suggestion => suggestion.Text, StringComparer.Ordinal)
                .Take(size).ToArray();
        }
    }

    public string? DidYouMean(string text, int maxEdits = 2, int candidateLimit = 2_000)
    {
        var queryTerms = _analyzer.Analyze(text, BuiltInAnalyzers.Standard).Select(token => token.Term).Distinct(StringComparer.Ordinal).ToArray();
        if (queryTerms.Length == 0) return null;
        var dictionary = GetTerms().Take(candidateLimit).ToArray();
        var changed = false;
        var correction = new List<string>();
        foreach (var queryTerm in queryTerms)
        {
            if (dictionary.Contains(queryTerm, StringComparer.Ordinal))
            {
                correction.Add(queryTerm);
                continue;
            }
            var candidate = dictionary
                .Select(term => new { Term = term, Distance = EditDistance(queryTerm, term, maxEdits) })
                .Where(item => item.Distance <= maxEdits)
                .OrderBy(item => item.Distance)
                .ThenByDescending(item => DocumentFrequencyAcrossFields(item.Term))
                .ThenBy(item => item.Term, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate is null)
            {
                correction.Add(queryTerm);
                continue;
            }
            correction.Add(candidate.Term);
            changed = true;
        }
        return changed ? string.Join(' ', correction) : null;
    }

    public IndexStats GetStats()
    {
        lock (_gate)
        {
            var postingCount = _postings.Sum(field => field.Value.Sum(term => term.Value.Count));
            var terms = _postings.Sum(field => field.Value.Count(term => ActiveDocumentFrequency(field.Key, term.Key) > 0));
            return new IndexStats(Name, _documents.Count, _obsoletePostingCount, terms, postingCount, _generation, _refreshedAt);
        }
    }

    public void MarkRefreshed(DateTimeOffset time)
    {
        lock (_gate) _refreshedAt = time;
    }

    public void Compact()
    {
        lock (_gate)
        {
            var remaining = _documents.Values.Select(document => new IndexedDocumentSnapshot(document.Document, document.Version)).ToArray();
            _documents.Clear();
            _postings.Clear();
            _fieldStatistics.Clear();
            _prefixTerms.Clear();
            foreach (var document in remaining)
            {
                AddDocument(document.Document, document.Version);
            }
            _obsoletePostingCount = 0;
            _generation++;
        }
    }

    public IndexSnapshot ExportSnapshot()
    {
        lock (_gate)
        {
            return new IndexSnapshot(Definition, _documents.Values.Select(document => new IndexedDocumentSnapshot(document.Document, document.Version)).ToArray(), _generation, _refreshedAt);
        }
    }

    public void ImportSnapshot(IndexSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Definition.Name, Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snapshot index name does not match this index.", nameof(snapshot));
        }
        lock (_gate)
        {
            _documents.Clear();
            _postings.Clear();
            _fieldStatistics.Clear();
            _prefixTerms.Clear();
            foreach (var document in snapshot.Documents)
            {
                AddDocument(document.Document, document.Version);
            }
            _generation = snapshot.Generation;
            _refreshedAt = snapshot.RefreshedAt;
            _obsoletePostingCount = 0;
        }
    }

    private void AddDocument(SearchDocument document, long version)
    {
        var fieldLengths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fieldTerms = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in document.Fields)
        {
            if (Definition.Fields.TryGetValue(field, out var definition) && !definition.Searchable) continue;
            var tokens = Analyze(field, value);
            fieldLengths[field] = tokens.Count;
            var stats = GetMutableStatistics(field);
            stats.DocumentCount++;
            stats.TotalLength += tokens.Count;
            var termGroups = tokens.GroupBy(token => token.Term, StringComparer.Ordinal).ToArray();
            fieldTerms[field] = termGroups.Select(group => group.Key).ToArray();
            foreach (var group in termGroups)
            {
                if (!_postings.TryGetValue(field, out var terms)) _postings[field] = terms = new Dictionary<string, Dictionary<string, Posting>>(StringComparer.Ordinal);
                if (!terms.TryGetValue(group.Key, out var documents)) terms[group.Key] = documents = new Dictionary<string, Posting>(StringComparer.Ordinal);
                var positions = group.Select(token => token.Position).Distinct().Order().ToArray();
                documents[document.Id] = new Posting(group.Count(), positions, version);
                IncrementDocumentFrequency(field, group.Key);
            }
            foreach (var token in tokens.Where(token => token.Type is "WORD" or "NGRAM" or "EDGE_NGRAM").Select(token => token.Term).Distinct(StringComparer.Ordinal))
            {
                for (var length = 2; length <= Math.Min(20, token.Length); length++)
                {
                    var prefix = token[..length];
                    if (!_prefixTerms.TryGetValue(prefix, out var candidates)) _prefixTerms[prefix] = candidates = new Dictionary<string, long>(StringComparer.Ordinal);
                    candidates[token] = candidates.TryGetValue(token, out var count) ? count + 1 : 1;
                }
            }
        }
        _documents[document.Id] = new IndexedDocument(document, version, fieldLengths, fieldTerms);
        var embeddingText = string.Join(' ', document.Fields.Where(field => !Definition.Fields.TryGetValue(field.Key, out var definition) || definition.Searchable).Select(field => field.Value));
        var vector = _embeddingModel.Embed(embeddingText);
        _exactVector.Upsert(document.Id, vector);
        _approximateVector.Upsert(document.Id, vector);
    }

    private void RemoveFieldLengths(IndexedDocument document)
    {
        foreach (var (field, length) in document.FieldLengths)
        {
            var stats = GetMutableStatistics(field);
            stats.DocumentCount--;
            stats.TotalLength -= length;
        }
        foreach (var (field, terms) in document.FieldTerms)
        {
            foreach (var term in terms) IncrementDocumentFrequency(field, term, -1);
        }
        _exactVector.Delete(document.Document.Id);
        _approximateVector.Delete(document.Document.Id);
    }

    private MutableFieldStatistics GetMutableStatistics(string field)
    {
        if (!_fieldStatistics.TryGetValue(field, out var statistics)) _fieldStatistics[field] = statistics = new MutableFieldStatistics();
        return statistics;
    }

    private void IncrementDocumentFrequency(string field, string term, int delta = 1)
    {
        if (!_documentFrequencies.TryGetValue(field, out var terms)) _documentFrequencies[field] = terms = new Dictionary<string, int>(StringComparer.Ordinal);
        terms[term] = terms.TryGetValue(term, out var current) ? current + delta : delta;
    }

    private int ActiveDocumentFrequency(string field, string term) =>
        _documentFrequencies.TryGetValue(field, out var terms) && terms.TryGetValue(term, out var count) ? Math.Max(0, count) : 0;

    private int DocumentFrequencyAcrossFields(string term) => _documentFrequencies.Keys.Sum(field => ActiveDocumentFrequency(field, term));

    public static int EditDistance(string source, string target, int limit)
    {
        if (Math.Abs(source.Length - target.Length) > limit) return limit + 1;
        var previous = Enumerable.Range(0, target.Length + 1).ToArray();
        for (var row = 1; row <= source.Length; row++)
        {
            var current = new int[target.Length + 1];
            current[0] = row;
            var rowMinimum = current[0];
            for (var column = 1; column <= target.Length; column++)
            {
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + (source[row - 1] == target[column - 1] ? 0 : 1));
                rowMinimum = Math.Min(rowMinimum, current[column]);
            }
            if (rowMinimum > limit) return limit + 1;
            previous = current;
        }
        return previous[target.Length];
    }
}
