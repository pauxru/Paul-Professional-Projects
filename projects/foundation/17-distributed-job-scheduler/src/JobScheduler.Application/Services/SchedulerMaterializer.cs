using JobScheduler.Application.Abstractions;
using JobScheduler.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Application.Services;

/// <summary>
/// Leader-only duty: turns due trigger occurrences into concrete Pending runs. Each occurrence is
/// keyed by a deterministic idempotency key (<c>{defId}:{instant}</c>) so a brief leader overlap
/// cannot create duplicate runs. First sighting of a definition only anchors its clock — it does
/// not backfill history.
/// </summary>
public sealed class SchedulerMaterializer(
    IJobDefinitionStore definitions,
    IJobRunStore runs,
    IClock clock,
    ILogger<SchedulerMaterializer> logger)
{
    public async Task<int> MaterializeAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var scheduled = await definitions.ListEnabledScheduledAsync(ct);
        int created = 0;

        foreach (var def in scheduled)
        {
            if (def.TriggerType == Domain.TriggerType.Manual)
            {
                continue;
            }

            if (def.LastFireAt is null)
            {
                def.MarkFired(now); // anchor only; never backfill on first sight
                continue;
            }

            var schedule = def.BuildSchedule();
            var due = schedule.ComputeDue(def.LastFireAt.Value, now);
            if (due.Count > 0)
            {
                var recovered = new HashSet<DateTimeOffset>(due.Recovered);
                foreach (var occ in due.All)
                {
                    var key = $"{def.Id}:{occ.UtcDateTime:O}";
                    if (await runs.ExistsByIdempotencyKeyAsync(key, ct))
                    {
                        continue;
                    }

                    var kind = recovered.Contains(occ)
                        ? $"{def.TriggerType.ToString().ToLowerInvariant()}-misfire"
                        : def.TriggerType.ToString().ToLowerInvariant();

                    var run = JobRun.Create(def, occ, now, key, Guid.NewGuid().ToString("N"), kind);
                    await runs.AddAsync(run, ct);
                    def.MarkFired(occ);
                    created++;
                }
            }

            def.MarkFired(now); // advance the anchor so the window is never re-evaluated
        }

        if (created > 0)
        {
            logger.LogInformation("Materialised {Count} run(s) from schedules at {Now:o}.", created, now);
        }

        await definitions.SaveChangesAsync(ct);
        return created;
    }
}
