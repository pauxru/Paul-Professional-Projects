using System.Collections.Concurrent;
using AuditPlatform.Application.Events;

namespace AuditPlatform.Infrastructure.Events;

public sealed class InMemoryTenantLock : ITenantLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IAsyncDisposable> AcquireAsync(string tenantId, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Handle(sem);
    }

    private sealed class Handle : IAsyncDisposable
    {
        private SemaphoreSlim? _sem;
        public Handle(SemaphoreSlim sem) => _sem = sem;
        public ValueTask DisposeAsync()
        {
            _sem?.Release();
            _sem = null;
            return ValueTask.CompletedTask;
        }
    }
}
