using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ReconEngine.Application.Abstractions;
using ReconEngine.Application.DataGeneration;
using ReconEngine.Application.Ingestion;
using ReconEngine.Application.Reconciliation;
using ReconEngine.Domain.Entities;
using ReconEngine.IntegrationTests.Support;

namespace ReconEngine.IntegrationTests.Reconciliation;

/// <summary>
/// End-to-end reconciliation through the real stack (EF Core + SQLite in-memory): generate a dataset,
/// stream it in through the import pipeline, run the orchestrator and assert the persisted run against
/// the generator's ground-truth manifest. Also pins idempotent re-runs, carry-forward and large-file
/// streaming.
/// </summary>
public sealed class ReconciliationFlowTests
{
    private static async Task<(GeneratedFiles Files, DefectManifest Manifest)> GenerateAsync(int rows, int seed)
    {
        var dataset = new SyntheticDataGenerator().Generate(GenerationOptions.All(rows, seed));
        var dir = Path.Combine(AppContext.BaseDirectory, "gen", Guid.NewGuid().ToString("N"));
        var files = await DatasetFileWriter.WriteAsync(dataset, dir, writeFixedWidth: false);
        return (files, dataset.Manifest);
    }

    private static async Task<ImportResult> ImportAsync(ReconApiFactory factory, string path, FileFormatProfile profile)
    {
        using var scope = factory.CreateScope();
        var import = scope.ServiceProvider.GetRequiredService<ImportService>();
        await using var stream = File.OpenRead(path);
        return await import.ImportAsync(stream, profile, Path.GetFileName(path), CancellationToken.None);
    }

    private static async Task<ReconciliationRun> RunAsync(ReconApiFactory factory)
    {
        using var scope = factory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ReconciliationOrchestrator>();
        return await orchestrator.RunAsync(null, null, null, "integration-test", CancellationToken.None);
    }

    [Fact]
    public async Task Generated_dataset_reconciles_to_the_manifest_counts()
    {
        using var factory = new ReconApiFactory();
        var (files, manifest) = await GenerateAsync(2_000, 42);
        try
        {
            await ImportAsync(factory, files.InternalCsv, BuiltInProfiles.InternalCsv);
            await ImportAsync(factory, files.ExternalCsv, BuiltInProfiles.ExternalCsv);

            var run = await RunAsync(factory);

            Assert.True(run.BalanceAssertionPassed, run.BalanceAssertionDetail);
            Assert.Equal(manifest.ExpectedAutoMatches, run.MatchCount);
            Assert.Equal(manifest.ExpectedExceptions, run.ExceptionCount);
        }
        finally
        {
            Directory.Delete(files.Directory, recursive: true);
        }
    }

    private static async Task<int> OpenExceptionCountAsync(ReconApiFactory factory)
    {
        using var scope = factory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IExceptionStore>();
        return (await store.GetOpenAsync()).Count;
    }

    [Fact]
    public async Task Re_running_the_same_inputs_is_idempotent()
    {
        using var factory = new ReconApiFactory();
        var (files, manifest) = await GenerateAsync(1_000, 7);
        try
        {
            await ImportAsync(factory, files.InternalCsv, BuiltInProfiles.InternalCsv);
            await ImportAsync(factory, files.ExternalCsv, BuiltInProfiles.ExternalCsv);

            var first = await RunAsync(factory);
            var openAfterFirst = await OpenExceptionCountAsync(factory);

            var second = await RunAsync(factory);
            var openAfterSecond = await OpenExceptionCountAsync(factory);

            // The first run flags exactly the generator's ground-truth defect count.
            Assert.Equal(manifest.ExpectedExceptions, first.ExceptionCount);
            Assert.Equal(manifest.ExpectedExceptions, openAfterFirst);

            // Idempotency guarantee: re-running the identical inputs neither duplicates exceptions
            // nor grows the open queue. Matched records (including fee-variance / status-mismatch
            // pairs) are archived out of the working set after the first run per the carry-forward
            // design, so the second run only re-evaluates the still-open records. The total open
            // exception set is therefore stable across re-runs and is never double-counted.
            Assert.Equal(openAfterFirst, openAfterSecond);
            Assert.Equal(manifest.ExpectedExceptions, openAfterSecond);
            Assert.True(
                second.ExceptionCount <= first.ExceptionCount,
                $"re-run in-scope exceptions ({second.ExceptionCount}) must not exceed the first run ({first.ExceptionCount})");
        }
        finally
        {
            Directory.Delete(files.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task Unmatched_records_are_carried_forward_to_the_next_run()
    {
        using var factory = new ReconApiFactory();
        var (files, _) = await GenerateAsync(1_000, 11);
        try
        {
            await ImportAsync(factory, files.InternalCsv, BuiltInProfiles.InternalCsv);
            await ImportAsync(factory, files.ExternalCsv, BuiltInProfiles.ExternalCsv);

            var first = await RunAsync(factory);
            var second = await RunAsync(factory);

            var expectedCarry =
                (first.InternalRecordCount - first.MatchedInternalCount) +
                (first.ExternalRecordCount - first.MatchedExternalCount);

            Assert.True(expectedCarry > 0, "the dataset should leave some records unmatched");
            Assert.Equal(expectedCarry, second.CarriedForwardCount);
        }
        finally
        {
            Directory.Delete(files.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task Large_file_streams_in_and_completes_within_a_sane_bound()
    {
        using var factory = new ReconApiFactory();
        var (files, manifest) = await GenerateAsync(50_000, 42);
        try
        {
            Assert.True(manifest.TotalInternalRows >= 50_000, "expected at least 50k internal rows");

            var sw = Stopwatch.StartNew();
            var result = await ImportAsync(factory, files.InternalCsv, BuiltInProfiles.InternalCsv);
            sw.Stop();

            Assert.Equal(manifest.TotalInternalRows, result.TotalRows);
            Assert.Equal(manifest.TotalInternalRows, result.AcceptedRows);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"import took {sw.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            Directory.Delete(files.Directory, recursive: true);
        }
    }
}
