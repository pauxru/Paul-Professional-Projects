namespace Contoso.Payments.Application.Catalog;

public sealed record ProductDto(Guid Id, string Sku, string Name, decimal Price, string Currency, bool IsActive);

public sealed record CreateProductRequest(string Sku, string Name, decimal Price, string Currency, int InitialStock);
