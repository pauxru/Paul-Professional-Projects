using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class NullAuditContextAccessor : IAuditContextAccessor
{
    public string? SourceIp => null;
    public string? UserAgent => null;
}

public sealed class SimulatedConnectorState
{
    public object Gate { get; } = new();
    public Dictionary<string, Dictionary<string, ConnectorAccountSnapshot>> Accounts { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SimulatedConnectorControl(SimulatedConnectorState state) : ISimulatedConnectorControl
{
    public void FailNext(string connectorKey, ProvisioningOperation operation, int count)
    {
        lock (state.Gate)
        {
            state.Failures[FailureKey(connectorKey, operation)] = Math.Max(0, count);
        }
    }

    public void SeedAccount(string connectorKey, ConnectorAccountSnapshot account)
    {
        lock (state.Gate)
        {
            GetAccounts(connectorKey)[account.ExternalId] = account;
        }
    }

    public IReadOnlyList<ConnectorAccountSnapshot> Snapshot(string connectorKey)
    {
        lock (state.Gate)
        {
            return GetAccounts(connectorKey).Values.ToArray();
        }
    }

    public void Reset()
    {
        lock (state.Gate)
        {
            state.Accounts.Clear();
            state.Failures.Clear();
        }
    }

    internal void ThrowIfScheduled(string connectorKey, ProvisioningOperation operation)
    {
        lock (state.Gate)
        {
            var key = FailureKey(connectorKey, operation);
            if (!state.Failures.TryGetValue(key, out var remaining) || remaining <= 0)
            {
                return;
            }

            state.Failures[key] = remaining - 1;
            throw new ProvisioningTransientException(
                $"Simulated transient failure for connector '{connectorKey}' operation '{operation}'.");
        }
    }

    internal Dictionary<string, ConnectorAccountSnapshot> GetAccounts(string connectorKey)
    {
        if (!state.Accounts.TryGetValue(connectorKey, out var accounts))
        {
            accounts = new Dictionary<string, ConnectorAccountSnapshot>(StringComparer.OrdinalIgnoreCase);
            state.Accounts[connectorKey] = accounts;
        }

        return accounts;
    }

    private static string FailureKey(string connectorKey, ProvisioningOperation operation) =>
        $"{connectorKey}:{operation}";
}

public sealed class SimulatedProvisioningConnector(
    string key,
    SimulatedConnectorState state,
    SimulatedConnectorControl control) : IProvisioningConnector
{
    public string Key { get; } = key;

    public Task<ConnectorAccountSnapshot> CreateAsync(ConnectorUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        control.ThrowIfScheduled(Key, ProvisioningOperation.Create);
        lock (state.Gate)
        {
            var account = new ConnectorAccountSnapshot(
                $"{Key}-{user.UserId:N}",
                user.UserId,
                user.UserName,
                user.Enabled,
                user.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            control.GetAccounts(Key)[account.ExternalId] = account;
            return Task.FromResult(account);
        }
    }

    public Task<ConnectorAccountSnapshot> UpdateAsync(ConnectorUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        control.ThrowIfScheduled(Key, ProvisioningOperation.Update);
        lock (state.Gate)
        {
            var accounts = control.GetAccounts(Key);
            var existing = accounts.Values.FirstOrDefault(x => x.UserId == user.UserId);
            var account = new ConnectorAccountSnapshot(
                existing?.ExternalId ?? $"{Key}-{user.UserId:N}",
                user.UserId,
                user.UserName,
                user.Enabled,
                user.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            accounts[account.ExternalId] = account;
            return Task.FromResult(account);
        }
    }

    public Task DisableAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        control.ThrowIfScheduled(Key, ProvisioningOperation.Disable);
        lock (state.Gate)
        {
            var accounts = control.GetAccounts(Key);
            var existing = accounts.Values.FirstOrDefault(x => x.UserId == userId);
            if (existing is not null)
            {
                accounts[existing.ExternalId] = existing with { Enabled = false };
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        control.ThrowIfScheduled(Key, ProvisioningOperation.Delete);
        lock (state.Gate)
        {
            var accounts = control.GetAccounts(Key);
            var existing = accounts.Values.FirstOrDefault(x => x.UserId == userId);
            if (existing is not null)
            {
                accounts.Remove(existing.ExternalId);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConnectorAccountSnapshot>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(control.Snapshot(Key));
    }
}

public sealed class ProvisioningTransientException(string message) : Exception(message);

public sealed class SimulatedHrIdentitySource : IHrIdentitySource
{
    private readonly IReadOnlyList<HrIdentityRecord> _records =
    [
        new("HR-9001", "Synthetic HR Joiner 9001", "synthetic.hr9001@northstar.example",
            "Finance", "Finance Analyst", "Nairobi", "CC-FIN", "Employee", 2,
            new DateOnly(2026, 9, 1)),
        new("HR-9002", "Synthetic HR Joiner 9002", "synthetic.hr9002@northstar.example",
            "Technology", "Platform Engineer", "Nairobi", "CC-TECH", "Employee", 3,
            new DateOnly(2026, 9, 1))
    ];

    public Task<IReadOnlyList<HrIdentityRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_records);
    }
}
