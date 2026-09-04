using Lab.Diagnostics.Measurement;
using Microsoft.Data.Sqlite;

namespace Lab.Scenarios.Incidents;

public sealed class ConnectionPoolExhaustionScenario : IIncidentScenario
{
    public string Id => "INC-003";

    public string Name => "Connection pool exhaustion";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        const int poolCapacity = 3;
        var operations = options.BoundedRequests(6, 30);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        var session = new MeasurementSession();
        await using var pool = new BoundedSqliteConnectionPool(poolCapacity);
        var timeouts = 0;
        var completed = 0;

        if (options.Mode == ScenarioMode.Broken)
        {
            var leaked = new List<BoundedSqliteConnectionPool.Lease>();
            try
            {
                for (var index = 0; index < poolCapacity; index++)
                {
                    leaked.Add(await pool.AcquireAsync(TimeSpan.FromMilliseconds(80), token));
                    completed++;
                }

                var contenders = Enumerable.Range(poolCapacity, operations - poolCapacity)
                    .Select(async _ =>
                    {
                        try
                        {
                            await using var lease = await pool.AcquireAsync(TimeSpan.FromMilliseconds(80), token);
                            await lease.ExecuteProbeAsync(token);
                            Interlocked.Increment(ref completed);
                        }
                        catch (TimeoutException)
                        {
                            Interlocked.Increment(ref timeouts);
                        }
                    });
                await Task.WhenAll(contenders);
            }
            finally
            {
                foreach (var lease in leaked)
                {
                    await lease.DisposeAsync();
                }
            }
        }
        else
        {
            var work = Enumerable.Range(0, operations).Select(async _ =>
            {
                await using var lease = await pool.AcquireAsync(TimeSpan.FromMilliseconds(250), token);
                await lease.ExecuteProbeAsync(token);
                await Task.Delay(4, token);
                Interlocked.Increment(ref completed);
            });
            await Task.WhenAll(work);
        }

        var outcome = session.Complete();
        return new ScenarioReport
        {
            ScenarioId = Id,
            ScenarioName = Name,
            Mode = options.Mode,
            RequestedOperations = operations,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ElapsedMilliseconds = outcome.Elapsed.TotalMilliseconds,
            Metrics = new Dictionary<string, object?>
            {
                ["poolCapacity"] = poolCapacity,
                ["completedOperations"] = completed,
                ["acquireTimeouts"] = timeouts,
                ["leasedAfterCleanup"] = pool.LeasedCount,
                ["hardAcquireTimeoutMilliseconds"] = options.Mode == ScenarioMode.Broken ? 80 : 250
            },
            Evidence =
            [
                "Each lease owns a real Microsoft.Data.Sqlite connection and a semaphore-backed capacity token.",
                "Broken mode retains the first three leases until the bounded contender phase has timed out; cleanup always disposes them."
            ],
            Limitations =
            [
                "Microsoft.Data.Sqlite does not expose server-pool exhaustion like a network database. The lab uses an explicit bounded lease pool so failure and cleanup are deterministic."
            ]
        };
    }

    private sealed class BoundedSqliteConnectionPool(int capacity) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _capacity = new(capacity, capacity);
        private int _leasedCount;

        public int LeasedCount => Volatile.Read(ref _leasedCount);

        public async Task<Lease> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var acquireBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acquireBudget.CancelAfter(timeout);
            try
            {
                await _capacity.WaitAsync(acquireBudget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"A connection lease was not available within {timeout.TotalMilliseconds:F0} ms.");
            }

            try
            {
                var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync(cancellationToken);
                Interlocked.Increment(ref _leasedCount);
                return new Lease(this, connection);
            }
            catch
            {
                _capacity.Release();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            _capacity.Dispose();
            return ValueTask.CompletedTask;
        }

        public sealed class Lease(BoundedSqliteConnectionPool owner, SqliteConnection connection) : IAsyncDisposable
        {
            private SqliteConnection? _connection = connection;

            public async Task ExecuteProbeAsync(CancellationToken cancellationToken)
            {
                if (_connection is null)
                {
                    throw new ObjectDisposedException(nameof(Lease));
                }

                await using var command = _connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                await command.ExecuteScalarAsync(cancellationToken);
            }

            public async ValueTask DisposeAsync()
            {
                var connectionToDispose = Interlocked.Exchange(ref _connection, null);
                if (connectionToDispose is null)
                {
                    return;
                }

                await connectionToDispose.DisposeAsync();
                Interlocked.Decrement(ref owner._leasedCount);
                owner._capacity.Release();
            }
        }
    }
}
