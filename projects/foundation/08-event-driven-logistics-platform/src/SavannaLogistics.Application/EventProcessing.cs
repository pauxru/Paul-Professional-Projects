using System.Collections.Concurrent;
using System.Threading.Channels;
using SavannaLogistics.Domain;

namespace SavannaLogistics.Application;

public sealed record WatermarkBatch(
    IReadOnlyList<VehiclePing> Ready,
    IReadOnlyList<VehiclePing> Late);

public sealed class WatermarkReorderBuffer
{
    private sealed class VehicleBuffer
    {
        public readonly SortedDictionary<long, VehiclePing> Events = [];
        public DateTimeOffset MaxEventTime = DateTimeOffset.MinValue;
        public DateTimeOffset Watermark = DateTimeOffset.MinValue;
        public DateTimeOffset LastScannedWatermark = DateTimeOffset.MinValue;
        public DateTimeOffset LastTouchedAt = DateTimeOffset.MinValue;
        public long LastEmittedSequence = -1;
    }

    private readonly ConcurrentDictionary<Guid, VehicleBuffer> _buffers = [];
    private readonly TimeSpan _allowedLateness;

    public WatermarkReorderBuffer(TimeSpan allowedLateness)
    {
        if (allowedLateness < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(allowedLateness));
        _allowedLateness = allowedLateness;
    }

    public WatermarkBatch Add(VehiclePing ping)
    {
        var state = _buffers.GetOrAdd(ping.VehicleId, _ => new VehicleBuffer());
        lock (state)
        {
            if (ping.SequenceNumber <= state.LastEmittedSequence || ping.DeviceTimestamp <= state.Watermark)
            {
                return new WatermarkBatch([], [ping]);
            }

            state.Events[ping.SequenceNumber] = ping;
            state.MaxEventTime = Max(state.MaxEventTime, ping.DeviceTimestamp);
            state.LastTouchedAt = Max(state.LastTouchedAt, ping.IngestTimestamp);
            state.Watermark = state.MaxEventTime - _allowedLateness;
            if (state.Watermark <= state.LastScannedWatermark)
            {
                return new WatermarkBatch([], []);
            }

            state.LastScannedWatermark = state.Watermark;
            return FlushThroughWatermark(state);
        }
    }

    public WatermarkBatch FlushExpired(DateTimeOffset processingTime)
    {
        var ready = new List<VehiclePing>();
        var late = new List<VehiclePing>();
        foreach (var state in _buffers.Values)
        {
            lock (state)
            {
                if (state.Events.Count == 0 || processingTime - state.LastTouchedAt < _allowedLateness)
                {
                    continue;
                }

                Drain(state, state.Events.Keys.ToArray(), ready, late);
            }
        }

        return new WatermarkBatch(ready, late);
    }

    public WatermarkBatch DrainAll()
    {
        var ready = new List<VehiclePing>();
        var late = new List<VehiclePing>();
        foreach (var state in _buffers.Values)
        {
            lock (state)
            {
                Drain(state, state.Events.Keys.ToArray(), ready, late);
            }
        }

        return new WatermarkBatch(ready, late);
    }

    private static WatermarkBatch FlushThroughWatermark(VehicleBuffer state)
    {
        var ready = new List<VehiclePing>();
        var late = new List<VehiclePing>();
        var keys = state.Events
            .Where(pair => pair.Value.DeviceTimestamp <= state.Watermark)
            .Select(pair => pair.Key)
            .ToArray();
        Drain(state, keys, ready, late);
        return new WatermarkBatch(ready, late);
    }

    private static void Drain(
        VehicleBuffer state,
        IReadOnlyList<long> keys,
        ICollection<VehiclePing> ready,
        ICollection<VehiclePing> late)
    {
        foreach (var key in keys.Order())
        {
            var ping = state.Events[key];
            state.Events.Remove(key);
            if (ping.SequenceNumber <= state.LastEmittedSequence)
            {
                late.Add(ping);
                continue;
            }

            ready.Add(ping);
            state.LastEmittedSequence = ping.SequenceNumber;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
}

public sealed record BusDeadLetter<T>(T Message, Exception Exception, DateTimeOffset FailedAt);

public sealed class PartitionedChannelBus<T>
{
    private readonly Channel<T>[] _channels;
    private readonly Func<T, Guid> _partitionKey;
    private readonly Func<T, CancellationToken, Task> _handler;
    private readonly Func<BusDeadLetter<T>, CancellationToken, Task>? _deadLetterHandler;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentQueue<BusDeadLetter<T>> _deadLetters = [];
    private readonly List<Task> _workers = [];
    private long _enqueued;
    private long _processed;
    private long _failed;
    private int _started;

    public PartitionedChannelBus(
        int partitions,
        int capacityPerPartition,
        Func<T, Guid> partitionKey,
        Func<T, CancellationToken, Task> handler,
        Func<BusDeadLetter<T>, CancellationToken, Task>? deadLetterHandler = null,
        TimeProvider? timeProvider = null)
    {
        if (partitions <= 0) throw new ArgumentOutOfRangeException(nameof(partitions));
        if (capacityPerPartition <= 0) throw new ArgumentOutOfRangeException(nameof(capacityPerPartition));
        _partitionKey = partitionKey;
        _handler = handler;
        _deadLetterHandler = deadLetterHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channels = Enumerable.Range(0, partitions)
            .Select(_ => Channel.CreateBounded<T>(new BoundedChannelOptions(capacityPerPartition)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            }))
            .ToArray();
    }

    public long ConsumerLag => Math.Max(0, Interlocked.Read(ref _enqueued) -
                                          Interlocked.Read(ref _processed) -
                                          Interlocked.Read(ref _failed));
    public long DeadLetterCount => Interlocked.Read(ref _failed);
    public IReadOnlyCollection<BusDeadLetter<T>> DeadLetters => _deadLetters.ToArray();

    public void Start(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        foreach (var channel in _channels)
        {
            _workers.Add(Task.Run(() => ConsumeAsync(channel.Reader, cancellationToken), CancellationToken.None));
        }
    }

    public async ValueTask PublishAsync(T message, CancellationToken cancellationToken = default)
    {
        var channel = _channels[GetPartition(message)];
        await channel.Writer.WriteAsync(message, cancellationToken);
        Interlocked.Increment(ref _enqueued);
    }

    public bool TryPublish(T message)
    {
        var channel = _channels[GetPartition(message)];
        if (!channel.Writer.TryWrite(message))
        {
            return false;
        }

        Interlocked.Increment(ref _enqueued);
        return true;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        foreach (var channel in _channels)
        {
            channel.Writer.TryComplete();
        }

        if (_workers.Count > 0)
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken);
        }
    }

    private async Task ConsumeAsync(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await _handler(message, cancellationToken);
                Interlocked.Increment(ref _processed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var deadLetter = new BusDeadLetter<T>(message, exception, _timeProvider.GetUtcNow());
                _deadLetters.Enqueue(deadLetter);
                Interlocked.Increment(ref _failed);
                if (_deadLetterHandler is not null)
                {
                    await _deadLetterHandler(deadLetter, cancellationToken);
                }
            }
        }
    }

    private int GetPartition(T message)
    {
        var hash = _partitionKey(message).GetHashCode() & int.MaxValue;
        return hash % _channels.Length;
    }
}
