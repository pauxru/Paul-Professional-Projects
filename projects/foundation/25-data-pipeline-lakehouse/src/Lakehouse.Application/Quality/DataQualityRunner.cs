using Lakehouse.Application.Abstractions;
using Lakehouse.Domain.Data;
using Lakehouse.Domain.Quality;

namespace Lakehouse.Application.Quality;

/// <summary>
/// Runs a <see cref="QualityGate"/> against live lake tables, producing and persisting one consolidated
/// report. The runner is pure orchestration: it reads rows, evaluates each declarative expectation and
/// records the outcome. Deciding what to do about a blocking failure is the caller's job (see
/// <see cref="CircuitBreaker"/>), which keeps evaluation and control-flow cleanly separated.
/// </summary>
public sealed class DataQualityRunner(ILakehouse lake, IClock clock, IDataQualityStore store)
{
    public DataQualityReport Evaluate(QualityGate gate, string runId)
    {
        var results = new List<ExpectationResult>();
        foreach (var dataset in gate.Datasets)
        {
            var t = lake.Table(dataset.Table);
            IReadOnlyList<Row> rows = t.Exists ? t.Scan() : Array.Empty<Row>();
            foreach (var expectation in dataset.Expectations)
                results.Add(expectation.Evaluate(rows));
        }

        var report = new DataQualityReport(gate.Name, runId, clock.UtcNow, results);
        store.Save(report);
        return report;
    }
}
