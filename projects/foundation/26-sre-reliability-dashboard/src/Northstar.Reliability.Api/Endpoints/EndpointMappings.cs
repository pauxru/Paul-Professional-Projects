using Microsoft.AspNetCore.Http.HttpResults;
using Northstar.Reliability.Api.Models;
using Northstar.Reliability.Application.Abstractions;
using Northstar.Reliability.Application.Services;
using Northstar.Reliability.Application.Simulation;
using Northstar.Reliability.Domain.Incidents;
using Northstar.Reliability.Domain.Postmortems;
using Northstar.Reliability.Domain.Services;
using Northstar.Reliability.Domain.Slo;
using Northstar.Reliability.Domain.Telemetry;

namespace Northstar.Reliability.Api.Endpoints;

public static class AuthEndpointMappings
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth");
        group.MapPost("/token", (
            TokenRequest request,
            ITokenIssuer issuer,
            IHostEnvironment environment) =>
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                return Results.NotFound();
            }

            var subject = string.IsNullOrWhiteSpace(request.Subject) ? "demo.sre@northstar.invalid" : request.Subject.Trim();
            var requestedScopes = request.Scopes is { Count: > 0 }
                ? request.Scopes
                : ["reliability.read", "reliability.write", "reliability.admin"];
            var allowedScopes = new[] { "reliability.read", "reliability.write", "reliability.admin" };
            var scopes = requestedScopes.Where(scope => allowedScopes.Contains(scope, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (scopes.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["scopes"] = ["At least one recognized reliability scope is required."]
                });
            }

            return Results.Ok(new { accessToken = issuer.Issue(subject, scopes), tokenType = "Bearer", expiresIn = 28_800, scopes });
        }).AllowAnonymous();
        return app;
    }
}

public static class ServiceEndpointMappings
{
    public static IEndpointRouteBuilder MapServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/services").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        group.MapGet("/", async (int page, int pageSize, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetServicesAsync(page, pageSize, cancellationToken)));

        group.MapPost("/", async (
            CreateServiceRequest request,
            ReliabilityFacade facade,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(
                ("slug", request.Slug),
                ("name", request.Name),
                ("owningTeam", request.OwningTeam),
                ("onCallRotation", request.OnCallRotation),
                ("repositoryUrl", request.RepositoryUrl));
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var service = ServiceDefinition.Create(
                request.Slug!,
                request.Name!,
                request.Tier,
                request.OwningTeam!,
                request.OnCallRotation!,
                request.RepositoryUrl!,
                request.RunbookUrl,
                request.Dependencies,
                clock.UtcNow);
            var created = await facade.CreateServiceAsync(service, cancellationToken);
            return Results.Created($"/api/v1/services/{created.Slug}", created);
        }).RequireAuthorization("reliability.write");

        group.MapGet("/{slug}/scorecard", async (string slug, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetScorecardAsync(slug, cancellationToken)));
        return app;
    }
}

