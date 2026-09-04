using System.Collections.Concurrent;
using SavannaLogistics.Application;

namespace SavannaLogistics.UnitTests;

public sealed class PartitionedBusTests
{
    private sealed record Message(Guid VehicleId, int Sequence);

    [Fact]
    public async Task Bus_PreservesPerVehicleOrderingUnderParallelLoad()
    {
        var received = new ConcurrentDictionary<Guid, ConcurrentQueue<int>>();
        var bus = new PartitionedChannelBus<Message>(
            4,
            128,
            message => message.VehicleId,
            async (message, _) =>
            {
                await Task.Delay(message.Sequence % 3);
                received.GetOrAdd(message.VehicleId, _ => new ConcurrentQueue<int>()).Enqueue(message.Sequence);
            });
        bus.Start();
        var vehicles = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(vehicles.Select(async vehicle =>
        {
            for (var sequence = 0; sequence < 100; sequence++)
            {
                await bus.PublishAsync(new Message(vehicle, sequence));
            }
        }));
        await bus.CompleteAsync();

        foreach (var vehicle in vehicles)
        {
            Assert.Equal(Enumerable.Range(0, 100), received[vehicle]);
        }
        Assert.Equal(0, bus.ConsumerLag);
    }

    [Fact]
    public async Task Bus_DifferentPartitions_ProcessInParallel()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        while ((first.GetHashCode() & 1) == (second.GetHashCode() & 1)) second = Guid.NewGuid();
        var simultaneous = 0;
        var maximum = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bus = new PartitionedChannelBus<Message>(
            2,
            4,
            message => message.VehicleId,
            async (_, _) =>
            {
                var current = Interlocked.Increment(ref simultaneous);
                maximum = Math.Max(maximum, current);
                if (current == 2) release.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Interlocked.Decrement(ref simultaneous);
            });
        bus.Start();
        await bus.PublishAsync(new Message(first, 1));
        await bus.PublishAsync(new Message(second, 1));
        await bus.CompleteAsync();
        Assert.Equal(2, maximum);
    }

    [Fact]
    public void Bus_BoundedChannelSignalsBackpressure()
    {
        var bus = new PartitionedChannelBus<Message>(
            1,
            1,
            message => message.VehicleId,
            (_, _) => Task.CompletedTask);
        Assert.True(bus.TryPublish(new Message(Guid.NewGuid(), 1)));
        Assert.False(bus.TryPublish(new Message(Guid.NewGuid(), 2)));
        Assert.Equal(1, bus.ConsumerLag);
    }

    [Fact]
    public async Task Bus_HandlerFailure_GoesToDeadLetterAndContinues()
    {
        var processed = 0;
        var bus = new PartitionedChannelBus<Message>(
            1,
            4,
            message => message.VehicleId,
            (message, _) =>
            {
                if (message.Sequence == 1) throw new InvalidOperationException("synthetic failure");
                Interlocked.Increment(ref processed);
                return Task.CompletedTask;
            });
        bus.Start();
        var vehicle = Guid.NewGuid();
        await bus.PublishAsync(new Message(vehicle, 1));
        await bus.PublishAsync(new Message(vehicle, 2));
        await bus.CompleteAsync();
        Assert.Equal(1, bus.DeadLetterCount);
        Assert.Single(bus.DeadLetters);
        Assert.Equal(1, processed);
    }
}
