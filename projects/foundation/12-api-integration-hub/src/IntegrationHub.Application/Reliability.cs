using System.Collections.Concurrent;

namespace IntegrationHub.Application;

public sealed record RetryOutcome<T>(
    bool IsSuccess,
    T? Value = default,
    bool IsTransient = false,
    TimeSpan? RetryAfter = null,
    Exception? Error = null);

public sealed class RetryExecutor(
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<double>? jitter = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    public async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<RetryOutcome<T>>> action,
        int maxAttempts,
        TimeSpan baseDelay,
        Action<int, TimeSpan>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var outcome = await action(attempt, cancellationToken);
            if (outcome.IsSuccess)
            {
                return outcome.Value!;
            }

            lastError = outcome.Error;
            if (!outcome.IsTransient || attempt == maxAttempts)
            {
                break;
            }

            var exponential = TimeSpan.FromMilliseconds(
                baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            var selected = outcome.RetryAfter ?? exponential;
            var withJitter = outcome.RetryAfter is null
                ? selected + TimeSpan.FromMilliseconds(selected.TotalMilliseconds * 0.2 * _jitter())
                : selected;
            onRetry?.Invoke(attempt, withJitter);
            await _delay(withJitter, cancellationToken);
        }

        throw lastError ?? new InvalidOperationException("The operation failed after all retry attempts.");
    }
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

public sealed class ConnectorCircuitBreaker(
    IClock clock,
    int failureThreshold,
    TimeSpan breakDuration,
    Func<Exception, bool>? shouldCountFailure = null)
{
    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;
    private bool _halfOpenProbeInFlight;

    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                RefreshState();
                if (_openedAt is null)
                {
                    return CircuitState.Closed;
                }
                return clock.UtcNow - _openedAt < breakDuration ? CircuitState.Open : CircuitState.HalfOpen;
            }
        }
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        lock (_gate)
        {
            RefreshState();
            if (_openedAt is not null && clock.UtcNow - _openedAt < breakDuration)
            {
                throw new CircuitOpenException(_openedAt.Value.Add(breakDuration));
            }

            if (_openedAt is not null)
            {
                if (_halfOpenProbeInFlight)
                {
                    throw new CircuitOpenException(clock.UtcNow.Add(breakDuration));
                }
                _halfOpenProbeInFlight = true;
            }
        }

        try
        {
            var result = await action();
            lock (_gate)
            {
                _consecutiveFailures = 0;
                _openedAt = null;
                _halfOpenProbeInFlight = false;
            }
            return result;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _halfOpenProbeInFlight = false;
                if (shouldCountFailure?.Invoke(ex) ?? true)
                {
                    _consecutiveFailures++;
                    if (_openedAt is not null || _consecutiveFailures >= failureThreshold)
                    {
                        _openedAt = clock.UtcNow;
                    }
                }
                else if (_openedAt is not null)
                {
                    _consecutiveFailures = 0;
                    _openedAt = null;
                }
            }
            throw;
        }
    }

    private void RefreshState()
    {
        if (_openedAt is not null && clock.UtcNow - _openedAt >= breakDuration)
        {
            _halfOpenProbeInFlight = false;
        }
    }
}

public sealed class CircuitOpenException(DateTimeOffset retryAt)
    : Exception($"Connector circuit is open until {retryAt:O}")
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}

public sealed class ConnectorBulkhead(int maxConcurrency)
{
    private readonly SemaphoreSlim _semaphore = new(maxConcurrency, maxConcurrency);

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, TimeSpan queueTimeout, CancellationToken cancellationToken)
    {
        if (!await _semaphore.WaitAsync(queueTimeout, cancellationToken))
        {
            throw new BulkheadRejectedException();
        }

        try
        {
            return await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

public sealed class BulkheadRejectedException : Exception
{
    public BulkheadRejectedException() : base("Connector bulkhead queue is full.")
    {
    }
}

public sealed class IdempotentExecutor(IIdempotencyStore store, IClock clock)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public async Task<IdempotencyResult> ExecuteAsync(
        string scope,
        string key,
        Func<Task<IdempotencyResult>> operation,
        CancellationToken cancellationToken = default)
    {
        var lockKey = $"{scope}\n{key}";
        var gate = Locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await store.GetAsync(scope, key, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            var result = await operation();
            if (await store.TryStoreAsync(scope, key, result with { CreatedAt = clock.UtcNow }, cancellationToken))
            {
                return result;
            }

            return await store.GetAsync(scope, key, cancellationToken)
                   ?? throw new InvalidOperationException("Idempotency result was concurrently lost.");
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
            {
                Locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(lockKey, gate));
            }
        }
    }
}
