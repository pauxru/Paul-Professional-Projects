namespace NotificationPlatform.Infrastructure.Delivery;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Application.Options;

public sealed class DeliveryWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly NotificationOptions _options;
    private readonly ILogger<DeliveryWorker> _logger;

    public DeliveryWorker(IServiceProvider serviceProvider, IOptions<NotificationOptions> options, ILogger<DeliveryWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Delivery worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<IDeliveryPipeline>();
                var processed = await pipeline.ProcessDueAsync(_options.WorkerBatchSize, stoppingToken).ConfigureAwait(false);
                if (processed == 0)
                {
                    await Task.Delay(_options.PollIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delivery worker loop error");
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
        }
        _logger.LogInformation("Delivery worker stopped");
    }
}

// A minimal DI extension so BackgroundService can resolve scoped services conveniently.
internal static class ScopeExt
{
    public static IServiceScope CreateScope(this IServiceProvider sp) => sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
}
