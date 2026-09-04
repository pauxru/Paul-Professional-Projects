using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Application.Answering;
using RagAssistant.Application.Evaluation;
using RagAssistant.Application.Retrieval;
using RagAssistant.Domain.Documents;
using RagAssistant.Eval;
using RagAssistant.Infrastructure;
using RagAssistant.Infrastructure.Persistence;
using RagAssistant.Infrastructure.Seeding;

var diag = args.Any(a => string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase));

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Database:Provider"] = "Sqlite",
        ["Database:ConnectionString"] = "Data Source=rag-eval.db",
        ["Ai:Provider"] = "Local",
        ["Ai:EmbeddingDimensions"] = "384",
        ["Budget:DailyLimitUsd"] = "1000",
        ["Budget:DailyRequestLimit"] = "10000",
        ["Rag:MinRetrievalScore"] = "0.02",
        ["Rag:MinSupportForSentence"] = "0.15",
        ["Rag:MinSupportRatio"] = "0.25",
    })
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddLogging();
services.AddRagInfrastructure(configuration);

await using var provider = services.BuildServiceProvider();

var factory = provider.GetRequiredService<IDbContextFactory<RagDbContext>>();
await using var db = await factory.CreateDbContextAsync();
if (diag)
{
    await db.Database.EnsureDeletedAsync();
}
await db.Database.EnsureCreatedAsync();

await using (var scope = provider.CreateAsyncScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<CorpusSeeder>();
    var ingested = await seeder.SeedAsync(CancellationToken.None);
    Console.WriteLine($"Seed: {ingested} document(s) ingested.");
}

await using (var scope = provider.CreateAsyncScope())
{
    var harness = scope.ServiceProvider.GetRequiredService<EvaluationHarness>();
    var employee = new UserPrincipal("eval-employee", ["employee"], ["engineering", "finance", "hr", "operations", "security"], Classification.Confidential);
    var restricted = new UserPrincipal("eval-guest", ["employee"], [], Classification.Public);
    var examples = EvalGoldenDataset.Build(employee, restricted);

    Console.WriteLine($"Running evaluation on {examples.Count} golden examples...\n");
    var summary = await harness.RunAsync(examples, k: 5, CancellationToken.None);

    Console.WriteLine("Retrieval metrics (K=5):");
    foreach (var metric in summary.RetrievalMetrics)
    {
        Console.WriteLine($"  {metric.Mode,-8}  Recall@5={metric.RecallAtK:P1}   MRR={metric.MeanReciprocalRank:F3}   nDCG@5={metric.NdcgAtK:F3}");
    }

    Console.WriteLine();
    Console.WriteLine($"Citation precision      : {summary.CitationPrecision:P1}");
    Console.WriteLine($"Citation recall         : {summary.CitationRecall:P1}");
    Console.WriteLine($"Any-correct-citation    : {summary.AnyCorrectCitationRate:P1}");
    Console.WriteLine($"Refusal accuracy        : {summary.RefusalAccuracy:P1}");
    Console.WriteLine($"Total examples          : {summary.Examples}");

    foreach (var slice in summary.Slices)
    {
        Console.WriteLine();
        Console.WriteLine($"Slice '{slice.Slice}' ({slice.Examples} examples):");
        foreach (var m in slice.RetrievalMetrics)
        {
            Console.WriteLine($"  {m.Mode,-8}  Recall@5={m.RecallAtK:P1}   MRR={m.MeanReciprocalRank:F3}   nDCG@5={m.NdcgAtK:F3}");
        }
    }

    if (diag)
    {
        Console.WriteLine();
        Console.WriteLine("### PER-EXAMPLE DIAGNOSTIC ###");
        Console.WriteLine();

        var answering = scope.ServiceProvider.GetRequiredService<AnsweringService>();
        var perfect = 0;
        var partial = 0;
        var missed = 0;
        var refuseWrong = 0;
        foreach (var ex in examples)
        {
            if (ex.ShouldRefuse) continue;
            var ans = await answering.AnswerAsync(new AnswerRequest(ex.Question, ex.AskedBy, RetrievalMode.Hybrid, 5), CancellationToken.None);
            var citedTitles = ans.Citations.Select(c => c.DocumentTitle).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var expected = ex.ExpectedDocumentTitles;
            var relevant = citedTitles.Count(t => expected.Contains(t, StringComparer.OrdinalIgnoreCase));
            var precision = citedTitles.Length == 0 ? 0 : (double)relevant / citedTitles.Length;
            if (ans.Refused)
            {
                refuseWrong++;
            }
            else if (relevant == citedTitles.Length && citedTitles.Length > 0) perfect++;
            else if (relevant > 0) partial++;
            else missed++;

            Console.WriteLine($"{ex.Id}  refused={ans.Refused}  citations={citedTitles.Length}  correct={relevant}  precision={precision:P0}");
            Console.WriteLine($"  Q: {ex.Question}");
            Console.WriteLine($"  Expected: {string.Join(" | ", expected)}");
            Console.WriteLine($"  Cited   : {string.Join(" | ", citedTitles)}");
            Console.WriteLine($"  Answer  : {(ans.Answer.Length > 200 ? ans.Answer[..200] + "..." : ans.Answer)}");
            Console.WriteLine();
        }

        Console.WriteLine($"Perfect (100% precision) : {perfect}");
        Console.WriteLine($"Partial                  : {partial}");
        Console.WriteLine($"Missed                   : {missed}");
        Console.WriteLine($"Wrongly refused          : {refuseWrong}");
    }
}

