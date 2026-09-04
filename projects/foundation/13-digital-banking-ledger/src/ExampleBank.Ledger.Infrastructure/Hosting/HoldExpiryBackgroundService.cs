using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Holds;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExampleBank.Ledger.Infrastructure.Hosting;

/// <summary>
/// Periodically expires holds that have passed their expiry time, releasing the held funds back to
/// the available balance. Uses <see cref="IClock"/> so tests can drive expiry deterministically.
/// </summary>
public sealed class HoldExpiryBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IClock _clock;
    private readonly ILogger<HoldExpiryBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public HoldExpiryBackgroundService(
        IServiceProvider services,
        IClock clock,
        ILogger<HoldExpiryBackgroundService> logger)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var holds = scope.ServiceProvider.GetRequiredService<HoldService>();
                int expired = await holds.ExpireDueHoldsAsync(_clock.UtcNow, stoppingToken);
                if (expired > 0)
                {
                    _logger.LogInformation("Expired {Count} hold(s).", expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hold expiry sweep failed.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
