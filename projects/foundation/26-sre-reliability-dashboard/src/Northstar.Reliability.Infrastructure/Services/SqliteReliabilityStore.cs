using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Northstar.Reliability.Application.Abstractions;
using Northstar.Reliability.Domain.Incidents;
using Northstar.Reliability.Domain.Postmortems;
using Northstar.Reliability.Domain.Services;
using Northstar.Reliability.Domain.Slo;
using Northstar.Reliability.Domain.Telemetry;
using Northstar.Reliability.Infrastructure.Persistence;

namespace Northstar.Reliability.Infrastructure.Services;

public sealed class SqliteReliabilityStore(ReliabilityDbContext context) : IReliabilityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ServiceDefinition>> GetServicesAsync(CancellationToken cancellationToken = default) =>
        (await context.Services.AsNoTracking().ToListAsync(cancellationToken))
        .Select(ToDomain)
        .OrderBy(service => service.Slug)
        .ToArray();

    public async Task<ServiceDefinition?> GetServiceAsync(string slug, CancellationToken cancellationToken = default)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        var entity = await context.Services.AsNoTracking()
            .SingleOrDefaultAsync(service => service.Slug == normalized, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task AddServiceAsync(ServiceDefinition service, CancellationToken cancellationToken = default)
    {
        context.Services.Add(ToEntity(service));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SliDefinition>> GetSlisAsync(CancellationToken cancellationToken = default) =>
        (await context.Slis.AsNoTracking().ToListAsync(cancellationToken))
        .Select(ToDomain)
        .OrderBy(sli => sli.Name)
        .ToArray();

    public async Task<SliDefinition?> GetSliAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await context.Slis.AsNoTracking().SingleOrDefaultAsync(sli => sli.Id == id, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task AddSliAsync(SliDefinition sli, CancellationToken cancellationToken = default)
    {
        context.Slis.Add(ToEntity(sli));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SloDefinition>> GetSlosAsync(CancellationToken cancellationToken = default) =>
        (await context.Slos.AsNoTracking().ToListAsync(cancellationToken))
        .Select(ToDomain)
        .OrderBy(slo => slo.Name)
        .ToArray();

    public async Task<SloDefinition?> GetSloAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await context.Slos.AsNoTracking().SingleOrDefaultAsync(slo => slo.Id == id, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task AddSloAsync(SloDefinition slo, CancellationToken cancellationToken = default)
    {
        context.Slos.Add(ToEntity(slo));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MetricSample>> GetMetricsAsync(string? serviceSlug = null, CancellationToken cancellationToken = default)
    {
        var query = context.Metrics.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(serviceSlug))
        {
            var normalized = serviceSlug.Trim().ToLowerInvariant();
            query = query.Where(metric => metric.ServiceSlug == normalized);
        }

        return (await query.ToListAsync(cancellationToken))
            .Select(ToDomain)
            .OrderBy(metric => metric.Timestamp)
            .ToArray();
    }

    public async Task AddMetricsAsync(IEnumerable<MetricSample> metrics, CancellationToken cancellationToken = default)
    {
        context.Metrics.AddRange(metrics.Select(ToEntity));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ReplaceMetricsAsync(IEnumerable<MetricSample> metrics, CancellationToken cancellationToken = default)
    {
        context.Metrics.RemoveRange(await context.Metrics.ToListAsync(cancellationToken));
        context.Metrics.AddRange(metrics.Select(ToEntity));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AlertInstance>> GetAlertsAsync(CancellationToken cancellationToken = default) =>
        (await context.Alerts.AsNoTracking().ToListAsync(cancellationToken))
        .Select(entity => Deserialize<AlertInstance>(entity.PayloadJson))
        .OrderByDescending(alert => alert.UpdatedAt)
        .ToArray();

    public async Task<AlertInstance?> GetAlertAsync(Guid sloId, string ruleName, CancellationToken cancellationToken = default)
    {
        var entity = await context.Alerts.AsNoTracking()
            .SingleOrDefaultAsync(alert => alert.SloId == sloId && alert.RuleName == ruleName, cancellationToken);
        return entity is null ? null : Deserialize<AlertInstance>(entity.PayloadJson);
    }

    public async Task SaveAlertAsync(AlertInstance alert, CancellationToken cancellationToken = default)
    {
        var entity = await context.Alerts.SingleOrDefaultAsync(item => item.Id == alert.Id, cancellationToken);
        if (entity is null)
        {
            context.Alerts.Add(new AlertEntity
            {
                Id = alert.Id,
                SloId = alert.SloId,
                RuleName = alert.RuleName,
                PayloadJson = Serialize(alert),
                UpdatedAt = alert.UpdatedAt
            });
        }
        else
        {
            entity.SloId = alert.SloId;
            entity.RuleName = alert.RuleName;
            entity.PayloadJson = Serialize(alert);
            entity.UpdatedAt = alert.UpdatedAt;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MaintenanceWindow>> GetMaintenanceWindowsAsync(CancellationToken cancellationToken = default) =>
        (await context.MaintenanceWindows.AsNoTracking().ToListAsync(cancellationToken))
        .Select(entity => Deserialize<MaintenanceWindow>(entity.PayloadJson))
        .OrderBy(window => window.StartsAt)
        .ToArray();

    public async Task AddMaintenanceWindowAsync(MaintenanceWindow maintenanceWindow, CancellationToken cancellationToken = default)
    {
        context.MaintenanceWindows.Add(new MaintenanceWindowEntity
        {
            Id = maintenanceWindow.Id,
            ServiceSlug = maintenanceWindow.ServiceSlug,
            StartsAt = maintenanceWindow.StartsAt,
            EndsAt = maintenanceWindow.EndsAt,
            PayloadJson = Serialize(maintenanceWindow)
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Incident>> GetIncidentsAsync(CancellationToken cancellationToken = default) =>
        (await context.Incidents.AsNoTracking().ToListAsync(cancellationToken))
        .Select(entity => Deserialize<Incident>(entity.PayloadJson))
        .OrderByDescending(incident => incident.StartedAt)
        .ToArray();

    public async Task<Incident?> GetIncidentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await context.Incidents.AsNoTracking().SingleOrDefaultAsync(incident => incident.Id == id, cancellationToken);
        return entity is null ? null : Deserialize<Incident>(entity.PayloadJson);
    }

    public async Task AddIncidentAsync(Incident incident, CancellationToken cancellationToken = default)
    {
        context.Incidents.Add(new IncidentEntity
        {
            Id = incident.Id,
            StartedAt = incident.StartedAt,
            PayloadJson = Serialize(incident)
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveIncidentAsync(Incident incident, CancellationToken cancellationToken = default)
    {
        var entity = await context.Incidents.SingleOrDefaultAsync(item => item.Id == incident.Id, cancellationToken)
            ?? throw new KeyNotFoundException("Incident was not found.");
        entity.StartedAt = incident.StartedAt;
        entity.PayloadJson = Serialize(incident);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Postmortem>> GetPostmortemsAsync(CancellationToken cancellationToken = default) =>
        (await context.Postmortems.AsNoTracking().ToListAsync(cancellationToken))
        .Select(entity => Deserialize<Postmortem>(entity.PayloadJson))
        .OrderByDescending(postmortem => postmortem.CreatedAt)
        .ToArray();

    public async Task<Postmortem?> GetPostmortemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await context.Postmortems.AsNoTracking().SingleOrDefaultAsync(postmortem => postmortem.Id == id, cancellationToken);
        return entity is null ? null : Deserialize<Postmortem>(entity.PayloadJson);
    }

    public async Task AddPostmortemAsync(Postmortem postmortem, CancellationToken cancellationToken = default)
    {
        context.Postmortems.Add(new PostmortemEntity
        {
            Id = postmortem.Id,
            IncidentId = postmortem.IncidentId,
            CreatedAt = postmortem.CreatedAt,
            PayloadJson = Serialize(postmortem)
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SavePostmortemAsync(Postmortem postmortem, CancellationToken cancellationToken = default)
    {
        var entity = await context.Postmortems.SingleOrDefaultAsync(item => item.Id == postmortem.Id, cancellationToken)
            ?? throw new KeyNotFoundException("Postmortem was not found.");
        entity.IncidentId = postmortem.IncidentId;
        entity.CreatedAt = postmortem.CreatedAt;
        entity.PayloadJson = Serialize(postmortem);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static ServiceEntity ToEntity(ServiceDefinition service) => new()
    {
        Id = service.Id,
        Slug = service.Slug,
        Name = service.Name,
        Tier = (int)service.Tier,
        OwningTeam = service.OwningTeam,
        OnCallRotation = service.OnCallRotation,
        RepositoryUrl = service.RepositoryUrl,
        RunbookUrl = service.RunbookUrl,
        DependenciesJson = Serialize(service.Dependencies),
        CreatedAt = service.CreatedAt,
        Version = 0
    };

    private static ServiceDefinition ToDomain(ServiceEntity entity) => new(
        entity.Id,
        entity.Slug,
        entity.Name,
        (CriticalityTier)entity.Tier,
        entity.OwningTeam,
        entity.OnCallRotation,
        entity.RepositoryUrl,
        entity.RunbookUrl,
        Deserialize<string[]>(entity.DependenciesJson),
        entity.CreatedAt);

    private static SliEntity ToEntity(SliDefinition sli) => new()
    {
        Id = sli.Id,
        Name = sli.Name,
        ServiceSlug = sli.ServiceSlug,
        AggregationMode = (int)sli.AggregationMode,
        Kind = (int)sli.Kind,
        FilterJson = Serialize(sli.Filter),
        LatencyThresholdMilliseconds = sli.LatencyThresholdMilliseconds,
        CreatedAt = sli.CreatedAt
    };

    private static SliDefinition ToDomain(SliEntity entity) => new(
        entity.Id,
        entity.Name,
        entity.ServiceSlug,
        (SliAggregationMode)entity.AggregationMode,
        (SliKind)entity.Kind,
        Deserialize<SliFilter>(entity.FilterJson),
        entity.LatencyThresholdMilliseconds,
        entity.CreatedAt);

    private static SloEntity ToEntity(SloDefinition slo) => new()
    {
        Id = slo.Id,
        Name = slo.Name,
        SliId = slo.SliId,
        ServiceSlug = slo.ServiceSlug,
        Target = slo.Target,
        WindowKind = (int)slo.WindowKind,
        RollingDays = slo.RollingDays,
        CalendarPeriod = (int)slo.CalendarPeriod,
        CreatedAt = slo.CreatedAt
    };

    private static SloDefinition ToDomain(SloEntity entity) => new(
        entity.Id,
        entity.Name,
        entity.SliId,
        entity.ServiceSlug,
        entity.Target,
        (SloWindowKind)entity.WindowKind,
        entity.RollingDays,
        (CalendarWindowPeriod)entity.CalendarPeriod,
        entity.CreatedAt);

    private static MetricEntity ToEntity(MetricSample sample) => new()
    {
        Id = sample.Id,
        ServiceSlug = sample.ServiceSlug,
        Timestamp = sample.Timestamp,
        Endpoint = sample.Endpoint,
        Region = sample.Region,
        Tier = sample.Tier,
        Requests = sample.Requests,
        Errors = sample.Errors,
        LatencyGoodRequests = sample.LatencyGoodRequests,
        QualityGoodEvents = sample.QualityGoodEvents,
        QualityValidEvents = sample.QualityValidEvents,
        FreshnessGoodEvents = sample.FreshnessGoodEvents,
        FreshnessValidEvents = sample.FreshnessValidEvents,
        ProbeGoodMinutes = sample.ProbeGoodMinutes,
        ProbeTotalMinutes = sample.ProbeTotalMinutes,
        P50LatencyMilliseconds = sample.P50LatencyMilliseconds,
        P95LatencyMilliseconds = sample.P95LatencyMilliseconds,
        LatencyHistogramJson = Serialize(sample.LatencyHistogram),
        Resolution = (int)sample.Resolution
    };

    private static MetricSample ToDomain(MetricEntity entity) => new(
        entity.Id,
        entity.ServiceSlug,
        entity.Timestamp,
        entity.Endpoint,
        entity.Region,
        entity.Tier,
        entity.Requests,
        entity.Errors,
        entity.LatencyGoodRequests,
        entity.QualityGoodEvents,
        entity.QualityValidEvents,
        entity.FreshnessGoodEvents,
        entity.FreshnessValidEvents,
        entity.ProbeGoodMinutes,
        entity.ProbeTotalMinutes,
        entity.P50LatencyMilliseconds,
        entity.P95LatencyMilliseconds,
        Deserialize<LatencyHistogramBucket[]>(entity.LatencyHistogramJson),
        (MetricResolution)entity.Resolution);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions) ??
        throw new InvalidOperationException($"Could not deserialize persisted {typeof(T).Name}.");
}
