using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lab.Diagnostics.Measurement;

public sealed class EfCommandCounterInterceptor : DbCommandInterceptor
{
    private const int MaximumSamples = 12;
    private readonly object _gate = new();
    private readonly List<string> _samples = [];
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public IReadOnlyList<string> Samples
    {
        get
        {
            lock (_gate)
            {
                return [.. _samples];
            }
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        lock (_gate)
        {
            _samples.Clear();
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command)
    {
        Interlocked.Increment(ref _count);
        lock (_gate)
        {
            if (_samples.Count < MaximumSamples)
            {
                _samples.Add(command.CommandText.ReplaceLineEndings(" ").Trim());
            }
        }
    }
}
