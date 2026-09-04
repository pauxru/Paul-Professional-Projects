using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Domain;
using Contoso.Storefront.Infrastructure.Messaging;
using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.Api.Hosting;

public sealed class OutboxBackgroundWorker(
    IServiceScopeFactory scopeFactory,
    StartupState startupState,
    IAsyncDelay delay,
    IOptions<OperationalOptions> options,
    ILogger<OutboxBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await startupState.WaitUntilReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                var count = await processor.ProcessOnceAsync(
                    options.Value.WorkerBatchSize,
                    stoppingToken);

                if (count == 0)
                {
                    await delay.DelayAsync(
                        TimeSpan.FromMilliseconds(options.Value.WorkerPollIntervalMs),
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox processing cycle failed");
                await delay.DelayAsync(
                    TimeSpan.FromMilliseconds(options.Value.WorkerPollIntervalMs),
                    stoppingToken);
            }
        }
    }
}

public sealed class DevelopmentDataSeeder(
    IServiceScopeFactory scopeFactory,
    StartupState startupState,
    IHostEnvironment environment,
    IClock clock,
    ILogger<DevelopmentDataSeeder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment())
        {
            return;
        }

        await startupState.WaitUntilReadyAsync(stoppingToken);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorefrontDbContext>();

        if (await dbContext.Products.AnyAsync(stoppingToken))
        {
            return;
        }

        dbContext.Products.AddRange(
            new Product(
                Guid.Parse("0b5e69e7-71b8-47d6-b012-256e85480111"),
                "CONTOSO-COFFEE-001",
                "Contoso Trail Coffee",
                "Fictional demo catalogue item.",
                new Money(18.50m, "USD"),
                clock.UtcNow),
            new Product(
                Guid.Parse("fb8fb6c5-b8e0-43e5-96bf-f44c1abbb222"),
                "CONTOSO-MUG-002",
                "Contoso Camp Mug",
                "Fictional demo catalogue item.",
                new Money(12.00m, "USD"),
                clock.UtcNow));
        await dbContext.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Seeded fictional Contoso Retail demo catalogue.");
    }
}
