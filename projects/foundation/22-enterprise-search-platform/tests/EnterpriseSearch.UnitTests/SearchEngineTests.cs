using System.Diagnostics;
using EnterpriseSearch.Application.Search;
using EnterpriseSearch.Domain.Search;
using EnterpriseSearch.Infrastructure.Search;
using EnterpriseSearch.UnitTests.Support;

namespace EnterpriseSearch.UnitTests;

public sealed class SearchEngineTests
{
    [Fact]
    public void Analyze_CharacterFilters_NormalizesHtmlCaseAndAccents()
    {
        var tokens = new TextAnalyzer().Analyze("<b>Café</b> NAÏVE", new AnalyzerDefinition("test", RemoveStopWords: false, Stem: false));
        Assert.Equal(["cafe", "naive"], tokens.Select(token => token.Term));
    }

    [Fact]
    public void Analyze_WhitespaceTokenizer_PreservesWhitespaceSeparatedTokens()
    {
        var tokens = new TextAnalyzer().Analyze("one\ttwo  three", new AnalyzerDefinition("white", TokenizerKind.Whitespace, RemoveStopWords: false, Stem: false));
        Assert.Equal(["one", "two", "three"], tokens.Select(token => token.Term));
        Assert.Equal([0, 1, 2], tokens.Select(token => token.Position));
    }

    [Fact]
    public void Analyze_NgramTokenizer_ProducesInteriorGrams()
    {
        var tokens = new TextAnalyzer().Analyze("desk", new AnalyzerDefinition("ngram", TokenizerKind.NGram, RemoveStopWords: false, Stem: false, MinGram: 2, MaxGram: 2));
        Assert.Equal(["de", "es", "sk"], tokens.Select(token => token.Term));
    }

    [Fact]
    public void Analyze_EdgeNgramTokenizer_ProducesPrefixes()
    {
        var tokens = new TextAnalyzer().Analyze("laptop", new AnalyzerDefinition("edge", TokenizerKind.EdgeNGram, RemoveStopWords: false, Stem: false, MinGram: 2, MaxGram: 4));
        Assert.Equal(["la", "lap", "lapt"], tokens.Select(token => token.Term));
    }

    [Fact]
    public void Analyze_StopwordFilter_RemovesTermsButKeepsPositionGaps()
    {
        var tokens = new TextAnalyzer().Analyze("quick the fox", BuiltInAnalyzers.Standard);
        Assert.Equal(["quick", "fox"], tokens.Select(token => token.Term));
        Assert.Equal([0, 2], tokens.Select(token => token.Position));
    }

    [Theory]
    [InlineData("caresses", "caress")]
    [InlineData("ponies", "poni")]
    [InlineData("cats", "cat")]
    [InlineData("agreed", "agre")]
    [InlineData("relational", "relat")]
    [InlineData("hopping", "hop")]
    public void PorterStemmer_KnownWords_ReturnsPorterStems(string word, string expected)
    {
        Assert.Equal(expected, PorterStemmer.Stem(word));
    }

    [Fact]
    public void Analyze_SynonymFilter_ExpandsAtSamePosition()
    {
        var tokens = new TextAnalyzer().Analyze("laptop", new AnalyzerDefinition("syn", ExpandSynonyms: true, RemoveStopWords: false, Stem: false));
        Assert.Contains(tokens, token => token.Term == "laptop" && token.Position == 0);
        Assert.Contains(tokens, token => token.Term == "notebook" && token.Position == 0 && token.Type == "SYNONYM");
    }

    [Fact]
    public void Analyze_Shingles_AddsAdjacentTokenShingle()
    {
        var tokens = new TextAnalyzer().Analyze("quick fox", new AnalyzerDefinition("shingle", Shingles: true, RemoveStopWords: false, Stem: false));
        Assert.Contains(tokens, token => token.Term == "quick_fox" && token.Type == "SHINGLE");
    }

