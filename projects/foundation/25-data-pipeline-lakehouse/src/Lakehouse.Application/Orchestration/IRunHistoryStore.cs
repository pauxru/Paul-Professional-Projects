namespace Lakehouse.Application.Orchestration;

/// <summary>Persists run history so timings, row counts and outcomes are queryable after the fact.</summary>
public interface IRunHistoryStore
{
    void Save(RunRecord record);
    IReadOnlyList<RunRecord> Recent(int limit = 50);
    RunRecord? Latest();
}
