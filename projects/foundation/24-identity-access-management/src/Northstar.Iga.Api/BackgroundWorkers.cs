using Microsoft.Extensions.Options;
using Northstar.Iga.Application;

namespace Northstar.Iga.Api;

public sealed class ElevationExpiryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ElevationExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
                await service.ExpireElevationsAsync("system:elevation-expiry", Guid.NewGuid().ToString("N"), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "JIT elevation expiry cycle failed");
            }
        }
    }
}

public sealed class CampaignDeadlineWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CampaignDeadlineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
                await service.AutoRevokeOverdueCampaignsAsync(
                    "system:campaign-deadline",
                    Guid.NewGuid().ToString("N"),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Certification deadline cycle failed");
            }
        }
    }
}

public sealed class ApprovalEscalationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ApprovalEscalationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<Northstar.Iga.Infrastructure.IgaDbContext>();
                var securityApprover = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .FirstOrDefaultAsync(
                        db.Users.Where(x => x.Status == Domain.IdentityStatus.Active &&
                                            x.JobTitle.Contains("Security"))
                            .Select(x => x.Id),
                        stoppingToken);
                if (securityApprover == Guid.Empty)
                {
                    continue;
                }

                var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
                await service.EscalateOverdueApprovalsAsync(
                    securityApprover,
                    "system:approval-escalation",
                    Guid.NewGuid().ToString("N"),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Approval escalation cycle failed");
            }
        }
    }
}
