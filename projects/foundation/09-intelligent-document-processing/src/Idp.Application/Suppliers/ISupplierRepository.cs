using Idp.Domain.Suppliers;

namespace Idp.Application.Suppliers;

public interface ISupplierRepository
{
    void Add(Supplier supplier);
    Task<Supplier?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Supplier>> GetAllAsync(CancellationToken ct = default);
}
