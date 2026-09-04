using Microsoft.EntityFrameworkCore;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Domain.Catalog;
using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Inventory;

namespace Contoso.Payments.Application.Catalog;

public sealed class CatalogService
{
    private readonly IAppDbContext _db;
    private readonly IIdGenerator _ids;

    public CatalogService(IAppDbContext db, IIdGenerator ids)
    {
        _db = db;
        _ids = ids;
    }

    public async Task<AppResult<ProductDto>> CreateAsync(CreateProductRequest req, CancellationToken ct)
    {
        try
        {
            var product = new Product(_ids.NewGuid(), req.Sku, req.Name, Money.Of(req.Price, req.Currency));
            _db.Products.Add(product);
            _db.Inventory.Add(new InventoryItem(product.Id, product.Sku, Math.Max(0, req.InitialStock)));
            await _db.SaveChangesAsync(ct);
            return AppResult<ProductDto>.Ok(new ProductDto(product.Id, product.Sku, product.Name,
                product.Price.Amount, product.Price.Currency, product.IsActive));
        }
        catch (DomainException dex)
        {
            return AppResult<ProductDto>.Fail(dex.Code, dex.Message, 422);
        }
    }

    public async Task<PagedResult<ProductDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Products.AsNoTracking().OrderBy(p => p.Sku);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.Price.Amount, p.Price.Currency, p.IsActive))
            .ToListAsync(ct);
        return new PagedResult<ProductDto>(items, page, pageSize, total);
    }
}
