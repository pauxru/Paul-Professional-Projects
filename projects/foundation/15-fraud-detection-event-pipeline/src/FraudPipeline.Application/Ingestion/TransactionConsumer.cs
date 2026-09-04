using FraudPipeline.Application.Abstractions;
using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;

namespace FraudPipeline.Application.Ingestion;

/// <summary>
/// Per-partition consumer loop. Runs on its own logical worker so per-entity
/// ordering is preserved. On failure, the event is written to dead-letter and
/// the loop continues.
/// </summary>
public sealed class TransactionConsumer
{
    private readonly PartitionedTransactionBus _bus;
    private readonly ScoringService _scoring;
    private readonly FeatureStoreService _features;
    private readonly ITransactionRepository _txns;
    private readonly IDeadLetterRepository _deadletter;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;

    public TransactionConsumer(
        PartitionedTransactionBus bus,
        ScoringService scoring,
        FeatureStoreService features,
        ITransactionRepository txns,
        IDeadLetterRepository deadletter,
        IIdGenerator ids,
        IClock clock)
    {
        _bus = bus;
        _scoring = scoring;
        _features = features;
        _txns = txns;
        _deadletter = deadletter;
        _ids = ids;
        _clock = clock;
    }

    public async Task RunPartitionAsync(int partition, CancellationToken ct)
    {
        var reader = _bus.Reader(partition);
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var txn))
            {
                try
                {
                    // For each dequeued txn: persist, observe features, and score once.
                    await _txns.AddAsync(txn, ct);
                    await _txns.SaveAsync(ct);
                    _features.Observe(txn);
                    await _scoring.ScoreAsync(txn, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var dle = new DeadLetterEvent(
                        id: _ids.NewGuid(),
                        source: $"partition/{partition}",
                        rawPayload: $"txn ref {txn.TransactionRef}",
                        reason: ex.Message,
                        exceptionType: ex.GetType().Name,
                        receivedAt: _clock.UtcNow);
                    await _deadletter.AddAsync(dle, ct);
                    await _deadletter.SaveAsync(ct);
                }
            }
        }
    }
}