public static class SloEndpointMappings
{
    public static IEndpointRouteBuilder MapSloEndpoints(this IEndpointRouteBuilder app)
    {
        var sliGroup = app.MapGroup("/api/v1/slis").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        sliGroup.MapGet("/", async (ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetSlisAsync(cancellationToken)));
        sliGroup.MapPost("/", async (
            CreateSliRequest request,
            ReliabilityFacade facade,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(("name", request.Name), ("serviceSlug", request.ServiceSlug));
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var sli = SliDefinition.Create(
                request.Name!,
                request.ServiceSlug!,
                request.AggregationMode,
                request.Kind,
                new SliFilter(request.Endpoint, request.Region, request.Tier),
                request.LatencyThresholdMilliseconds,
                clock.UtcNow);
            var created = await facade.CreateSliAsync(sli, cancellationToken);
            return Results.Created($"/api/v1/slis/{created.Id}", created);
        }).RequireAuthorization("reliability.write");

        var sloGroup = app.MapGroup("/api/v1/slos").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        sloGroup.MapGet("/", async (ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetSlosAsync(cancellationToken)));
        sloGroup.MapPost("/", async (
            CreateSloRequest request,
            ReliabilityFacade facade,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(("name", request.Name), ("serviceSlug", request.ServiceSlug));
            if (request.SliId == Guid.Empty) errors["sliId"] = ["A valid SLI is required."];
            if (request.Target <= 0m || request.Target >= 1m) errors["target"] = ["Target must be between zero and one."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var slo = SloDefinition.Create(
                request.Name!,
                request.SliId,
                request.ServiceSlug!,
                request.Target,
                request.WindowKind,
                request.RollingDays,
                request.CalendarPeriod,
                clock.UtcNow);
            var created = await facade.CreateSloAsync(slo, cancellationToken);
            return Results.Created($"/api/v1/slos/{created.Id}", created);
        }).RequireAuthorization("reliability.write");
        sloGroup.MapGet("/{id:guid}/status", async (Guid id, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetSloStatusAsync(id, cancellationToken)));
        sloGroup.MapGet("/{id:guid}/history", async (Guid id, int days, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetSloHistoryAsync(id, days == 0 ? 14 : days, cancellationToken)));

        var metrics = app.MapGroup("/api/v1/metrics").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        metrics.MapGet("/", async (string? service, IReliabilityStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.GetMetricsAsync(service, cancellationToken)));
        metrics.MapPost("/ingest", async (
            IngestMetricsRequest request,
            ReliabilityFacade facade,
            CancellationToken cancellationToken) =>
        {
            if (request.Samples is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["samples"] = ["At least one sample is required."] });
            }

            var missingFields = request.Samples.Any(sample =>
                string.IsNullOrWhiteSpace(sample.ServiceSlug) || string.IsNullOrWhiteSpace(sample.Endpoint) ||
                string.IsNullOrWhiteSpace(sample.Region) || string.IsNullOrWhiteSpace(sample.Tier));
            if (missingFields)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["samples"] = ["Each sample requires serviceSlug, endpoint, region, and tier."] });
            }

            var samples = request.Samples.Select(sample => MetricSample.Create(
                sample.ServiceSlug!,
                sample.Timestamp,
                sample.Endpoint!,
                sample.Region!,
                sample.Tier!,
                sample.Requests,
                sample.Errors,
                sample.LatencyGoodRequests,
                sample.QualityGoodEvents,
                sample.QualityValidEvents,
                sample.FreshnessGoodEvents,
                sample.FreshnessValidEvents,
                sample.ProbeGoodMinutes,
                sample.ProbeTotalMinutes,
                sample.P50LatencyMilliseconds,
                sample.P95LatencyMilliseconds,
                sample.LatencyHistogram,
                sample.Resolution)).ToArray();
            var ingested = await facade.IngestMetricsAsync(samples, cancellationToken);
            return Results.Created("/api/v1/metrics", new { accepted = ingested.Count, samples = ingested });
        }).RequireAuthorization("reliability.write");
        metrics.MapPost("/retention", async (
            ReliabilityFacade facade,
            MetricRetentionPolicy policy,
            CancellationToken cancellationToken) =>
            Results.Ok(await facade.ApplyRetentionAsync(policy, cancellationToken))).RequireAuthorization("reliability.admin");

        var simulator = app.MapGroup("/api/v1/simulator").RequireAuthorization("reliability.write").RequireRateLimiting("api");
        simulator.MapPost("/run", async (SimulationApiRequest request, ReliabilityFacade facade, CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(
                ("serviceSlug", request.ServiceSlug),
                ("endpoint", request.Endpoint),
                ("region", request.Region),
                ("tier", request.Tier));
            if (request.ResolutionMinutes <= 0) errors["resolutionMinutes"] = ["Resolution must be positive."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var samples = await facade.SimulateAsync(new TrafficSimulationRequest(
                request.ServiceSlug!,
                request.StartsAt,
                request.EndsAt,
                TimeSpan.FromMinutes(request.ResolutionMinutes),
                request.BaseRequestsPerMinute,
                request.Endpoint!,
                request.Region!,
                request.Tier!,
                request.BackgroundErrorRate,
                request.Incident,
                request.RandomSeed,
                request.CascadeServiceSlugs), cancellationToken);
            return Results.Ok(new { generated = samples.Count, first = samples.FirstOrDefault(), last = samples.LastOrDefault() });
        });

        var burnRates = app.MapGroup("/api/v1/burn-rates").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        burnRates.MapGet("/", async (Guid? sloId, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetBurnRatesAsync(sloId, cancellationToken)));
        return app;
    }
}

public static class AlertEndpointMappings
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/alerts").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        group.MapGet("/", async (ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetAlertsAsync(cancellationToken)));
        group.MapPost("/evaluate", async (Guid? sloId, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.EvaluateAlertsAsync(sloId, cancellationToken))).RequireAuthorization("reliability.write");
        group.MapPost("/{id:guid}/ack", async (Guid id, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.AcknowledgeAlertAsync(id, cancellationToken))).RequireAuthorization("reliability.write");
        group.MapPost("/{id:guid}/suppress", async (
            Guid id,
            SuppressAlertRequest request,
            ReliabilityFacade facade,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["A suppression reason is required."] });
            }

            return Results.Ok(await facade.SuppressAlertAsync(id, request.Reason, cancellationToken));
        }).RequireAuthorization("reliability.write");
        return app;
    }
}

