using Lab.Application.Abstractions;
using Lab.Application.Contracts;
using Lab.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lab.Infrastructure.Persistence;

public sealed class EfOrderRepository(LogisticsDbContext dbContext) : IOrderRepository
{
    public async Task<PagedResult<OrderSummary>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var total = await dbContext.Orders.CountAsync(cancellationToken);
        var items = await (
                from order in dbContext.Orders.AsNoTracking()
                join customer in dbContext.Customers.AsNoTracking() on order.CustomerId equals customer.Id
                orderby order.CreatedAt descending
                select new OrderSummary(
                    order.Id,
                    order.Reference,
                    customer.Name,
                    order.Destination,
                    order.Status,
                    order.CreatedAt))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<OrderSummary>(
            items,
            page,
            pageSize,
            total,
            total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize));
    }

    public Task<OrderDetails?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        (
            from order in dbContext.Orders.AsNoTracking()
            join customer in dbContext.Customers.AsNoTracking() on order.CustomerId equals customer.Id
            join shipment in dbContext.Shipments.AsNoTracking() on order.Id equals shipment.OrderId into shipments
            from shipment in shipments.DefaultIfEmpty()
            where order.Id == id
            select new OrderDetails(
                order.Id,
                order.Reference,
                customer.Name,
                customer.Email,
                order.Destination,
                order.Status,
                order.CreatedAt,
                shipment == null
                    ? null
                    : new ShipmentResponse(
                        shipment.Id,
                        shipment.TrackingNumber,
                        shipment.DispatchedAt,
                        shipment.DeliveredAt)))
        .SingleOrDefaultAsync(cancellationToken);

    public async Task<OrderDetails> AddAsync(Customer customer, LogisticsOrder order, CancellationToken cancellationToken)
    {
        dbContext.Customers.Add(customer);
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new OrderDetails(
            order.Id,
            order.Reference,
            customer.Name,
            customer.Email,
            order.Destination,
            order.Status,
            order.CreatedAt,
            null);
    }
}
