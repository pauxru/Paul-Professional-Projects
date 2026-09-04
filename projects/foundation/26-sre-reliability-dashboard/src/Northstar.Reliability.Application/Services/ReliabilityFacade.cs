using Northstar.Reliability.Application.Abstractions;
using Northstar.Reliability.Application.Simulation;
using Northstar.Reliability.Domain.Common;
using Northstar.Reliability.Domain.Incidents;
using Northstar.Reliability.Domain.Postmortems;
using Northstar.Reliability.Domain.Reporting;
using Northstar.Reliability.Domain.Services;
using Northstar.Reliability.Domain.Slo;
using Northstar.Reliability.Domain.Telemetry;

namespace Northstar.Reliability.Application.Services;

public sealed record SloHistoryPoint(DateTimeOffset At, decimal Attainment, decimal RemainingBudgetPercent);

public sealed record BurnRateReport(
    Guid SloId,
    string ServiceSlug,
    string RuleName,
    AlertSeverity Severity,
    BurnRateReading LongWindow,
    BurnRateReading ShortWindow,
    decimal LongThreshold,
    decimal ShortThreshold,
    bool BothWindowsBreached);

public sealed record ServiceReliabilityReport(
    string ServiceSlug,
    string OwningTeam,
    IReadOnlyList<SloStatus> SloStatuses,
    IReadOnlyList<Incident> Incidents,
    ServiceMaturityScorecard Scorecard);

public sealed record TeamReliabilityReport(string Team, IReadOnlyList<ServiceReliabilityReport> Services);

public sealed record QuarterReliabilityReport(
    int Year,
    int Quarter,
    IReadOnlyList<Incident> Incidents,
    IReadOnlyList<IncidentTrendPoint> IncidentTrend,
    IReadOnlyList<(string ServiceSlug, decimal ConsumedPercent)> TopBudgetConsumers,
    AlertQualityReport AlertQuality);

public sealed record IncidentTrendPoint(DateOnly Day, int IncidentCount);

