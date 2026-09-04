using Lab.Application.Contracts;
using Lab.Domain.Entities;

namespace Lab.Application.Abstractions;

public interface IOrderRepository
{
    Task<PagedResult<OrderSummary>> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<OrderDetails?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<OrderDetails> AddAsync(Customer customer, LogisticsOrder order, CancellationToken cancellationToken);
}