public static class IncidentEndpointMappings
{
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/incidents").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        group.MapGet("/", async (ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetIncidentsAsync(cancellationToken)));
        group.MapGet("/{id:guid}", async (Guid id, ReliabilityFacade facade, CancellationToken cancellationToken) =>
        {
            var incident = (await facade.GetIncidentsAsync(cancellationToken)).SingleOrDefault(item => item.Id == id);
            return incident is null ? Results.NotFound() : Results.Ok(incident);
        });
        group.MapGet("/{id:guid}/timeline", async (Guid id, ReliabilityFacade facade, CancellationToken cancellationToken) =>
        {
            var incident = (await facade.GetIncidentsAsync(cancellationToken)).SingleOrDefault(item => item.Id == id);
            return incident is null ? Results.NotFound() : Results.Ok(incident.Timeline);
        });
        group.MapGet("/{id:guid}/timings", async (Guid id, ReliabilityFacade facade, CancellationToken cancellationToken) =>
        {
            var incident = (await facade.GetIncidentsAsync(cancellationToken)).SingleOrDefault(item => item.Id == id);
            return incident is null ? Results.NotFound() : Results.Ok(IncidentMetricsCalculator.Calculate(incident));
        });
        group.MapPost("/", async (
            DeclareIncidentRequest request,
            ReliabilityFacade facade,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(
                ("title", request.Title),
                ("commander", request.Commander),
                ("communicationsLead", request.CommunicationsLead),
                ("author", request.Author));
            if (request.AffectedServices is not { Count: > 0 }) errors["affectedServices"] = ["At least one affected service is required."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var now = clock.UtcNow;
            var incident = Incident.Declare(
                request.Title!,
                request.Severity,
                request.AffectedServices!,
                request.Commander!,
                request.CommunicationsLead!,
                request.StartedAt ?? now,
                now,
                request.Author!);
            var created = await facade.DeclareIncidentAsync(incident, request.LinkedAlertIds, cancellationToken);
            return Results.Created($"/api/v1/incidents/{created.Id}", created);
        }).RequireAuthorization("reliability.write");
        group.MapPost("/{id:guid}/timeline", async (
            Guid id,
            IncidentTimelineRequest request,
            ReliabilityFacade facade,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(("author", request.Author), ("message", request.Message));
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return Results.Ok(await facade.AddIncidentTimelineAsync(
                id,
                request.OccurredAt ?? clock.UtcNow,
                request.Author!,
                request.Type,
                request.Message!,
                cancellationToken));
        }).RequireAuthorization("reliability.write");
        group.MapPost("/{id:guid}/mitigate", async (
            Guid id,
            IncidentTransitionRequest request,
            ReliabilityFacade facade,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(("author", request.Author), ("message", request.Message));
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return Results.Ok(await facade.MitigateIncidentAsync(id, request.Author!, request.Message!, cancellationToken));
        }).RequireAuthorization("reliability.write");
        group.MapPost("/{id:guid}/resolve", async (
            Guid id,
            IncidentTransitionRequest request,
            ReliabilityFacade facade,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(("author", request.Author), ("message", request.Message));
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return Results.Ok(await facade.ResolveIncidentAsync(id, request.Author!, request.Message!, cancellationToken));
        }).RequireAuthorization("reliability.write");
        return app;
    }
}

