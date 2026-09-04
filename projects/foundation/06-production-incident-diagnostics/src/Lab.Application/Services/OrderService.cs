using Lab.Application.Abstractions;
using Lab.Application.Contracts;
using Lab.Domain.Entities;

namespace Lab.Application.Services;

public sealed class OrderService(IOrderRepository repository, IClock clock)
{
    public Task<PagedResult<OrderSummary>> ListAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        repository.ListAsync(page, pageSize, cancellationToken);

    public Task<OrderDetails?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        repository.GetAsync(id, cancellationToken);

    public Task<OrderDetails> BookAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var customer = new Customer(request.CustomerName ?? string.Empty, request.CustomerEmail ?? string.Empty);
        var order = new LogisticsOrder(
            customer.Id,
            request.Reference ?? string.Empty,
            request.Destination ?? string.Empty,
            clock.UtcNow);

        return repository.AddAsync(customer, order, cancellationToken);
    }
}
