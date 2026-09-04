using ReconEngine.Application.DataGeneration;
using ReconEngine.Application.Reconciliation;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.UnitTests.DataGeneration;

/// <summary>
/// Proves the synthetic generator and the reconciliation engine agree: the engine must detect exactly
/// the number of each defect class the generator recorded in its manifest. This is the anchor that lets
/// every other count-based test trust the generator.
/// </summary>
public sealed class GeneratorExactCountsTests
{
    private const long HighThreshold = 1_000_000;

    [Fact]
    public void Engine_detects_exactly_the_injected_defect_counts()
    {
        var dataset = new SyntheticDataGenerator().Generate(GenerationOptions.All(5_000, 42));
        var calculator = ReconciliationCalculator.CreateDefault();

        var result = calculator.Calculate(
            dataset.Internal, dataset.External, MatchingRuleSetDefinition.Default, HighThreshold);

        Assert.True(result.BalancePassed, result.BalanceDetail);

        var byType = result.Exceptions
            .GroupBy(e => e.Type.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (type, expected) in dataset.Manifest.ExpectedExceptionsByType)
        {
            var actual = byType.TryGetValue(type, out var c) ? c : 0;
            Assert.True(expected == actual, $"{type}: expected {expected} but engine produced {actual}.");
        }

        Assert.Equal(dataset.Manifest.ExpectedExceptions, result.Exceptions.Count);
        Assert.Equal(dataset.Manifest.ExpectedAutoMatches, result.MatchCount);
    }
}
