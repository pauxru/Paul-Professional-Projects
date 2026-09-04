using System.Collections.Concurrent;
using ExampleBank.Ledger.Application.Abstractions;

namespace ExampleBank.Ledger.Infrastructure.Concurrency;

/// <summary>
/// In-process implementation of the ledger's lock ordering. Per-account locks are always taken in
/// ascending account-id order, and the global chain lock is always taken last, giving a single
/// total lock order and therefore deadlock freedom. Registered as a singleton so every command
/// shares the same lock table.
/// </summary>
public sealed class AccountLockManager : IAccountLockManager
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _accountLocks = new();
    private readonly SemaphoreSlim _chainLock = new(1, 1);

    public async Task<IAsyncDisposable> AcquireAccountsAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        var ordered = accountIds.Distinct().OrderBy(id => id).ToList();
        var acquired = new List<SemaphoreSlim>(ordered.Count);
        try
        {
            foreach (var id in ordered)
            {
                var semaphore = _accountLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(cancellationToken);
                acquired.Add(semaphore);
            }
        }
        catch
        {
            Release(acquired);
            throw;
        }

        return new Releaser(acquired);
    }

    public async Task<IAsyncDisposable> AcquireChainAsync(CancellationToken cancellationToken)
    {
        await _chainLock.WaitAsync(cancellationToken);
        return new Releaser(new List<SemaphoreSlim> { _chainLock });
    }

    private static void Release(List<SemaphoreSlim> semaphores)
    {
        for (int i = semaphores.Count - 1; i >= 0; i--)
        {
            semaphores[i].Release();
        }
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly List<SemaphoreSlim> _semaphores;
        private bool _disposed;

        public Releaser(List<SemaphoreSlim> semaphores) => _semaphores = semaphores;

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                Release(_semaphores);
            }

            return ValueTask.CompletedTask;
        }
    }
}
