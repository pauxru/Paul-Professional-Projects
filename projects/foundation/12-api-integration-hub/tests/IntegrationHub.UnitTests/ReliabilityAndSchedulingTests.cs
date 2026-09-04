using IntegrationHub.Application;
using IntegrationHub.Domain;

namespace IntegrationHub.UnitTests;

public sealed class ReliabilityAndSchedulingTests
{
    [Fact]
    public async Task Retry_TransientFailures_EventuallySucceed()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var retry = new RetryExecutor((delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }, () => 0);
        var result = await retry.ExecuteAsync(
            (_, _) => Task.FromResult(++attempts < 3
                ? new RetryOutcome<string>(false, IsTransient: true, Error: new IOException("temporary"))
                : new RetryOutcome<string>(true, "ok")),
            4,
            TimeSpan.FromMilliseconds(100));
        Assert.Equal("ok", result);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], delays);
    }

    [Fact]
    public async Task Retry_RetryAfter_OverridesBackoff()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var retry = new RetryExecutor((delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        await retry.ExecuteAsync(
            (_, _) => Task.FromResult(++attempts == 1
                ? new RetryOutcome<int>(false, IsTransient: true, RetryAfter: TimeSpan.FromSeconds(7), Error: new IOException())
                : new RetryOutcome<int>(true, 42)),
            2,
            TimeSpan.FromMilliseconds(10));
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays));
    }

    [Fact]
    public async Task CircuitBreaker_ThresholdReached_OpensCircuit()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T00:00:00Z"));
        var breaker = new ConnectorCircuitBreaker(clock, 2, TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<IOException>(() => breaker.ExecuteAsync<int>(() => throw new IOException()));
        await Assert.ThrowsAsync<IOException>(() => breaker.ExecuteAsync<int>(() => throw new IOException()));
        Assert.Equal(CircuitState.Open, breaker.State);
        await Assert.ThrowsAsync<CircuitOpenException>(() => breaker.ExecuteAsync(() => Task.FromResult(1)));
    }

    [Fact]
    public async Task CircuitBreaker_AfterBreak_AllowsHalfOpenProbeAndCloses()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T00:00:00Z"));
        var breaker = new ConnectorCircuitBreaker(clock, 1, TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<IOException>(() => breaker.ExecuteAsync<int>(() => throw new IOException()));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.Equal(7, await breaker.ExecuteAsync(() => Task.FromResult(7)));
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task IdempotentExecutor_DuplicateKey_ExecutesTargetOnce()
    {
        var store = new MemoryIdempotencyStore();
        var calls = 0;
        var executor = new IdempotentExecutor(store, new FakeClock(DateTimeOffset.UtcNow));
        async Task<IdempotencyResult> Operation()
        {
            calls++;
            await Task.Yield();
            return new IdempotencyResult(201, """{"id":"1"}""", default);
        }
        var first = await executor.ExecuteAsync("load", "key-1", Operation);
        var second = await executor.ExecuteAsync("load", "key-1", Operation);
        Assert.Equal(1, calls);
        Assert.Equal(first.Payload, second.Payload);
    }

    [Fact]
    public async Task IdempotentExecutor_ConcurrentDuplicateKey_ExecutesTargetOnce()
    {
        var store = new MemoryIdempotencyStore();
        var calls = 0;
        var executor = new IdempotentExecutor(store, new FakeClock(DateTimeOffset.UtcNow));
        async Task<IdempotencyResult> Operation()
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(20);
            return new IdempotencyResult(201, "created", default);
        }
        var results = await Task.WhenAll(
            executor.ExecuteAsync("load", "concurrent-key", Operation),
            executor.ExecuteAsync("load", "concurrent-key", Operation));
        Assert.Equal(1, calls);
        Assert.All(results, x => Assert.Equal("created", x.Payload));
    }

    [Fact]
    public async Task CheckpointedBatch_AfterMidBatchFailure_ResumesAtFailedRecord()
    {
        var checkpoints = new MemoryCheckpointStore();
        var deadLetters = new MemoryDeadLetterStore();
        var processor = new CheckpointedBatchProcessor(
            checkpoints, deadLetters, new SequentialIdGenerator(), new FakeClock(DateTimeOffset.UtcNow));
        var seen = new List<int>();
        await Assert.ThrowsAsync<IOException>(() => processor.ProcessAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "load", "batch", new[] { 0, 1, 2 },
            (record, _, _) =>
            {
                if (record == 1 && seen.Count == 1)
                {
                    throw new IOException("process stopped");
                }
                seen.Add(record);
                return Task.CompletedTask;
            },
            (record, _) => record.ToString(),
            record => record.ToString(),
            _ => false));
        await processor.ProcessAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "load", "batch", new[] { 0, 1, 2 },
            (record, _, _) =>
            {
                seen.Add(record);
                return Task.CompletedTask;
            },
            (record, _) => record.ToString(),
            record => record.ToString(),
            _ => false);
        Assert.Equal([0, 1, 2], seen);
    }

    [Fact]
    public async Task CheckpointedBatch_PoisonRecord_IsQuarantinedAndRunContinues()
    {
        var checkpoints = new MemoryCheckpointStore();
        var deadLetters = new MemoryDeadLetterStore();
        var processor = new CheckpointedBatchProcessor(
            checkpoints, deadLetters, new SequentialIdGenerator(), new FakeClock(DateTimeOffset.UtcNow));
        var result = await processor.ProcessAsync(
            Guid.NewGuid(), "load", "batch", new[] { "good", "poison", "good-2" },
            (record, _, _) => record == "poison"
                ? throw new PoisonException()
                : Task.CompletedTask,
            (record, _) => record,
            record => record,
            ex => ex is PoisonException);
        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Quarantined);
        Assert.Single(deadLetters.Items);
    }

    [Theory]
    [InlineData("*/5 * * * *", "2026-09-03T10:05:00+00:00")]
    [InlineData("30 8 * * *", "2026-09-04T08:30:00+00:00")]
    [InlineData("0 0 1 * *", "2026-10-01T00:00:00+00:00")]
    public void Cron_NextOccurrence_IsCalculated(string cron, string expected)
    {
        var start = DateTimeOffset.Parse("2026-09-03T10:01:00Z");
        Assert.Equal(DateTimeOffset.Parse(expected), CronExpression.Parse(cron).GetNextOccurrence(start));
    }

    [Fact]
    public void Scheduler_PreventsOverlap()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T10:05:00Z"));
        var decision = new FlowScheduler(clock).Evaluate(
            CronExpression.Parse("*/5 * * * *"),
            new FlowScheduleState { LastScheduledAt = clock.UtcNow.AddMinutes(-5), IsRunning = true },
            "latest",
            TimeSpan.FromMinutes(10));
        Assert.False(decision.ShouldRun);
        Assert.Equal("overlap-prevented", decision.Reason);
    }

    [Fact]
    public void Scheduler_SkipPolicy_SkipsOldMisfire()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T10:30:00Z"));
        var decision = new FlowScheduler(clock).Evaluate(
            CronExpression.Parse("0 * * * *"),
            new FlowScheduleState { LastScheduledAt = clock.UtcNow.AddHours(-2) },
            "skip",
            TimeSpan.FromMinutes(10));
        Assert.False(decision.ShouldRun);
        Assert.Equal("misfire-skipped", decision.Reason);
    }

    [Fact]
    public void Scheduler_LatestPolicy_CatchesUpMisfire()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-03T10:30:00Z"));
        var decision = new FlowScheduler(clock).Evaluate(
            CronExpression.Parse("0 * * * *"),
            new FlowScheduleState { LastScheduledAt = clock.UtcNow.AddHours(-2) },
            "latest",
            TimeSpan.FromMinutes(10));
        Assert.True(decision.ShouldRun);
        Assert.Equal("catch-up", decision.Reason);
    }

    private sealed class PoisonException : Exception;
}
