namespace ExampleBank.Ledger.Application.Abstractions;

/// <summary>
/// Provides the concurrency-control primitives for the ledger.
///
/// <para><see cref="AcquireAccountsAsync"/> takes per-account locks in a deterministic
/// (sorted) order so that two transfers touching the same pair of accounts in opposite
/// directions can never deadlock.</para>
///
/// <para><see cref="AcquireChainAsync"/> serialises appends to the tamper-evidence hash chain,
/// which requires a total order. It is always acquired <em>after</em> account locks, giving a
/// single global lock ordering and therefore deadlock freedom.</para>
/// </summary>
public interface IAccountLockManager
{
    Task<IAsyncDisposable> AcquireAccountsAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken);

    Task<IAsyncDisposable> AcquireChainAsync(CancellationToken cancellationToken);
}
