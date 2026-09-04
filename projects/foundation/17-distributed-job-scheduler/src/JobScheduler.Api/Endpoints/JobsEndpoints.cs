using JobScheduler.Api.Auth;
using JobScheduler.Api.Contracts;
using JobScheduler.Api.Middleware;
using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.Domain.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Endpoints;

public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/jobs").WithTags("Jobs");

        group.MapGet("/", ListAsync).RequireAuthorization(AuthConstants.PolicyRead);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AuthConstants.PolicyRead);
        group.MapPost("/", CreateAsync).RequireAuthorization(AuthConstants.PolicyManage);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AuthConstants.PolicyManage);
        group.MapPost("/{id:guid}/enable", EnableAsync).RequireAuthorization(AuthConstants.PolicyManage);
        group.MapPost("/{id:guid}/disable", DisableAsync).RequireAuthorization(AuthConstants.PolicyManage);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AuthConstants.PolicyManage);
        group.MapPost("/{id:guid}/trigger", TriggerAsync).RequireAuthorization(AuthConstants.PolicyTrigger);
    }

    private static async Task<Ok<PagedResponse<JobDefinitionDto>>> ListAsync(
        IJobDefinitionStore store, int? page, int? pageSize, bool? enabledOnly, CancellationToken ct)
    {
        var result = await store.ListAsync(PageRequest.Of(page, pageSize), enabledOnly, ct);
        return TypedResults.Ok(result.ToResponse(d => d.ToDto()));
    }

    private static async Task<Results<Ok<JobDefinitionDto>, NotFound>> GetAsync(
        Guid id, IJobDefinitionStore store, CancellationToken ct)
    {
        var def = await store.GetAsync(id, ct);
        return def is null ? TypedResults.NotFound() : TypedResults.Ok(def.ToDto());
    }

    private static async Task<Results<Created<JobDefinitionDto>, ProblemHttpResult, Conflict<ProblemDetails>>> CreateAsync(
        CreateJobDefinitionRequest request, IJobDefinitionStore store, IHandlerRegistry handlers, IClock clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.HandlerType))
        {
            return TypedResults.Problem("Name and handlerType are required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!handlers.IsRegistered(request.HandlerType))
        {
            return TypedResults.Problem(
                $"Handler '{request.HandlerType}' is not on the allow-list. Registered: {string.Join(", ", handlers.RegisteredTypes)}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!IsValidJson(request.PayloadJson))
        {
            return TypedResults.Problem("PayloadJson must be a valid JSON object.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (await store.GetByNameAsync(request.Name, ct) is not null)
        {
            return TypedResults.Conflict(new ProblemDetails
            {
                Title = "Duplicate job name",
                Detail = $"A job named '{request.Name}' already exists.",
                Status = StatusCodes.Status409Conflict
            });
        }

        JobDefinition def;
        try
        {
            def = JobDefinition.Create(
                request.Name, request.HandlerType, clock.UtcNow,
                payloadJson: request.PayloadJson ?? "{}",
                queue: request.Queue ?? "default",
                priority: request.Priority ?? 0,
                concurrencyLimit: request.ConcurrencyLimit ?? 1,
                singleton: request.Singleton ?? false,
                owner: request.Owner ?? "unknown",
                tags: request.Tags,
                dependsOn: request.DependsOn,
                retryStrategy: request.RetryStrategy ?? RetryStrategy.ExponentialJitter,
                retryBaseSeconds: request.RetryBaseSeconds ?? 5,
                retryMaxSeconds: request.RetryMaxSeconds ?? 300,
                retryJitter: request.RetryJitter ?? 0.2,
                maxAttempts: request.MaxAttempts ?? 3,
                timeoutSeconds: request.TimeoutSeconds ?? 300,
                deadlineSeconds: request.DeadlineSeconds,
                triggerType: request.TriggerType ?? TriggerType.Manual,
                cronExpression: request.CronExpression,
                intervalSeconds: request.IntervalSeconds,
                runAt: request.RunAt,
                timeZoneId: request.TimeZoneId ?? "UTC",
                misfirePolicy: request.MisfirePolicy ?? MisfirePolicy.FireNow,
                catchUpWindowSeconds: request.CatchUpWindowSeconds ?? 300,
                maxCatchUp: request.MaxCatchUp ?? 100);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        // Reject a dependency cycle before persisting.
        try
        {
            var all = await store.ListAllAsync(ct);
            var edges = all.ToDictionary(d => d.Name, d => (IReadOnlyCollection<string>)d.DependsOn, StringComparer.Ordinal);
            edges[def.Name] = def.DependsOn;
            DagValidator.TopologicalOrder(edges);
        }
        catch (DependencyCycleException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        await store.AddAsync(def, ct);
        await store.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/jobs/{def.Id}", def.ToDto());
    }

    private static async Task<Results<Ok<JobDefinitionDto>, NotFound>> UpdateAsync(
        Guid id, UpdateJobDefinitionRequest request, IJobDefinitionStore store, IClock clock, CancellationToken ct)
    {
        var def = await store.GetAsync(id, ct);
        if (def is null)
        {
            return TypedResults.NotFound();
        }

        def.UpdateConfiguration(
            request.PayloadJson ?? def.PayloadJson,
            request.Priority ?? def.Priority,
            request.ConcurrencyLimit ?? def.ConcurrencyLimit,
            request.Singleton ?? def.Singleton,
            request.MaxAttempts ?? def.MaxAttempts,
            request.TimeoutSeconds ?? def.TimeoutSeconds,
            clock.UtcNow);

        await store.SaveChangesAsync(ct);
        return TypedResults.Ok(def.ToDto());
    }

    private static async Task<Results<Ok<JobDefinitionDto>, NotFound>> EnableAsync(
        Guid id, IJobDefinitionStore store, IClock clock, CancellationToken ct)
    {
        var def = await store.GetAsync(id, ct);
        if (def is null)
        {
            return TypedResults.NotFound();
        }
        def.Enable(clock.UtcNow);
        await store.SaveChangesAsync(ct);
        return TypedResults.Ok(def.ToDto());
    }

    private static async Task<Results<Ok<JobDefinitionDto>, NotFound>> DisableAsync(
        Guid id, IJobDefinitionStore store, IClock clock, CancellationToken ct)
    {
        var def = await store.GetAsync(id, ct);
        if (def is null)
        {
            return TypedResults.NotFound();
        }
        def.Disable(clock.UtcNow);
        await store.SaveChangesAsync(ct);
        return TypedResults.Ok(def.ToDto());
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id, IJobDefinitionStore store, CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(id, ct);
        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<JobRunDto>, NotFound, ProblemHttpResult>> TriggerAsync(
        Guid id, TriggerJobRequest? request, HttpContext http, IJobDefinitionStore defs, IJobRunStore runs, IClock clock, CancellationToken ct)
    {
        var def = await defs.GetAsync(id, ct);
        if (def is null)
        {
            return TypedResults.NotFound();
        }

        if (!IsValidJson(request?.PayloadJson))
        {
            return TypedResults.Problem("PayloadJson must be a valid JSON object.", statusCode: StatusCodes.Status400BadRequest);
        }

        var now = clock.UtcNow;
        var correlationId = string.IsNullOrWhiteSpace(request?.CorrelationId) ? http.Current() : request!.CorrelationId!;
        var idempotencyKey = string.IsNullOrWhiteSpace(request?.IdempotencyKey)
            ? $"manual:{def.Id}:{Guid.NewGuid():N}"
            : request!.IdempotencyKey!;

        if (await runs.ExistsByIdempotencyKeyAsync(idempotencyKey, ct))
        {
            return TypedResults.Problem($"A run with idempotency key '{idempotencyKey}' already exists.", statusCode: StatusCodes.Status409Conflict);
        }

        var run = JobRun.Create(def, now, now, idempotencyKey, correlationId, "manual", request?.PayloadJson);
        await runs.AddAsync(run, ct);
        await runs.SaveChangesAsync(ct);
        return TypedResults.Ok(run.ToDto());
    }

    private static bool IsValidJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true; // absent means "use the definition payload"
        }
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(json);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
