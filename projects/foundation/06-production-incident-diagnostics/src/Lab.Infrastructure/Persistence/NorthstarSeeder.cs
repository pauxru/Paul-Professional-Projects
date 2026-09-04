using Lab.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lab.Infrastructure.Persistence;

public static class NorthstarSeeder
{
    public static async Task SeedAsync(LogisticsDbContext dbContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await dbContext.Orders.AnyAsync(cancellationToken))
        {
            return;
        }

        var customer = new Customer("Northstar Logistics (fictional)", "dispatch@northstar.example");
        var secondCustomer = new Customer("Acme Manufacturing (fictional)", "logistics@acme.example");
        var delivered = new LogisticsOrder(customer.Id, "NS-10001", "Nairobi distribution centre", now.AddDays(-2));
        delivered.Dispatch("TRK-10001", now.AddDays(-1));
        delivered.MarkDelivered(now.AddHours(-4));
        var inTransit = new LogisticsOrder(secondCustomer.Id, "NS-10002", "Mombasa port terminal", now.AddHours(-16));
        inTransit.Dispatch("TRK-10002", now.AddHours(-12));
        var booked = new LogisticsOrder(customer.Id, "NS-10003", "Kisumu warehouse", now.AddHours(-2));

        dbContext.Customers.AddRange(customer, secondCustomer);
        dbContext.Orders.AddRange(delivered, inTransit, booked);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public static async Task EnsureCreatedAndSeedAsync(IServiceProvider services, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await SeedAsync(dbContext, now, cancellationToken);
    }
}
