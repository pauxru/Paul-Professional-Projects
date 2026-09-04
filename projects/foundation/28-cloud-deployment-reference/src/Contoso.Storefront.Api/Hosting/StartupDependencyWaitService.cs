using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Ports;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.Api.Hosting;

public sealed class StartupDependencyWaitService(
    IServiceScopeFactory scopeFactory,
    StartupState state,
    IAsyncDelay delay,
    IHostApplicationLifetime lifetime,
    IOptions<OperationalOptions> options,
    ILogger<StartupDependencyWaitService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        for (var attempt = 1; attempt <= settings.DependencyWaitMaxAttempts; attempt++)
        {
            StartupProbeResult result;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var probe = scope.ServiceProvider.GetRequiredService<IStartupDependencyProbe>();
                result = await probe.CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                result = StartupProbeResult.Unhealthy(exception.Message);
            }

            if (result.IsHealthy)
            {
                logger.LogInformation(
                    "Startup dependency checks succeeded on attempt {Attempt}",
                    attempt);
                state.MarkReady(result.Detail);
                return;
            }

            logger.LogWarning(
                "Startup dependency attempt {Attempt}/{MaximumAttempts} failed: {Detail}",
                attempt,
                settings.DependencyWaitMaxAttempts,
                result.Detail);

            if (attempt == settings.DependencyWaitMaxAttempts)
            {
                state.MarkFailed(result.Detail);
                lifetime.StopApplication();
                return;
            }

            var delayMs = settings.DependencyWaitInitialDelayMs * Math.Pow(2, attempt - 1);
            await delay.DelayAsync(TimeSpan.FromMilliseconds(delayMs), stoppingToken);
        }
    }
}
