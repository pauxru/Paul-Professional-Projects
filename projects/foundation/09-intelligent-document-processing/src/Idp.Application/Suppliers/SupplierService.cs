using Idp.Application.Abstractions;
using Idp.Domain.Suppliers;

namespace Idp.Application.Suppliers;

/// <summary>Supplier master-data use cases (list, get, create).</summary>
public sealed class SupplierService
{
    private readonly ISupplierRepository _suppliers;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SupplierService(
        ISupplierRepository suppliers, IUnitOfWork unitOfWork, IClock clock)
    {
        _suppliers = suppliers;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<IReadOnlyList<Supplier>> ListAsync(CancellationToken ct = default) =>
        _suppliers.GetAllAsync(ct);

    public Task<Supplier?> GetAsync(Guid id, CancellationToken ct = default) =>
        _suppliers.GetAsync(id, ct);

    public async Task<Supplier> CreateAsync(
        string name, string? taxId, string? currency, IEnumerable<string>? aliases,
        string? bankAccount, CancellationToken ct = default)
    {
        var supplier = new Supplier(name, taxId, currency, _clock.UtcNow, aliases, bankAccount);
        _suppliers.Add(supplier);
        await _unitOfWork.SaveChangesAsync(ct);
        return supplier;
    }
}
