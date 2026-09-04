using Microsoft.Extensions.Options;
using Northstar.Secrets.Application;

namespace Northstar.Secrets.Api.Services;

public sealed class RotationSchedulerBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<RotationConfiguration> options,
    IHostEnvironment environment,
    ILogger<RotationSchedulerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("Testing"))
        {
            return;
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(options.Value.SchedulerIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<AutomaticRotationScheduler>();
                var requested = await scheduler.RunOnceAsync(stoppingToken);
                if (requested.Count > 0)
                {
                    logger.LogInformation(
                        "Automatic scheduler requested {RotationCount} rotations",
                        requested.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic rotation scheduler iteration failed");
            }
        }
    }
}
