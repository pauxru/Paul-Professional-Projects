namespace IntegrationHub.Application;

public sealed record BatchProcessResult(int Processed, int Quarantined, int NextIndex);

public sealed class CheckpointedBatchProcessor(
    ICheckpointStore checkpoints,
    IDeadLetterStore deadLetters,
    IIdGenerator ids,
    IClock clock)
{
    public async Task<BatchProcessResult> ProcessAsync<T>(
        Guid runId,
        string stepId,
        string batchKey,
        IReadOnlyList<T> records,
        Func<T, int, CancellationToken, Task> process,
        Func<T, int, string> recordKey,
        Func<T, string> serialize,
        Func<Exception, bool> isPoison,
        CancellationToken cancellationToken = default)
    {
        var start = await checkpoints.GetNextIndexAsync(runId, stepId, batchKey, cancellationToken);
        var processed = 0;
        var quarantined = 0;
        for (var index = start; index < records.Count; index++)
        {
            try
            {
                await process(records[index], index, cancellationToken);
                processed++;
            }
            catch (Exception ex) when (isPoison(ex))
            {
                await deadLetters.AddAsync(new DeadLetterItem(
                    ids.NewId(),
                    runId,
                    stepId,
                    batchKey,
                    recordKey(records[index], index),
                    serialize(records[index]),
                    ex.ToString(),
                    Domain.DeadLetterStatus.Pending,
                    clock.UtcNow), cancellationToken);
                quarantined++;
            }

            await checkpoints.SaveAsync(runId, stepId, batchKey, index + 1, cancellationToken);
        }

        return new BatchProcessResult(processed, quarantined, records.Count);
    }
}