public sealed class ReliabilityFacade(
    IReliabilityStore store,
    IClock clock,
    SyntheticTelemetrySimulator simulator,
    ErrorBudgetPolicy budgetPolicy,
    IReadOnlyList<MultiWindowAlertRule> alertRules)
{
    public async Task<PageResult<ServiceDefinition>> GetServicesAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var services = await store.GetServicesAsync(cancellationToken);
        return PageResult<ServiceDefinition>.Create(services.OrderBy(service => service.Slug), page, pageSize);
    }

    public Task<IReadOnlyList<SliDefinition>> GetSlisAsync(CancellationToken cancellationToken = default) =>
        store.GetSlisAsync(cancellationToken);

    public Task<IReadOnlyList<SloDefinition>> GetSlosAsync(CancellationToken cancellationToken = default) =>
        store.GetSlosAsync(cancellationToken);

    public Task<IReadOnlyList<AlertInstance>> GetAlertsAsync(CancellationToken cancellationToken = default) =>
        store.GetAlertsAsync(cancellationToken);

    public Task<IReadOnlyList<Incident>> GetIncidentsAsync(CancellationToken cancellationToken = default) =>
        store.GetIncidentsAsync(cancellationToken);

    public Task<IReadOnlyList<Postmortem>> GetPostmortemsAsync(CancellationToken cancellationToken = default) =>
        store.GetPostmortemsAsync(cancellationToken);

    public Task<IReadOnlyList<MaintenanceWindow>> GetMaintenanceWindowsAsync(CancellationToken cancellationToken = default) =>
        store.GetMaintenanceWindowsAsync(cancellationToken);

    public async Task<ServiceDefinition> CreateServiceAsync(ServiceDefinition service, CancellationToken cancellationToken = default)
    {
        var services = await store.GetServicesAsync(cancellationToken);
        if (services.Any(existing => string.Equals(existing.Slug, service.Slug, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainRuleViolationException($"Service '{service.Slug}' already exists.");
        }

        ServiceDependencyGraph.EnsureAcyclic(services.Append(service));
        await store.AddServiceAsync(service, cancellationToken);
        return service;
    }

    public async Task<SliDefinition> CreateSliAsync(SliDefinition sli, CancellationToken cancellationToken = default)
    {
        await GetRequiredServiceAsync(sli.ServiceSlug, cancellationToken);
        await store.AddSliAsync(sli, cancellationToken);
        return sli;
    }

    public async Task<SloDefinition> CreateSloAsync(SloDefinition slo, CancellationToken cancellationToken = default)
    {
        var sli = await GetRequiredSliAsync(slo.SliId, cancellationToken);
        if (!string.Equals(sli.ServiceSlug, slo.ServiceSlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleViolationException("SLO and its SLI must belong to the same service.");
        }

        await store.AddSloAsync(slo, cancellationToken);
        return slo;
    }

    public async Task<IReadOnlyList<MetricSample>> IngestMetricsAsync(
        IEnumerable<MetricSample> metrics,
        CancellationToken cancellationToken = default)
    {
        using var activity = ReliabilityInstrumentation.ActivitySource.StartActivity("metrics.ingest");
        var materialized = metrics.ToArray();
        if (materialized.Length == 0)
        {
            throw new DomainRuleViolationException("At least one metric sample is required.");
        }

        var services = await store.GetServicesAsync(cancellationToken);
        var known = services.Select(service => service.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (materialized.Any(metric => !known.Contains(metric.ServiceSlug)))
        {
            throw new DomainRuleViolationException("Metrics can only be ingested for catalogued services.");
        }

        foreach (var metric in materialized)
        {
            metric.EnsureValid();
        }

        await store.AddMetricsAsync(materialized, cancellationToken);
        ReliabilityInstrumentation.MetricsIngested.Add(materialized.Length);
        return materialized;
    }

    public async Task<IReadOnlyList<MetricSample>> SimulateAsync(
        TrafficSimulationRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetServices = new[] { request.ServiceSlug }
            .Concat(request.Incident?.Kind == SyntheticIncidentKind.DependencyFailureCascade
                ? request.CascadeServiceSlugs ?? []
                : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var serviceSlug in targetServices)
        {
            await GetRequiredServiceAsync(serviceSlug, cancellationToken);
        }

        var generated = targetServices.SelectMany((serviceSlug, index) =>
        {
            var cascadingIncident = request.Incident?.Kind == SyntheticIncidentKind.DependencyFailureCascade && index > 0
                ? request.Incident with { Severity = request.Incident.Severity * 0.7m }
                : request.Incident;
            return simulator.Generate(request with
            {
                ServiceSlug = serviceSlug,
                Incident = cascadingIncident,
                BaseRequestsPerMinute = index == 0 ? request.BaseRequestsPerMinute : Math.Max(1, request.BaseRequestsPerMinute / 2),
                RandomSeed = request.RandomSeed + index
            });
        }).ToArray();
        await store.AddMetricsAsync(generated, cancellationToken);
        return generated;
    }

    public async Task<MetricRetentionResult> ApplyRetentionAsync(
        MetricRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var metrics = await store.GetMetricsAsync(cancellationToken: cancellationToken);
        var result = MetricRetentionEngine.Apply(metrics, policy, clock.UtcNow);
        await store.ReplaceMetricsAsync(result.RetainedRawSamples.Concat(result.HourlyRollups), cancellationToken);
        return result;
    }

    public async Task<SloStatus> GetSloStatusAsync(Guid sloId, CancellationToken cancellationToken = default)
    {
        var slo = await GetRequiredSloAsync(sloId, cancellationToken);
        var sli = await GetRequiredSliAsync(slo.SliId, cancellationToken);
        var metrics = await store.GetMetricsAsync(slo.ServiceSlug, cancellationToken);
        return ErrorBudgetCalculator.Evaluate(slo, sli, metrics, clock.UtcNow);
    }

    public async Task<IReadOnlyList<SloHistoryPoint>> GetSloHistoryAsync(
        Guid sloId,
        int days,
        CancellationToken cancellationToken = default)
    {
        var slo = await GetRequiredSloAsync(sloId, cancellationToken);
        var sli = await GetRequiredSliAsync(slo.SliId, cancellationToken);
        var metrics = await store.GetMetricsAsync(slo.ServiceSlug, cancellationToken);
        var count = Math.Clamp(days, 1, 90);
        var now = clock.UtcNow;
        var history = new List<SloHistoryPoint>();
        for (var offset = count - 1; offset >= 0; offset--)
        {
            var at = now.AddDays(-offset);
            var status = ErrorBudgetCalculator.Evaluate(slo, sli, metrics, at);
            history.Add(new SloHistoryPoint(at, status.Sli.Value, status.ErrorBudget.RemainingPercent));
        }

        return history;
    }

    public async Task<IReadOnlyList<BurnRateReport>> GetBurnRatesAsync(
        Guid? sloId = null,
        CancellationToken cancellationToken = default)
    {
        var slos = await store.GetSlosAsync(cancellationToken);
        var chosen = sloId.HasValue ? slos.Where(slo => slo.Id == sloId.Value).ToArray() : slos.ToArray();
        if (sloId.HasValue && chosen.Length == 0)
        {
            throw new KeyNotFoundException("SLO was not found.");
        }

        var slis = await store.GetSlisAsync(cancellationToken);
        var now = clock.UtcNow;
        var reports = new List<BurnRateReport>();
        foreach (var slo in chosen)
        {
            var sli = slis.Single(sliItem => sliItem.Id == slo.SliId);
            var metrics = await store.GetMetricsAsync(slo.ServiceSlug, cancellationToken);
            foreach (var rule in alertRules)
            {
                var longReading = BurnRateCalculator.Calculate(slo, sli, metrics, now, rule.LongWindow.Duration);
                var shortReading = BurnRateCalculator.Calculate(slo, sli, metrics, now, rule.ShortWindow.Duration);
                reports.Add(new BurnRateReport(
                    slo.Id,
                    slo.ServiceSlug,
                    rule.Name,
                    rule.Severity,
                    longReading,
                    shortReading,
                    rule.LongWindow.Threshold,
                    rule.ShortWindow.Threshold,
                    longReading.BurnRate >= rule.LongWindow.Threshold &&
                    shortReading.BurnRate >= rule.ShortWindow.Threshold));
            }
        }

        return reports;
    }

    public async Task<IReadOnlyList<AlertInstance>> EvaluateAlertsAsync(
        Guid? sloId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ReliabilityInstrumentation.ActivitySource.StartActivity("alerts.evaluate");
        var reports = await GetBurnRatesAsync(sloId, cancellationToken);
        var current = await store.GetAlertsAsync(cancellationToken);
        var maintenance = await store.GetMaintenanceWindowsAsync(cancellationToken);
        var incidents = await store.GetIncidentsAsync(cancellationToken);
        var now = clock.UtcNow;
        var result = new List<AlertInstance>();

        foreach (var report in reports)
        {
            var rule = alertRules.Single(item => item.Name == report.RuleName);
            var existing = current.SingleOrDefault(alert => alert.SloId == report.SloId && alert.RuleName == report.RuleName);
            var metrics = await store.GetMetricsAsync(report.ServiceSlug, cancellationToken);
            var dataObservedAt = metrics.Count == 0 ? now : metrics.Max(metric => metric.Timestamp);
            var isMaintenance = maintenance.Any(window => string.Equals(window.ServiceSlug, report.ServiceSlug, StringComparison.OrdinalIgnoreCase) && window.IsActiveAt(now));
            var hasOpenIncident = incidents.Any(incident => incident.IsOpen &&
                incident.AffectedServices.Contains(report.ServiceSlug, StringComparer.OrdinalIgnoreCase));
            var evaluated = MultiWindowAlertEvaluator.Evaluate(
                existing,
                new AlertEvaluationInput(
                    report.SloId,
                    report.ServiceSlug,
                    rule,
                    report.LongWindow.BurnRate,
                    report.ShortWindow.BurnRate,
                    now,
                    dataObservedAt,
                    isMaintenance,
                    hasOpenIncident));
            await store.SaveAlertAsync(evaluated, cancellationToken);
            ReliabilityInstrumentation.AlertsEvaluated.Add(1, new KeyValuePair<string, object?>("alert.rule", report.RuleName));
            result.Add(evaluated);
        }

        return result;
    }

    public async Task<AlertInstance> AcknowledgeAlertAsync(Guid alertId, CancellationToken cancellationToken = default)
    {
        var alert = (await store.GetAlertsAsync(cancellationToken)).SingleOrDefault(item => item.Id == alertId)
            ?? throw new KeyNotFoundException("Alert was not found.");
        var acknowledged = alert.Acknowledge(clock.UtcNow);
        await store.SaveAlertAsync(acknowledged, cancellationToken);
        return acknowledged;
    }

    public async Task<AlertInstance> SuppressAlertAsync(
        Guid alertId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainRuleViolationException("A suppression reason is required.");
        }

        var alert = (await store.GetAlertsAsync(cancellationToken)).SingleOrDefault(item => item.Id == alertId)
            ?? throw new KeyNotFoundException("Alert was not found.");
        var now = clock.UtcNow;
        var suppressed = alert with
        {
            State = AlertState.Suppressed,
            SuppressedAt = now,
            SuppressionReason = reason.Trim(),
            UpdatedAt = now
        };
        await store.SaveAlertAsync(suppressed, cancellationToken);
        return suppressed;
    }

    public async Task<MaintenanceWindow> AddMaintenanceWindowAsync(
        MaintenanceWindow maintenanceWindow,
        CancellationToken cancellationToken = default)
    {
        await GetRequiredServiceAsync(maintenanceWindow.ServiceSlug, cancellationToken);
        await store.AddMaintenanceWindowAsync(maintenanceWindow, cancellationToken);
        return maintenanceWindow;
    }

    public async Task<Incident> DeclareIncidentAsync(
        Incident incident,
        IEnumerable<Guid>? linkedAlertIds = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ReliabilityInstrumentation.ActivitySource.StartActivity("incident.declare");
        var services = await store.GetServicesAsync(cancellationToken);
        var known = services.Select(service => service.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (incident.AffectedServices.Any(service => !known.Contains(service)))
        {
            throw new DomainRuleViolationException("Incidents can only affect catalogued services.");
        }

        var current = incident;
        var alerts = await store.GetAlertsAsync(cancellationToken);
        foreach (var alertId in linkedAlertIds ?? [])
        {
            var alert = alerts.SingleOrDefault(item => item.Id == alertId)
                ?? throw new DomainRuleViolationException($"Linked alert '{alertId}' was not found.");
            current = current.LinkAlert(alertId);
            await store.SaveAlertAsync(alert.LinkIncident(current.Id, clock.UtcNow), cancellationToken);
        }

        await store.AddIncidentAsync(current, cancellationToken);
        ReliabilityInstrumentation.IncidentsDeclared.Add(1, new KeyValuePair<string, object?>("incident.severity", current.Severity.ToString()));
        return current;
    }

    public async Task<Incident> AddIncidentTimelineAsync(
        Guid incidentId,
        DateTimeOffset at,
        string author,
        IncidentTimelineEventType type,
        string message,
        CancellationToken cancellationToken = default)
    {
        var incident = await GetRequiredIncidentAsync(incidentId, cancellationToken);
        var updated = incident.AddTimelineEvent(at, author, type, message);
        await store.SaveIncidentAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<Incident> MitigateIncidentAsync(
        Guid incidentId,
        string author,
        string message,
        CancellationToken cancellationToken = default)
    {
        var incident = await GetRequiredIncidentAsync(incidentId, cancellationToken);
        var updated = incident.Mitigate(clock.UtcNow, author, message);
        await store.SaveIncidentAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<Incident> ResolveIncidentAsync(
        Guid incidentId,
        string author,
        string message,
        CancellationToken cancellationToken = default)
    {
        var incident = await GetRequiredIncidentAsync(incidentId, cancellationToken);
        var resolved = incident.Resolve(clock.UtcNow, author, message);
        var slos = await store.GetSlosAsync(cancellationToken);
        var slis = await store.GetSlisAsync(cancellationToken);
        var metrics = await store.GetMetricsAsync(cancellationToken: cancellationToken);
        foreach (var slo in slos.Where(item => resolved.AffectedServices.Contains(item.ServiceSlug, StringComparer.OrdinalIgnoreCase)))
        {
            var sli = slis.Single(item => item.Id == slo.SliId);
            resolved = resolved.AttributeBudgetImpact(IncidentBudgetAttributor.Calculate(resolved, slo, sli, metrics, clock.UtcNow));
        }

        await store.SaveIncidentAsync(resolved, cancellationToken);
        return resolved;
    }

    public async Task<Postmortem> CreatePostmortemAsync(
        Postmortem postmortem,
        CancellationToken cancellationToken = default)
    {
        _ = await GetRequiredIncidentAsync(postmortem.IncidentId, cancellationToken);
        await store.AddPostmortemAsync(postmortem, cancellationToken);
        return postmortem;
    }

    public async Task<Postmortem> SubmitPostmortemForReviewAsync(Guid id, CancellationToken cancellationToken = default) =>
        await UpdatePostmortemAsync(id, postmortem => postmortem.SubmitForReview(), cancellationToken);

    public async Task<Postmortem> ApprovePostmortemAsync(Guid id, CancellationToken cancellationToken = default) =>
        await UpdatePostmortemAsync(id, postmortem => postmortem.Approve(), cancellationToken);

    public async Task<Postmortem> PublishPostmortemAsync(Guid id, CancellationToken cancellationToken = default) =>
        await UpdatePostmortemAsync(id, postmortem => postmortem.Publish(), cancellationToken);

    public async Task<Postmortem> AddActionItemAsync(
        Guid postmortemId,
        string description,
        string owner,
        DateOnly dueDate,
        CancellationToken cancellationToken = default) =>
        await UpdatePostmortemAsync(
            postmortemId,
            postmortem => postmortem.AddActionItem(description, owner, dueDate),
            cancellationToken);

    public async Task<Postmortem> CompleteActionItemAsync(
        Guid postmortemId,
        Guid actionItemId,
        CancellationToken cancellationToken = default) =>
        await UpdatePostmortemAsync(
            postmortemId,
            postmortem => postmortem.CompleteActionItem(actionItemId, clock.UtcNow),
            cancellationToken);

    public async Task<IReadOnlyList<ActionItem>> GetOverdueActionItemsAsync(CancellationToken cancellationToken = default) =>
        PostmortemThemeAnalyzer.OverdueActions(await store.GetPostmortemsAsync(cancellationToken), clock.UtcNow);

    public async Task<ServiceReliabilityReport> GetServiceReportAsync(string serviceSlug, CancellationToken cancellationToken = default)
    {
        var service = await GetRequiredServiceAsync(serviceSlug, cancellationToken);
        var slos = (await store.GetSlosAsync(cancellationToken))
            .Where(slo => string.Equals(slo.ServiceSlug, service.Slug, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var statuses = new List<SloStatus>();
        foreach (var slo in slos)
        {
            statuses.Add(await GetSloStatusAsync(slo.Id, cancellationToken));
        }

        var incidents = (await store.GetIncidentsAsync(cancellationToken))
            .Where(incident => incident.AffectedServices.Contains(service.Slug, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var postmortems = await store.GetPostmortemsAsync(cancellationToken);
        var dependencyRisk = await DetermineDependencyRiskAsync(service, cancellationToken);
        var remainingBudget = statuses.Count == 0 ? 100m : statuses.Min(status => status.ErrorBudget.RemainingPercent);
        var scorecard = ServiceScorecardCalculator.Calculate(
            service,
            statuses.Count > 0,
            postmortems.Any(postmortem =>
                postmortem.CreatedAt >= clock.UtcNow.AddDays(-90) &&
                incidents.Any(incident => incident.Id == postmortem.IncidentId)),
            remainingBudget,
            dependencyRisk);
        return new ServiceReliabilityReport(service.Slug, service.OwningTeam, statuses, incidents, scorecard);
    }

    public async Task<TeamReliabilityReport> GetTeamReportAsync(string team, CancellationToken cancellationToken = default)
    {
        var services = (await store.GetServicesAsync(cancellationToken))
            .Where(service => string.Equals(service.OwningTeam, team, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var reports = new List<ServiceReliabilityReport>();
        foreach (var service in services)
        {
            reports.Add(await GetServiceReportAsync(service.Slug, cancellationToken));
        }

        return new TeamReliabilityReport(team, reports);
    }

    public async Task<QuarterReliabilityReport> GetQuarterReportAsync(
        int year,
        int quarter,
        CancellationToken cancellationToken = default)
    {
        if (quarter is < 1 or > 4)
        {
            throw new DomainRuleViolationException("Quarter must be between one and four.");
        }

        var start = new DateTimeOffset(year, (quarter - 1) * 3 + 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(3);
        var incidents = (await store.GetIncidentsAsync(cancellationToken))
            .Where(incident => incident.StartedAt >= start && incident.StartedAt < end)
            .ToArray();
        var services = await store.GetServicesAsync(cancellationToken);
        var slos = await store.GetSlosAsync(cancellationToken);
        var slis = await store.GetSlisAsync(cancellationToken);
        var consumers = new List<(string ServiceSlug, decimal ConsumedPercent)>();
        foreach (var service in services)
        {
            var metrics = await store.GetMetricsAsync(service.Slug, cancellationToken);
            var serviceSlos = slos.Where(slo => string.Equals(slo.ServiceSlug, service.Slug, StringComparison.OrdinalIgnoreCase));
            var maxConsumed = 0m;
            foreach (var slo in serviceSlos)
            {
                var sli = slis.Single(item => item.Id == slo.SliId);
                var attainment = SliEvaluator.Evaluate(sli, metrics, start, end);
                maxConsumed = Math.Max(maxConsumed, ErrorBudgetCalculator.Calculate(slo.Target, attainment).ConsumedPercent);
            }

            if (maxConsumed > 0m) consumers.Add((service.Slug, maxConsumed));
        }

        var alerts = await store.GetAlertsAsync(cancellationToken);
        return new QuarterReliabilityReport(
            year,
            quarter,
            incidents,
            incidents
                .GroupBy(incident => DateOnly.FromDateTime(incident.StartedAt.UtcDateTime))
                .OrderBy(group => group.Key)
                .Select(group => new IncidentTrendPoint(group.Key, group.Count()))
                .ToArray(),
            consumers.OrderByDescending(item => item.ConsumedPercent).Take(10).ToArray(),
            AlertQualityAnalyzer.Analyze(alerts, incidents));
    }

    public async Task<AlertQualityReport> GetAlertQualityAsync(CancellationToken cancellationToken = default) =>
        AlertQualityAnalyzer.Analyze(
            await store.GetAlertsAsync(cancellationToken),
            await store.GetIncidentsAsync(cancellationToken));

    public async Task<ServiceMaturityScorecard> GetScorecardAsync(string serviceSlug, CancellationToken cancellationToken = default) =>
        (await GetServiceReportAsync(serviceSlug, cancellationToken)).Scorecard;

    public async Task<DeploymentGateDecision> GetDeploymentGateAsync(
        string serviceSlug,
        CancellationToken cancellationToken = default)
    {
        var report = await GetServiceReportAsync(serviceSlug, cancellationToken);
        var remaining = report.SloStatuses.Count == 0
            ? 100m
            : report.SloStatuses.Min(status => status.ErrorBudget.RemainingPercent);
        return ErrorBudgetPolicyEngine.Evaluate(report.ServiceSlug, remaining, budgetPolicy);
    }

    public async Task SeedDemoAsync(CancellationToken cancellationToken = default)
    {
        if ((await store.GetServicesAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        var now = clock.UtcNow;
        var definitions = new[]
        {
            ("edge-gateway", "Edge Gateway", CriticalityTier.Tier0, "Platform Reliability", "platform-primary", Array.Empty<string>()),
            ("identity", "Identity", CriticalityTier.Tier0, "Identity Engineering", "identity-primary", new[] { "edge-gateway" }),
            ("catalog", "Catalogue", CriticalityTier.Tier1, "Commerce Core", "commerce-primary", new[] { "identity" }),
            ("checkout", "Checkout", CriticalityTier.Tier0, "Commerce Core", "commerce-primary", new[] { "identity", "catalog" }),
            ("payments", "Payments Adapter", CriticalityTier.Tier0, "Payments", "payments-primary", new[] { "checkout" }),
            ("orders", "Order Orchestrator", CriticalityTier.Tier1, "Commerce Core", "commerce-primary", new[] { "payments", "catalog" }),
            ("inventory", "Inventory", CriticalityTier.Tier1, "Supply Chain", "supply-primary", new[] { "catalog" }),
            ("shipping", "Shipping", CriticalityTier.Tier2, "Supply Chain", "supply-primary", new[] { "orders", "inventory" }),
            ("notifications", "Notifications", CriticalityTier.Tier2, "Customer Experience", "cx-primary", new[] { "orders" }),
            ("search", "Search", CriticalityTier.Tier2, "Discovery", "discovery-primary", new[] { "catalog" }),
            ("analytics", "Analytics Pipeline", CriticalityTier.Tier3, "Data Platform", "data-primary", new[] { "orders" }),
            ("status-page", "Status Page", CriticalityTier.Tier1, "Platform Reliability", "platform-primary", new[] { "edge-gateway" })
        };

        foreach (var definition in definitions)
        {
            var service = ServiceDefinition.Create(
                definition.Item1,
                definition.Item2,
                definition.Item3,
                definition.Item4,
                definition.Item5,
                $"https://github.example.invalid/northstar/{definition.Item1}",
                $"https://runbooks.example.invalid/{definition.Item1}",
                definition.Item6,
                now);
            await store.AddServiceAsync(service, cancellationToken);
            var sli = SliDefinition.Create(
                $"{service.Name} availability",
                service.Slug,
                SliAggregationMode.RequestBased,
                SliKind.Availability,
                SliFilter.All,
                null,
                now);
            await store.AddSliAsync(sli, cancellationToken);
            await store.AddSloAsync(SloDefinition.Create(
                $"{service.Name} 99.9 availability",
                sli.Id,
                service.Slug,
                0.999m,
                SloWindowKind.Rolling,
                30,
                CalendarWindowPeriod.Monthly,
                now), cancellationToken);

            var generated = simulator.Generate(new TrafficSimulationRequest(
                service.Slug,
                now.AddHours(-12),
                now,
                TimeSpan.FromMinutes(5),
                service.Tier == CriticalityTier.Tier0 ? 400 : 160,
                "/api/request",
                "eu-west",
                service.Tier.ToString(),
                0.0002m,
                null,
                26026 + definitions.ToList().FindIndex(item => item.Item1 == service.Slug)));
            await store.AddMetricsAsync(generated, cancellationToken);
        }
    }

    private async Task<DependencyRisk> DetermineDependencyRiskAsync(ServiceDefinition service, CancellationToken cancellationToken)
    {
        if (service.Dependencies.Count == 0)
        {
            return DependencyRisk.Healthy;
        }

        var allServices = await store.GetServicesAsync(cancellationToken);
        var slos = await store.GetSlosAsync(cancellationToken);
        foreach (var dependency in service.Dependencies)
        {
            if (!allServices.Any(item => string.Equals(item.Slug, dependency, StringComparison.OrdinalIgnoreCase)))
            {
                return DependencyRisk.Critical;
            }

            var dependencySlo = slos.FirstOrDefault(item => string.Equals(item.ServiceSlug, dependency, StringComparison.OrdinalIgnoreCase));
            if (dependencySlo is not null)
            {
                var status = await GetSloStatusAsync(dependencySlo.Id, cancellationToken);
                if (status.ErrorBudget.RemainingPercent < 25m)
                {
                    return DependencyRisk.Critical;
                }

                if (status.ErrorBudget.RemainingPercent < 50m)
                {
                    return DependencyRisk.Elevated;
                }
            }
        }

        return DependencyRisk.Healthy;
    }

    private async Task<Postmortem> UpdatePostmortemAsync(
        Guid id,
        Func<Postmortem, Postmortem> mutate,
        CancellationToken cancellationToken)
    {
        var postmortem = await GetRequiredPostmortemAsync(id, cancellationToken);
        var updated = mutate(postmortem);
        await store.SavePostmortemAsync(updated, cancellationToken);
        return updated;
    }

    private async Task<ServiceDefinition> GetRequiredServiceAsync(string slug, CancellationToken cancellationToken) =>
        await store.GetServiceAsync(slug, cancellationToken) ?? throw new KeyNotFoundException($"Service '{slug}' was not found.");

    private async Task<SliDefinition> GetRequiredSliAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetSliAsync(id, cancellationToken) ?? throw new KeyNotFoundException("SLI was not found.");

    private async Task<SloDefinition> GetRequiredSloAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetSloAsync(id, cancellationToken) ?? throw new KeyNotFoundException("SLO was not found.");

    private async Task<Incident> GetRequiredIncidentAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetIncidentAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Incident was not found.");

    private async Task<Postmortem> GetRequiredPostmortemAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetPostmortemAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Postmortem was not found.");
}
