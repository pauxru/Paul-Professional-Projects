using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Domain.Journal;
using Microsoft.Extensions.Logging;

namespace ExampleBank.Ledger.Application.Common;

/// <summary>
/// Describes a ledger command to be executed atomically: which accounts to lock, an idempotency
/// scope/key, and a <see cref="Build"/> delegate that loads the (now-locked) accounts, validates
/// the operation, applies the cached-balance effects and returns an <em>unsealed</em> entry.
/// </summary>
public sealed record EntryCommand(
    string Scope,
    string? IdempotencyKey,
    IReadOnlyCollection<Guid> AccountsToLock,
    Func<ILedgerUnitOfWork, CancellationToken, Task<JournalEntry>> Build);

/// <summary>
/// Runs ledger commands with the full concurrency protocol:
/// <list type="number">
/// <item>replay the stored response if the idempotency key was already used;</item>
/// <item>take per-account locks in deterministic order (deadlock-free);</item>
/// <item>build and validate inside a serialized transaction;</item>
/// <item>for entries, seal into the hash chain under the global chain lock;</item>
/// <item>commit, retrying transient/concurrency failures and replaying on duplicate keys.</item>
/// </list>
/// </summary>
public sealed class LedgerCommandExecutor
{
    private const int MaxAttempts = 5;

    private readonly ILedgerUnitOfWorkFactory _uowFactory;
    private readonly IAccountLockManager _locks;
    private readonly IClock _clock;
    private readonly ILedgerMetrics _metrics;
    private readonly ILogger<LedgerCommandExecutor> _logger;

    public LedgerCommandExecutor(
        ILedgerUnitOfWorkFactory uowFactory,
        IAccountLockManager locks,
        IClock clock,
        ILedgerMetrics metrics,
        ILogger<LedgerCommandExecutor> logger)
    {
        _uowFactory = uowFactory;
        _locks = locks;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Executes an append-to-ledger command that seals a new entry into the hash chain.</summary>
    public async Task<EntryResult> ExecuteAsync(EntryCommand command, CancellationToken cancellationToken)
    {
        if (command.IdempotencyKey is { } preKey)
        {
            var replay = await TryReplayAsync<EntryResult>(preKey, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }
        }

        var startedAt = _clock.UtcNow;
        await using var accountLocks = await _locks.AcquireAccountsAsync(command.AccountsToLock, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            await using var uow = await _uowFactory.CreateAsync(cancellationToken);
            try
            {
                await uow.BeginTransactionAsync(cancellationToken);
                var entry = await command.Build(uow, cancellationToken);

                await using (await _locks.AcquireChainAsync(cancellationToken))
                {
                    var head = await uow.Journal.GetChainHeadAsync(cancellationToken);
                    entry.Seal(head.Sequence + 1, head.Hash);
                    await uow.Journal.AddAsync(entry, cancellationToken);

                    var result = EntryResult.From(entry);
                    if (command.IdempotencyKey is { } key)
                    {
                        await uow.Idempotency.AddAsync(
                            IdempotencyRecord.Create(key, command.Scope, string.Empty, entry.Id, LedgerJson.Serialize(result), _clock.UtcNow),
                            cancellationToken);
                    }

                    await uow.SaveChangesAsync(cancellationToken);
                    await uow.CommitAsync(cancellationToken);

                    _metrics.EntryPosted(entry.Type.ToString());
                    _metrics.RecordPostingLatency((_clock.UtcNow - startedAt).TotalMilliseconds, entry.Type.ToString());
                    return result;
                }
            }
            catch (DuplicateKeyException)
            {
                await uow.RollbackAsync(cancellationToken);
                if (command.IdempotencyKey is { } key
                    && await TryReplayAsync<EntryResult>(key, cancellationToken) is { } replay)
                {
                    return replay;
                }

                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                await uow.RollbackAsync(cancellationToken);
                _logger.LogWarning(ex, "Ledger command {Scope} attempt {Attempt} failed transiently; retrying.", command.Scope, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Executes a state-changing command that does not append a hash-chained entry (e.g. placing or
    /// releasing a hold), with the same locking, idempotency and retry semantics.
    /// </summary>
    public async Task<T> ExecuteCommandAsync<T>(
        string scope,
        string? idempotencyKey,
        IReadOnlyCollection<Guid> accountsToLock,
        Func<ILedgerUnitOfWork, CancellationToken, Task<(T Result, Guid ResultId)>> build,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is { } preKey)
        {
            var replay = await TryReplayAsync<T>(preKey, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }
        }

        await using var accountLocks = await _locks.AcquireAccountsAsync(accountsToLock, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            await using var uow = await _uowFactory.CreateAsync(cancellationToken);
            try
            {
                await uow.BeginTransactionAsync(cancellationToken);
                var (result, resultId) = await build(uow, cancellationToken);

                if (idempotencyKey is { } key)
                {
                    await uow.Idempotency.AddAsync(
                        IdempotencyRecord.Create(key, scope, string.Empty, resultId, LedgerJson.Serialize(result), _clock.UtcNow),
                        cancellationToken);
                }

                await uow.SaveChangesAsync(cancellationToken);
                await uow.CommitAsync(cancellationToken);
                return result;
            }
            catch (DuplicateKeyException)
            {
                await uow.RollbackAsync(cancellationToken);
                if (idempotencyKey is { } key && await TryReplayAsync<T>(key, cancellationToken) is { } replay)
                {
                    return replay;
                }

                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                await uow.RollbackAsync(cancellationToken);
                _logger.LogWarning(ex, "Ledger command {Scope} attempt {Attempt} failed transiently; retrying.", scope, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex is TransientStorageException or ConcurrencyConflictException;

    private async Task<T?> TryReplayAsync<T>(string key, CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var existing = await uow.Idempotency.FindAsync(key, cancellationToken);
        return existing is null ? default : LedgerJson.Deserialize<T>(existing.ResponseJson);
    }
}