public static class PostmortemEndpointMappings
{
    public static IEndpointRouteBuilder MapPostmortemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/postmortems").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        group.MapGet("/", async (ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetPostmortemsAsync(cancellationToken)));
        group.MapGet("/actions/overdue", async (ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetOverdueActionItemsAsync(cancellationToken)));
        group.MapGet("/themes", async (ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(PostmortemThemeAnalyzer.Analyze(await facade.GetPostmortemsAsync(cancellationToken))));
        group.MapPost("/", async (
            CreatePostmortemRequest request,
            ReliabilityFacade facade,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(("title", request.Title), ("summary", request.Summary));
            if (request.IncidentId == Guid.Empty) errors["incidentId"] = ["A valid incident is required."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var incident = (await facade.GetIncidentsAsync(cancellationToken)).SingleOrDefault(item => item.Id == request.IncidentId);
            if (incident is null) return Results.NotFound();
            var automaticImpact = incident.BudgetImpacts.Count == 0
                ? "Incident impact attribution will be completed when the incident is resolved."
                : $"Automatic attribution: {incident.BudgetImpacts.Sum(impact => impact.BadEventsOrMinutes)} bad events/minutes consumed {incident.BudgetImpacts.Sum(impact => impact.ErrorBudgetConsumed):0.##} budget events.";
            var impact = string.IsNullOrWhiteSpace(request.Impact)
                ? automaticImpact
                : $"{automaticImpact} Additional impact context: {request.Impact.Trim()}";
            var postmortem = Postmortem.Create(
                request.IncidentId,
                request.Title!,
                request.Summary!,
                impact,
                request.TimelineNarrative ?? incident.Timeline.Select(eventItem => $"{eventItem.OccurredAt:u} — {eventItem.Type}: {eventItem.Message}"),
                request.ContributingFactors,
                request.WhatWentWell ?? string.Empty,
                request.WhatWentPoorly ?? string.Empty,
                clock.UtcNow);
            var created = await facade.CreatePostmortemAsync(postmortem, cancellationToken);
            return Results.Created($"/api/v1/postmortems/{created.Id}", created);
        }).RequireAuthorization("reliability.write");
        group.MapPost("/{id:guid}/submit-review", async (Guid id, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.SubmitPostmortemForReviewAsync(id, cancellationToken))).RequireAuthorization("reliability.write");
        group.MapPost("/{id:guid}/approve", async (Guid id, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.ApprovePostmortemAsync(id, cancellationToken))).RequireAuthorization("reliability.admin");
        group.MapPost("/{id:guid}/publish", async (Guid id, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.PublishPostmortemAsync(id, cancellationToken))).RequireAuthorization("reliability.admin");
        group.MapPost("/{id:guid}/actions", async (
            Guid id,
            AddActionItemRequest request,
            ReliabilityFacade facade,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(("description", request.Description), ("owner", request.Owner));
            if (request.DueDate == default) errors["dueDate"] = ["A due date is required."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return Results.Ok(await facade.AddActionItemAsync(id, request.Description!, request.Owner!, request.DueDate, cancellationToken));
        }).RequireAuthorization("reliability.write");
        group.MapPost("/{id:guid}/actions/{actionId:guid}/complete", async (
            Guid id,
            Guid actionId,
            ReliabilityFacade facade,
            CancellationToken cancellationToken) =>
            Results.Ok(await facade.CompleteActionItemAsync(id, actionId, cancellationToken))).RequireAuthorization("reliability.write");
        return app;
    }
}

public static class OperationsEndpointMappings
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var maintenance = app.MapGroup("/api/v1/maintenance-windows").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        maintenance.MapGet("/", async (ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetMaintenanceWindowsAsync(cancellationToken)));
        maintenance.MapPost("/", async (
            MaintenanceWindowRequest request,
            ReliabilityFacade facade,
            CancellationToken cancellationToken) =>
        {
            var errors = RequestValidation.Required(
                ("serviceSlug", request.ServiceSlug),
                ("reason", request.Reason),
                ("declaredBy", request.DeclaredBy));
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var created = await facade.AddMaintenanceWindowAsync(MaintenanceWindow.Create(
                request.ServiceSlug!,
                request.StartsAt,
                request.EndsAt,
                request.Reason!,
                request.DeclaredBy!), cancellationToken);
            return Results.Created($"/api/v1/maintenance-windows/{created.Id}", created);
        }).RequireAuthorization("reliability.write");

        var gates = app.MapGroup("/api/v1/gates").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        gates.MapGet("/{service}/deploy", async (string service, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetDeploymentGateAsync(service, cancellationToken)));

        var reports = app.MapGroup("/api/v1/reports").RequireAuthorization("reliability.read").RequireRateLimiting("api");
        reports.MapGet("/service/{service}", async (string service, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetServiceReportAsync(service, cancellationToken)));
        reports.MapGet("/team/{team}", async (string team, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetTeamReportAsync(team, cancellationToken)));
        reports.MapGet("/quarter/{year:int}/{quarter:int}", async (int year, int quarter, ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetQuarterReportAsync(year, quarter, cancellationToken)));
        reports.MapGet("/alert-quality", async (ReliabilityFacade facade, CancellationToken cancellationToken) =>
            Results.Ok(await facade.GetAlertQualityAsync(cancellationToken)));
        return app;
    }
}
