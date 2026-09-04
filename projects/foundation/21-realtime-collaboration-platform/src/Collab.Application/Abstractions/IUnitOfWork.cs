namespace Collab.Application.Abstractions;

/// <summary>Commits all pending changes tracked in the current unit of work as one transaction.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
