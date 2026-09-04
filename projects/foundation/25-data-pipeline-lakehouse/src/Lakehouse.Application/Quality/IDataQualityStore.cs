using Lakehouse.Domain.Quality;

namespace Lakehouse.Application.Quality;

/// <summary>Persists data-quality reports so runs, the dashboard and runbooks can read real results.</summary>
public interface IDataQualityStore
{
    void Save(DataQualityReport report);
    DataQualityReport? Latest(string gate);
    IReadOnlyList<DataQualityReport> Recent(int limit = 50);
}
