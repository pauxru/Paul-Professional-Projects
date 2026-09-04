namespace NotificationPlatform.Application.Fairness;

public interface ITenantFairnessScheduler
{
    /// <summary>
    /// Given a set of pending items grouped by tenant, returns a batch of item ids in fair order.
    /// Uses weighted round-robin: each tenant's turn is weighted by its FairnessWeight, so no
    /// tenant can starve others regardless of backlog size.
    /// </summary>
    List<Guid> PickBatch(IReadOnlyList<TenantBacklog> backlogs, int batchSize);
}

public sealed record TenantBacklog(Guid TenantId, double Weight, IReadOnlyList<Guid> PendingIds);

public sealed class TenantFairnessScheduler : ITenantFairnessScheduler
{
    public List<Guid> PickBatch(IReadOnlyList<TenantBacklog> backlogs, int batchSize)
    {
        if (backlogs.Count == 0 || batchSize <= 0) return new List<Guid>();

        var queues = backlogs
            .Where(b => b.PendingIds.Count > 0)
            .Select(b => new Queue<Guid>(b.PendingIds))
            .ToList();
        var weights = backlogs
            .Where(b => b.PendingIds.Count > 0)
            .Select(b => b.Weight)
            .ToList();

        var chosen = new List<Guid>(batchSize);
        var credits = new double[queues.Count];
        while (chosen.Count < batchSize)
        {
            bool anyProgress = false;
            for (int i = 0; i < queues.Count && chosen.Count < batchSize; i++)
            {
                if (queues[i].Count == 0) continue;
                credits[i] += weights[i];
                while (credits[i] >= 1.0 && queues[i].Count > 0 && chosen.Count < batchSize)
                {
                    chosen.Add(queues[i].Dequeue());
                    credits[i] -= 1.0;
                    anyProgress = true;
                }
            }
            if (!anyProgress) break;
        }

        return chosen;
    }
}
