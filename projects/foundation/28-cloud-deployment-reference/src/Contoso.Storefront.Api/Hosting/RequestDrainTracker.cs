namespace Contoso.Storefront.Api.Hosting;

public sealed class RequestDrainTracker
{
    private readonly object _gate = new();
    private TaskCompletionSource _empty =
        CompletedSource();
    private int _inFlight;

    public bool IsDraining { get; private set; }
    public int InFlight => Volatile.Read(ref _inFlight);

    public IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (IsDraining)
            {
                return null;
            }

            if (_inFlight == 0)
            {
                _empty = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _inFlight++;
            return new Lease(this);
        }
    }

    public void BeginDraining()
    {
        lock (_gate)
        {
            IsDraining = true;
            if (_inFlight == 0)
            {
                _empty.TrySetResult();
            }
        }
    }

    public async Task<bool> WaitForZeroAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task empty;
        lock (_gate)
        {
            empty = _empty.Task;
        }

        var completed = await Task.WhenAny(
            empty,
            Task.Delay(timeout, cancellationToken));
        return completed == empty;
    }

    private void Exit()
    {
        lock (_gate)
        {
            _inFlight--;
            if (_inFlight == 0)
            {
                _empty.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class Lease(RequestDrainTracker owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Exit();
            }
        }
    }
}
