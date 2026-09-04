using Northstar.Reliability.Application.Simulation;
using Northstar.Reliability.Domain.Incidents;
using Northstar.Reliability.Domain.Postmortems;
using Northstar.Reliability.Domain.Services;
using Northstar.Reliability.Domain.Slo;
using Northstar.Reliability.Domain.Telemetry;

namespace Northstar.Reliability.Api.Models;

public sealed record TokenRequest(string? Subject, IReadOnlyList<string>? Scopes);

public sealed record CreateServiceRequest(
    string? Slug,
    string? Name,
    CriticalityTier Tier,
    string? OwningTeam,
    string? OnCallRotation,
    string? RepositoryUrl,
    string? RunbookUrl,
    IReadOnlyList<string>? Dependencies);

public sealed record CreateSliRequest(
    string? Name,
    string? ServiceSlug,
    SliAggregationMode AggregationMode,
    SliKind Kind,
    string? Endpoint,
    string? Region,
    string? Tier,
    decimal? LatencyThresholdMilliseconds);

public sealed record CreateSloRequest(
    string? Name,
    Guid SliId,
    string? ServiceSlug,
    decimal Target,
    SloWindowKind WindowKind,
    int RollingDays,
    CalendarWindowPeriod CalendarPeriod);

public sealed record MetricSampleInput(
    string? ServiceSlug,
    DateTimeOffset Timestamp,
    string? Endpoint,
    string? Region,
    string? Tier,
    long Requests,
    long Errors,
    long LatencyGoodRequests,
    long QualityGoodEvents,
    long QualityValidEvents,
    long FreshnessGoodEvents,
    long FreshnessValidEvents,
    long ProbeGoodMinutes,
    long ProbeTotalMinutes,
    double P50LatencyMilliseconds,
    double P95LatencyMilliseconds,
    IReadOnlyList<LatencyHistogramBucket>? LatencyHistogram,
    MetricResolution Resolution);

public sealed record IngestMetricsRequest(IReadOnlyList<MetricSampleInput>? Samples);

public sealed record SimulationApiRequest(
    string? ServiceSlug,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int ResolutionMinutes,
    long BaseRequestsPerMinute,
    string? Endpoint,
    string? Region,
    string? Tier,
    decimal BackgroundErrorRate,
    IncidentInjection? Incident,
    int RandomSeed = 26026,
    IReadOnlyList<string>? CascadeServiceSlugs = null);

public sealed record MaintenanceWindowRequest(
    string? ServiceSlug,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Reason,
    string? DeclaredBy);

public sealed record SuppressAlertRequest(string? Reason);

public sealed record DeclareIncidentRequest(
    string? Title,
    IncidentSeverity Severity,
    IReadOnlyList<string>? AffectedServices,
    string? Commander,
    string? CommunicationsLead,
    DateTimeOffset? StartedAt,
    string? Author,
    IReadOnlyList<Guid>? LinkedAlertIds);

public sealed record IncidentTimelineRequest(
    DateTimeOffset? OccurredAt,
    string? Author,
    IncidentTimelineEventType Type,
    string? Message);

public sealed record IncidentTransitionRequest(string? Author, string? Message);

public sealed record CreatePostmortemRequest(
    Guid IncidentId,
    string? Title,
    string? Summary,
    string? Impact,
    IReadOnlyList<string>? TimelineNarrative,
    IReadOnlyList<string>? ContributingFactors,
    string? WhatWentWell,
    string? WhatWentPoorly);

public sealed record AddActionItemRequest(string? Description, string? Owner, DateOnly DueDate);

public static class RequestValidation
{
    public static Dictionary<string, string[]> Required(params (string Name, string? Value)[] values)
    {
        return values
            .Where(value => string.IsNullOrWhiteSpace(value.Value))
            .ToDictionary(value => value.Name, _ => new[] { "This field is required." });
    }
}
