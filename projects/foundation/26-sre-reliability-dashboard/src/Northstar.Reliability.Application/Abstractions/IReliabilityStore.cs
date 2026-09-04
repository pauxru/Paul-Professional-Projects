using Northstar.Reliability.Domain.Incidents;
using Northstar.Reliability.Domain.Postmortems;
using Northstar.Reliability.Domain.Services;
using Northstar.Reliability.Domain.Slo;
using Northstar.Reliability.Domain.Telemetry;

namespace Northstar.Reliability.Application.Abstractions;

public interface IReliabilityStore
{
    Task<IReadOnlyList<ServiceDefinition>> GetServicesAsync(CancellationToken cancellationToken = default);
    Task<ServiceDefinition?> GetServiceAsync(string slug, CancellationToken cancellationToken = default);
    Task AddServiceAsync(ServiceDefinition service, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SliDefinition>> GetSlisAsync(CancellationToken cancellationToken = default);
    Task<SliDefinition?> GetSliAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddSliAsync(SliDefinition sli, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SloDefinition>> GetSlosAsync(CancellationToken cancellationToken = default);
    Task<SloDefinition?> GetSloAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddSloAsync(SloDefinition slo, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MetricSample>> GetMetricsAsync(string? serviceSlug = null, CancellationToken cancellationToken = default);
    Task AddMetricsAsync(IEnumerable<MetricSample> metrics, CancellationToken cancellationToken = default);
    Task ReplaceMetricsAsync(IEnumerable<MetricSample> metrics, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlertInstance>> GetAlertsAsync(CancellationToken cancellationToken = default);
    Task<AlertInstance?> GetAlertAsync(Guid sloId, string ruleName, CancellationToken cancellationToken = default);
    Task SaveAlertAsync(AlertInstance alert, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MaintenanceWindow>> GetMaintenanceWindowsAsync(CancellationToken cancellationToken = default);
    Task AddMaintenanceWindowAsync(MaintenanceWindow maintenanceWindow, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Incident>> GetIncidentsAsync(CancellationToken cancellationToken = default);
    Task<Incident?> GetIncidentAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddIncidentAsync(Incident incident, CancellationToken cancellationToken = default);
    Task SaveIncidentAsync(Incident incident, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Postmortem>> GetPostmortemsAsync(CancellationToken cancellationToken = default);
    Task<Postmortem?> GetPostmortemAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddPostmortemAsync(Postmortem postmortem, CancellationToken cancellationToken = default);
    Task SavePostmortemAsync(Postmortem postmortem, CancellationToken cancellationToken = default);
}
