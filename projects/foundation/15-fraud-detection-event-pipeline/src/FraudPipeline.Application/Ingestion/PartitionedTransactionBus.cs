using System.Threading.Channels;
using FraudPipeline.Domain.Entities;

namespace FraudPipeline.Application.Ingestion;

public sealed class IngestionOptions
{
    public int PartitionCount { get; set; } = 8;
    public int PerPartitionCapacity { get; set; } = 1024;
}

/// <summary>
/// Partitioned in-process bus. A card id / customer id is hashed to a partition,
/// so all events for a given entity land in the same partition and their processing
/// order is preserved. Different partitions process in parallel.
/// </summary>
public sealed class PartitionedTransactionBus : IAsyncDisposable
{
    private readonly Channel<Transaction>[] _channels;
    private readonly IngestionOptions _options;

    public int PartitionCount => _options.PartitionCount;

    public PartitionedTransactionBus(IngestionOptions options)
    {
        _options = options;
        _channels = new Channel<Transaction>[options.PartitionCount];
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i] = Channel.CreateBounded<Transaction>(new BoundedChannelOptions(options.PerPartitionCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        }
    }

    public int PartitionOf(string key)
    {
        // Deterministic hash: stable string hash so re-runs pick the same partition.
        uint h = 2166136261;
        foreach (var c in key)
        {
            h ^= c;
            h *= 16777619;
        }
        return (int)(h % (uint)PartitionCount);
    }

    public async ValueTask WriteAsync(Transaction txn, CancellationToken ct)
    {
        var p = PartitionOf(txn.CardId);
        await _channels[p].Writer.WriteAsync(txn, ct);
    }

    public ChannelReader<Transaction> Reader(int partition) => _channels[partition].Reader;

    public int Lag(int partition) => _channels[partition].Reader.Count;
    public int TotalLag()
    {
        int total = 0;
        for (int i = 0; i < _channels.Length; i++) total += _channels[i].Reader.Count;
        return total;
    }

    public void Complete()
    {
        foreach (var c in _channels) c.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
