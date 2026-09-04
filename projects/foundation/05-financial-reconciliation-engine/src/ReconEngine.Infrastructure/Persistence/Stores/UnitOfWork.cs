using ReconEngine.Application.Abstractions;

namespace ReconEngine.Infrastructure.Persistence.Stores;

/// <summary>Commits the EF Core change tracker as one transaction.</summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _db;
    public UnitOfWork(AppDbContext db) => _db = db;

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
