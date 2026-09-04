using Northstar.Iga.Domain;

namespace Northstar.Iga.Application;

public sealed record ConnectorUser(
    Guid UserId,
    string UserName,
    string DisplayName,
    bool Enabled,
    IReadOnlyCollection<string> Permissions);

public sealed record ConnectorAccountSnapshot(
    string ExternalId,
    Guid? UserId,
    string UserName,
    bool Enabled,
    IReadOnlyCollection<string> Permissions);

public interface IProvisioningConnector
{
    string Key { get; }
    Task<ConnectorAccountSnapshot> CreateAsync(ConnectorUser user, CancellationToken cancellationToken);
    Task<ConnectorAccountSnapshot> UpdateAsync(ConnectorUser user, CancellationToken cancellationToken);
    Task DisableAsync(Guid userId, CancellationToken cancellationToken);
    Task DeleteAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectorAccountSnapshot>> GetAccountsAsync(CancellationToken cancellationToken);
}

public sealed record HrIdentityRecord(
    string EmployeeNumber,
    string DisplayName,
    string Email,
    string Department,
    string JobTitle,
    string Location,
    string CostCentre,
    string EmploymentType,
    int Clearance,
    DateOnly StartDate);

public interface IHrIdentitySource
{
    Task<IReadOnlyList<HrIdentityRecord>> ReadAsync(CancellationToken cancellationToken);
}

public interface ISimulatedConnectorControl
{
    void FailNext(string connectorKey, ProvisioningOperation operation, int count);
    void SeedAccount(string connectorKey, ConnectorAccountSnapshot account);
    IReadOnlyList<ConnectorAccountSnapshot> Snapshot(string connectorKey);
    void Reset();
}