    [Fact]
    public void InvertedIndex_Add_StoresTermFrequencyAndPositions()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "alpha alpha beta"));
        var posting = Assert.Single(index.GetPostings("title", "alpha"));
        Assert.Equal(2, posting.Value.TermFrequency);
        Assert.Equal([0, 1], posting.Value.Positions);
    }

    [Fact]
    public void InvertedIndex_Update_MakesOldTermsInvisible()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "legacy"));
        index.Upsert(harness.Document("one", "modern"));
        Assert.Empty(index.GetPostings("title", "legacy"));
        Assert.Single(index.GetPostings("title", "modern"));
    }

    [Fact]
    public void InvertedIndex_Delete_LeavesTombstoneUntilCompaction()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "legacy"));
        Assert.True(index.Delete("one"));
        Assert.Equal(0, index.GetStats().Documents);
        Assert.True(index.GetStats().Tombstones > 0);
        Assert.True(index.GetStats().PostingCount > 0);
    }

    [Fact]
    public void InvertedIndex_Compact_RemovesObsoletePostings()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "legacy"));
        index.Delete("one");
        index.Compact();
        Assert.Empty(index.GetPostings("title", "legacy"));
        Assert.Equal(0, index.GetStats().Tombstones);
    }

    [Fact]
    public void PhraseQuery_ExactPositions_ReturnsExactPhrase()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("exact", "quick brown fox"));
        index.Upsert(harness.Document("far", "quick agile brown fox"));
        var response = harness.Engine.Search(new SearchRequest("test", new PhraseClause("title", ["quick", "brown", "fox"]), Size: 10));
        Assert.Equal(["exact"], response.Hits.Select(hit => hit.Document.Id));
    }

    [Fact]
    public void PhraseQuery_WithSlop_ReturnsNearbyPhrase()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("far", "quick agile brown fox"));
        var response = harness.Engine.Search(new SearchRequest("test", new PhraseClause("title", ["quick", "brown", "fox"], Slop: 1), Size: 10));
        Assert.Single(response.Hits);
    }

    [Fact]
    public void PhraseQuery_StopwordGap_RequiresThePreservedPosition()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("with-stopword", "quick the fox"));
        index.Upsert(harness.Document("without-stopword", "quick fox"));
        var response = harness.Engine.Search(new SearchRequest("test", new PhraseClause("title", ["quick", "the", "fox"]), Size: 10));
        Assert.Equal(["with-stopword"], response.Hits.Select(hit => hit.Document.Id));
    }

    [Fact]
    public void Bm25Scorer_TinyFixture_EqualsHandComputedValue()
    {
        var harness = new SearchHarness(bm25: new Bm25Options { K1 = 1.2, B = 0.75 });
        var fields = new Dictionary<string, FieldDefinition> { ["body"] = new("body", "standard", 1d, true) };
        var index = harness.CreateIndex(fields: fields);
        index.Upsert(new SearchDocument("one", "test", new Dictionary<string, string> { ["body"] = "apple apple banana" }));
        index.Upsert(new SearchDocument("two", "test", new Dictionary<string, string> { ["body"] = "banana" }));
        var posting = index.GetPostings("body", "appl")["one"];
        var actual = new Bm25Scorer(new Bm25Options { K1 = 1.2, B = 0.75 }).ScoreTerm(index, "one", "body", "appl", posting).Contribution;
        var expected = Math.Log(2d) * (2d * 2.2d / (2d + 1.2d * (1d - .75d + .75d * 3d / 2d)));
        Assert.Equal(expected, actual, 10);
    }

    [Fact]
    public void Search_FieldBoost_RanksTitleMatchAboveBodyMatch()
    {
        var harness = new SearchHarness(function: new FunctionScoreOptions { RecencyWeight = 0, PopularityWeight = 0, InStockMultiplier = 1, ClickWeight = 0 });
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("title", "laptop", "ordinary"));
        index.Upsert(harness.Document("body", "ordinary", "laptop"));
        var response = harness.Engine.Search(new SearchRequest("test", new TermClause(null, "laptop"), Size: 10));
        Assert.Equal("title", response.Hits[0].Document.Id);
    }

    [Fact]
    public void FunctionScore_RecencyDecay_PrefersRecentDocument()
    {
        var harness = new SearchHarness(function: new FunctionScoreOptions { RecencyHalfLifeDays = 10, RecencyWeight = 1, PopularityWeight = 0, InStockMultiplier = 1, ClickWeight = 0 });
        var recent = harness.Document("recent", "item", created: harness.Clock.UtcNow);
        var old = harness.Document("old", "item", created: harness.Clock.UtcNow.AddDays(-20));
        var function = new FunctionScorer(new FunctionScoreOptions { RecencyHalfLifeDays = 10, RecencyWeight = 1, PopularityWeight = 0, InStockMultiplier = 1, ClickWeight = 0 }, harness.Clock, harness.Analytics);
        Assert.True(function.Score(recent, "test", "item").Multiplier > function.Score(old, "test", "item").Multiplier);
    }

    [Fact]
    public void BooleanQuery_MustNotAndFilter_ExcludesAndDoesNotChangeScore()
    {
        var harness = new SearchHarness(function: new FunctionScoreOptions { RecencyWeight = 0, PopularityWeight = 0, InStockMultiplier = 1, ClickWeight = 0 });
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("keep", "ordinary", "apple", "fruit"));
        index.Upsert(harness.Document("blocked", "blocked", "apple", "fruit"));
        index.Upsert(harness.Document("other", "ordinary", "apple", "other"));
        var baseline = harness.Engine.Search(new SearchRequest("test", new TermClause("body", "apple"), Size: 10)).Hits.Single(hit => hit.Document.Id == "keep").Score;
        var query = new BooleanClause(Must: [new TermClause("body", "apple")], MustNot: [new TermClause("title", "blocked")], Filter: [new TermClause("category", "fruit")]);
        var response = harness.Engine.Search(new SearchRequest("test", query, Size: 10));
        var hit = Assert.Single(response.Hits);
        Assert.Equal("keep", hit.Document.Id);
        Assert.Equal(baseline, hit.Score, 10);
    }

    [Fact]
    public void RangeQuery_FiltersNumericDocuments()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("low", "item", price: 99m));
        index.Upsert(harness.Document("mid", "item", price: 200m));
        var response = harness.Engine.Search(new SearchRequest("test", new RangeClause("price", 100m, 300m), Size: 10));
        Assert.Equal(["mid"], response.Hits.Select(hit => hit.Document.Id));
    }

    [Fact]
    public void PrefixQuery_ReturnsPrefixMatches()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "laptop"));
        var response = harness.Engine.Search(new SearchRequest("test", new PrefixClause("title", "lap"), Size: 10));
        Assert.Single(response.Hits);
    }

    [Fact]
    public void WildcardQuery_ExpansionLimit_IsEnforced()
    {
        var harness = new SearchHarness(new QueryLimits { MaxWildcardExpansion = 1 });
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "alpha"));
        index.Upsert(harness.Document("two", "alpine"));
        Assert.Throws<QueryValidationException>(() => harness.Engine.Search(new SearchRequest("test", new WildcardClause("title", "al*"), Size: 10)));
    }

    [Fact]
    public void FuzzyQuery_BoundedLevenshtein_ReturnsCloseTerm()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "laptop"));
        var response = harness.Engine.Search(new SearchRequest("test", new FuzzyClause("title", "laptpo", 2), Size: 10));
        Assert.Single(response.Hits);
    }

    [Fact]
    public void QueryStringParser_ComplexQuery_ParsesBooleanAndRange()
    {
        var parser = new QueryStringParser(new QueryLimits());
        var clause = parser.Parse("title:(laptop OR notebook) AND price:[100 TO 500] -refurbished");
        var root = Assert.IsType<BooleanClause>(clause);
        Assert.NotNull(root.Must);
        Assert.NotEmpty(root.Must!);
    }

    [Fact]
    public void QueryStringParser_MalformedInput_ReportsLocation()
    {
        var error = Assert.Throws<QueryParseException>(() => new QueryStringParser(new QueryLimits()).Parse("title:(laptop OR"));
        Assert.Contains("character", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryStringParser_PhraseSlop_ParsesBoundedDistance()
    {
        var clause = new QueryStringParser(new QueryLimits()).Parse("title:\"quick brown\"~2");
        var phrase = Assert.IsType<PhraseClause>(clause);
        Assert.Equal("title", phrase.Field);
        Assert.Equal(2, phrase.Slop);
    }

    [Fact]
    public void Facets_MultiSelect_ExcludesOwnFilterFromOwnCounts()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("electric", "item", category: "electronics"));
        index.Upsert(harness.Document("home", "item", category: "home"));
        var response = harness.Engine.Search(new SearchRequest("test", new MatchAllClause(), Filters: new Dictionary<string, IReadOnlyList<string>> { ["category"] = ["electronics"] }, Facets: [new FacetRequest("category", "category")], Size: 10));
        Assert.Equal(1, response.Total);
        var buckets = response.Facets["category"].Buckets.ToDictionary(bucket => bucket.Key, bucket => bucket.Count);
        Assert.Equal(1, buckets["electronics"]);
        Assert.Equal(1, buckets["home"]);
    }

    [Fact]
    public void SecurityTrimming_ExcludesRestrictedResultsAndFacets()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("public", "item", category: "public"));
        index.Upsert(harness.Document("secret", "item", category: "restricted", groups: ["staff"]));
        var response = harness.Engine.Search(new SearchRequest("test", new MatchAllClause(), Facets: [new FacetRequest("category", "category")], CallerGroups: Array.Empty<string>(), Size: 10));
        Assert.Equal(["public"], response.Hits.Select(hit => hit.Document.Id));
        Assert.DoesNotContain(response.Facets["category"].Buckets, bucket => bucket.Key == "restricted");
    }

    [Fact]
    public async Task AliasSwap_AtomicUpdate_PointsReadersAtNewVersion()
    {
        var harness = new SearchHarness();
        await harness.Service.CreateAsync(new IndexDefinition("products-v1", SearchHarness.DefaultFields()), "products");
        await harness.Service.CreateAsync(new IndexDefinition("products-v2", SearchHarness.DefaultFields()));
        await harness.Service.SwapAliasAsync("products", "products-v1", "products-v2");
        Assert.Equal("products-v2", harness.Cluster.GetIndex("products").Index.Name);
    }

    [Fact]
    public async Task NearRealTimeRefresh_QueuedDocument_IsInvisibleUntilRefresh()
    {
        var harness = new SearchHarness();
        await harness.Service.CreateAsync(new IndexDefinition("products", SearchHarness.DefaultFields()));
        harness.Service.EnqueueDocument(harness.Document("queued", "laptop", index: "products"));
        Assert.Equal(0, harness.Engine.Search(new SearchRequest("products", new TermClause("title", "laptop"), Size: 10)).Total);
        await harness.Service.RefreshAsync("products");
        Assert.Equal(1, harness.Engine.Search(new SearchRequest("products", new TermClause("title", "laptop"), Size: 10)).Total);
    }

    [Fact]
    public void SuggestAndDidYouMean_ReturnsPrefixAndCorrection()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "laptop"));
        Assert.Contains(index.Suggest("lap", 5), item => item.Text == "laptop");
        Assert.Equal("laptop", index.DidYouMean("laptpo"));
    }

    [Fact]
    public void Highlighting_UsesTokenOffsetsAndEmphasisTags()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "ordinary", "The quick brown fox"));
        var hit = Assert.Single(harness.Engine.Search(new SearchRequest("test", new TermClause("body", "quick"), HighlightFields: ["body"], Size: 10)).Hits);
        Assert.Equal("The <em>quick</em> brown fox", hit.Highlights!["body"]);
    }

    [Fact]
    public void VectorIndex_ApproximateSearch_MeasuresRecallAgainstExact()
    {
        var analyzer = new TextAnalyzer();
        var embedding = new DeterministicEmbeddingModel(analyzer);
        var exact = new ExactVectorIndex();
        var approximate = new ClusteredVectorIndex(maximumClusters: 4, probeCount: 2);
        for (var number = 0; number < 40; number++)
        {
            var vector = embedding.Embed($"retail laptop vector {number}");
            exact.Upsert(number.ToString(), vector);
            approximate.Upsert(number.ToString(), vector);
        }
        var measurement = VectorSearchMeasurement.Compare(exact, approximate, embedding.Embed("retail laptop vector 5"), 5);
        Assert.InRange(measurement.RecallAtK, 0d, 1d);
        Assert.True(measurement.ExactLatency >= TimeSpan.Zero);
        Assert.True(measurement.ApproximateLatency >= TimeSpan.Zero);
    }

    [Fact]
    public void HybridRrf_FusesTwoRankedLists_PreferringSharedDocument()
    {
        var fused = SearchEngine.ReciprocalRankFusion(new Dictionary<string, double> { ["a"] = 2, ["b"] = 1 }, new Dictionary<string, double> { ["b"] = 2, ["c"] = 1 });
        Assert.Equal("b", fused.MaxBy(pair => pair.Value).Key);
    }

    [Fact]
    public void ClickFeedback_FrequentlyClickedDocument_RisesInRanking()
    {
        var harness = new SearchHarness(function: new FunctionScoreOptions { RecencyWeight = 0, PopularityWeight = 0, InStockMultiplier = 1, ClickWeight = 0.5 });
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("a", "alpha"));
        index.Upsert(harness.Document("b", "alpha"));
        Assert.Equal("a", harness.Engine.Search(new SearchRequest("test", QueryText: "alpha", Size: 10)).Hits[0].Document.Id);
        for (var click = 0; click < 10; click++) harness.Engine.LogClick(new ClickEvent("test", "alpha", "b", 2, harness.Clock.UtcNow));
        Assert.Equal("b", harness.Engine.Search(new SearchRequest("test", QueryText: "alpha", Size: 10)).Hits[0].Document.Id);
    }

    [Fact]
    public void Analytics_LogsShownResultsClicksAndZeroResultRate()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "alpha"));
        harness.Engine.Search(new SearchRequest("test", QueryText: "alpha", Size: 10));
        harness.Engine.LogClick(new ClickEvent("test", "alpha", "one", 1, harness.Clock.UtcNow));
        harness.Engine.Search(new SearchRequest("test", QueryText: "no-match", Size: 10));
        var summary = harness.Engine.GetAnalytics();
        Assert.Equal(0.5d, summary.ZeroResultRate, 10);
        var first = Assert.Single(summary.CtrByPosition);
        Assert.Equal(1, first.Position);
        Assert.Equal(1, first.Impressions);
        Assert.Equal(1, first.Clicks);
    }

    [Fact]
    public async Task EvaluationHarness_HybridNdcg_IsAtLeastKeywordOnSeededGoldenSet()
    {
        var harness = new SearchHarness(function: new FunctionScoreOptions { RecencyWeight = 0, PopularityWeight = 0, InStockMultiplier = 1, ClickWeight = 0 });
        await new ContosoDemoSeeder(harness.Cluster, harness.Service, harness.Clock).SeedAsync();
        var report = new RelevanceEvaluationHarness(harness.Engine, harness.Clock).Run("catalogue", GoldenEvaluationSet.Create());
        Assert.Equal(30, report.QueryCount);
        Assert.True(report.Metrics[RetrievalMode.HybridRrf].NdcgAt10 >= report.Metrics[RetrievalMode.Keyword].NdcgAt10);
    }

    [Fact]
    public void SearchAfterPagination_IsStableAndDoesNotRepeatHits()
    {
        var harness = new SearchHarness(function: new FunctionScoreOptions { RecencyWeight = 0, PopularityWeight = 0, InStockMultiplier = 1, ClickWeight = 0 });
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("a", "alpha"));
        index.Upsert(harness.Document("b", "alpha"));
        index.Upsert(harness.Document("c", "alpha"));
        var first = harness.Engine.Search(new SearchRequest("test", QueryText: "alpha", Size: 1));
        var second = harness.Engine.Search(new SearchRequest("test", QueryText: "alpha", SearchAfter: first.NextSearchAfter, Size: 1));
        Assert.NotEqual(first.Hits[0].Document.Id, second.Hits[0].Document.Id);
        Assert.Equal("a", first.Hits[0].Document.Id);
        Assert.Equal("b", second.Hits[0].Document.Id);
    }

    [Fact]
    public void IndexingThroughput_FiveThousandDocuments_CompletesWithinBound()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        var stopwatch = Stopwatch.StartNew();
        for (var number = 0; number < 5_000; number++) index.Upsert(harness.Document($"bulk-{number}", $"catalogue laptop {number}", "durable retail product"));
        stopwatch.Stop();
        Assert.Equal(5_000, index.GetStats().Documents);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Indexing took {stopwatch.Elapsed}.");
    }
    [Fact]
    public void IndexingQueue_BackpressureRecoversAfterRefresh()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        var queue = new SearchIndexHandle(index, queueCapacity: 1);
        queue.Enqueue(new UpsertIndexOperation(harness.Document("one", "first")));
        Assert.Throws<IndexingBackpressureException>(() => queue.Enqueue(new UpsertIndexOperation(harness.Document("two", "second"))));
        Assert.Equal(1, queue.Refresh(harness.Clock.UtcNow));
        queue.Enqueue(new UpsertIndexOperation(harness.Document("two", "second")));
        Assert.Equal(1, queue.Refresh(harness.Clock.UtcNow));
        Assert.Equal(2, index.GetStats().Documents);
    }

    [Fact]
    public async Task PartialUpdate_QueuedPatch_ReindexesChangedFields()
    {
        var harness = new SearchHarness();
        await harness.Service.CreateAsync(new IndexDefinition("products", SearchHarness.DefaultFields()));
        harness.Service.EnqueueDocument(harness.Document("one", "legacy", index: "products"));
        await harness.Service.RefreshAsync("products");
        harness.Service.EnqueuePatch("products", "one", new DocumentPatch(Fields: new Dictionary<string, string> { ["title"] = "modern" }));
        await harness.Service.RefreshAsync("products");
        Assert.Equal(0, harness.Engine.Search(new SearchRequest("products", QueryText: "legacy", Size: 10)).Total);
        Assert.Equal(1, harness.Engine.Search(new SearchRequest("products", QueryText: "modern", Size: 10)).Total);
    }

    [Fact]
    public void SnapshotImport_RebuildsPersistedDocumentIndex()
    {
        var harness = new SearchHarness();
        var source = harness.CreateIndex("source");
        source.Upsert(harness.Document("one", "persisted laptop", index: "source"));
        var restored = new InvertedIndex(source.Definition, harness.Analyzer, new DeterministicEmbeddingModel(harness.Analyzer));
        restored.ImportSnapshot(source.ExportSnapshot());
        Assert.Single(restored.GetPostings("title", "laptop"));
        Assert.True(restored.TryGetDocument("one", out var document));
        Assert.Equal("persisted laptop", document!.Fields["title"]);
    }

    [Fact]
    public void HierarchicalAndRangeFacets_CountEachExpectedBucket()
    {
        var harness = new SearchHarness();
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("one", "item", category: "electronics/laptops", price: 150m));
        index.Upsert(harness.Document("two", "item", category: "electronics/televisions", price: 350m));
        var response = harness.Engine.Search(new SearchRequest("test", new MatchAllClause(), Facets:
        [
            new FacetRequest("category", "category", FacetKind.Hierarchical),
            new FacetRequest("price", "price", FacetKind.Range, [new NumericRange("100-300", 100m, 300m), new NumericRange("300-500", 300m, 500m)])
        ], Size: 10));
        Assert.Contains(response.Facets["category"].Buckets, bucket => bucket.Key == "electronics" && bucket.Count == 2);
        Assert.Equal(1, response.Facets["price"].Buckets.Single(bucket => bucket.Key == "100-300").Count);
        Assert.Equal(1, response.Facets["price"].Buckets.Single(bucket => bucket.Key == "300-500").Count);
    }

    [Fact]
    public void MultiFieldQuery_PerFieldBoost_PrefersBoostedBodyField()
    {
        var harness = new SearchHarness(function: new FunctionScoreOptions { RecencyWeight = 0, PopularityWeight = 0, InStockMultiplier = 1, ClickWeight = 0 });
        var index = harness.CreateIndex();
        index.Upsert(harness.Document("title", "laptop", "ordinary"));
        index.Upsert(harness.Document("body", "ordinary", "laptop"));
        var query = new MultiFieldClause("laptop", new Dictionary<string, double> { ["title"] = 0.1d, ["body"] = 4d });
        var response = harness.Engine.Search(new SearchRequest("test", query, Size: 10));
        Assert.Equal("body", response.Hits[0].Document.Id);
    }

    [Fact]
    public async Task DeleteByQuery_MatchedDocumentsBecomeInvisibleAfterRefresh()
    {
        var harness = new SearchHarness();
        await harness.Service.CreateAsync(new IndexDefinition("products", SearchHarness.DefaultFields()));
        harness.Service.EnqueueDocument(harness.Document("remove", "legacy laptop", index: "products"));
        harness.Service.EnqueueDocument(harness.Document("keep", "modern laptop", index: "products"));
        await harness.Service.RefreshAsync("products");
        foreach (var id in harness.Engine.FindDocumentIds("products", new TermClause("title", "legacy")))
        {
            harness.Service.EnqueueDelete("products", id);
        }
        await harness.Service.RefreshAsync("products");
        var response = harness.Engine.Search(new SearchRequest("products", QueryText: "laptop", Size: 10));
        Assert.Equal(["keep"], response.Hits.Select(hit => hit.Document.Id));
    }
}
