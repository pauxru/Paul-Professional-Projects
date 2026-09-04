using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Ports;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.Api.Hosting;

public sealed class GracefulShutdownService(
    RequestDrainTracker drainTracker,
    IAsyncDelay delay,
    IHostApplicationLifetime lifetime,
    IOptions<OperationalOptions> options,
    ILogger<GracefulShutdownService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStopping.Register(
            () => logger.LogInformation("Host stopping signal received; graceful drain will begin."));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        drainTracker.BeginDraining();
        var settings = options.Value;
        logger.LogInformation(
            "Connection draining started with {InFlight} in-flight requests",
            drainTracker.InFlight);

        await delay.DelayAsync(
            TimeSpan.FromSeconds(settings.PreStopDelaySeconds),
            cancellationToken);

        var drained = await drainTracker.WaitForZeroAsync(
            TimeSpan.FromSeconds(settings.DrainTimeoutSeconds),
            cancellationToken);

        if (drained)
        {
            logger.LogInformation("All in-flight requests completed before shutdown.");
        }
        else
        {
            logger.LogWarning(
                "Drain timeout elapsed with {InFlight} requests still in flight",
                drainTracker.InFlight);
        }
    }
}

public sealed class LifecycleLoggingService(
    IHostApplicationLifetime lifetime,
    ILogger<LifecycleLoggingService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(
            () => logger.LogInformation("Application lifecycle state: started"));
        lifetime.ApplicationStopping.Register(
            () => logger.LogInformation("Application lifecycle state: stopping"));
        lifetime.ApplicationStopped.Register(
            () => logger.LogInformation("Application lifecycle state: stopped"));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
