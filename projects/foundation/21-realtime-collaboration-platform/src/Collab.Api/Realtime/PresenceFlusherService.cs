using Collab.Application.Abstractions;
using Collab.Application.Contracts;
using Collab.Application.Options;
using Collab.Domain.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Collab.Api.Realtime;

/// <summary>
/// Coalesces presence changes into at most one broadcast per document per tick, so a storm of cursor
/// moves (e.g. 20/second) collapses to a handful of broadcasts. Also enforces presence liveness:
/// stale participants are marked idle and expired ones evicted, driven by heartbeat timestamps.
/// </summary>
public sealed class PresenceFlusherService(
    IHubContext<CollaborationHub> hub,
    IPresenceStore presence,
    IClock clock,
    IOptions<CollaborationOptions> options,
    ILogger<PresenceFlusherService> logger) : BackgroundService
{
    private readonly CollaborationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMilliseconds(_options.PresenceThrottleMs);
        using var timer = new PeriodicTimer(period);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = clock.UtcNow;
                presence.ApplyLiveness(
                    now.AddSeconds(-_options.PresenceIdleSeconds),
                    now.AddSeconds(-_options.PresenceEvictSeconds));

                foreach (var documentId in presence.DrainDirty())
                {
                    var snapshot = new PresenceSnapshot(documentId, presence.Participants(documentId));
                    await hub.Clients.Group(HubGroups.ForDocument(documentId))
                        .SendAsync("PresenceChanged", snapshot, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Presence flush failed");
            }
        }
    }
}
