using System.Security.Claims;
using AgentPlatform.Api.Contracts;
using AgentPlatform.Api.Observability;
using AgentPlatform.Api.Security;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Approvals;
using AgentPlatform.Application.Engine;
using AgentPlatform.Application.Evaluation;
using AgentPlatform.Application.Tools;
using AgentPlatform.Domain.Workflows;
using Microsoft.AspNetCore.Mvc;

namespace AgentPlatform.Api.Endpoints;

/// <summary>Baseline the regression gate compares against. Tuned to the measured deterministic run.</summary>
public static class EvalBaselines
{
    public static readonly EvalBaseline Default = new()
    {
        OverallTaskSuccess = 1.00,
        OverallToolSelectionAccuracy = 1.00,
        OverallUnauthorisedHandling = 1.00,
        OverallApprovalCorrectness = 1.00,
        OverallBudgetAdherence = 1.00,
    };
}

/// <summary>Maps every v1 API endpoint. Endpoints are thin; all logic lives in the application layer.</summary>
public static class ApiEndpoints
{
    public static void MapAgentPlatformApi(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");

        MapWorkflows(v1);
        MapRuns(v1);
        MapApprovals(v1);
        MapTools(v1);
        MapPrompts(v1);
        MapEvals(v1);
        MapOps(v1);
        MapDevToken(v1, app.Services);
    }

    private static AgentCaller Caller(HttpContext ctx) => CallerFactory.FromPrincipal(ctx.User);

    // ---------------------------------------------------------------- workflows

    private static void MapWorkflows(RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/workflows").RequireAuthorization(AgentPolicies.Run).WithTags("Workflows");

        group.MapGet("/", (IWorkflowRegistry registry) =>
            Results.Ok(registry.All.Select(w => w.ToResponse())));

        group.MapGet("/{name}", (string name, IWorkflowRegistry registry) =>
        {
            var versions = registry.All.Where(w => w.Name == name).Select(w => w.Version).OrderBy(v => v).ToList();
            if (versions.Count == 0) return Results.NotFound();
            return Results.Ok(new { name, versions, latest = registry.GetLatest(name)?.Version });
        });

        group.MapGet("/{name}/{version:int}", (string name, int version, IWorkflowRegistry registry) =>
        {
            var wf = registry.Get(name, version);
            return wf is null ? Results.NotFound() : Results.Ok(wf.ToResponse());
        });

        group.MapPost("/{name}/{version:int}/validate",
            (string name, int version, IWorkflowRegistry registry, IToolRegistry tools,
             ITransformRegistry transforms, IPromptRegistry prompts) =>
        {
            var wf = registry.Get(name, version);
            if (wf is null) return Results.NotFound();
            var validator = new WorkflowGraphValidator(
                tools.Descriptors.Select(d => d.Name),
                prompts.All.Select(p => p.Name).Distinct(StringComparer.Ordinal),
                transforms.Ids);
            var result = validator.Validate(wf);
            return Results.Ok(new { result.IsValid, result.Errors });
        }).RequireAuthorization(AgentPolicies.Admin);
    }

    // ---------------------------------------------------------------- runs

    private static void MapRuns(RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/runs").RequireAuthorization(AgentPolicies.Run).WithTags("Runs");

        group.MapPost("/", async (StartRunRequest request, HttpContext ctx, WorkflowEngine engine, IRunStore runs, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkflowName))
                return Results.Problem("workflowName is required.", statusCode: StatusCodes.Status400BadRequest);

            var command = new StartRunCommand
            {
                WorkflowName = request.WorkflowName!,
                Version = request.Version,
                Inputs = request.Inputs.ToInputs(),
                IdempotencyKey = request.IdempotencyKey,
                CorrelationId = ctx.CorrelationId(),
            };
            var result = await engine.StartRunAsync(Caller(ctx), command, ct);
            var run = await runs.GetAsync(result.RunId, ct);
            return Results.Created($"/api/v1/runs/{result.RunId}", run!.ToResponse());
        });

        group.MapGet("/", async (HttpContext ctx, IRunStore runs, int? page, int? pageSize, CancellationToken ct) =>
        {
            var caller = Caller(ctx);
            var list = await runs.ListAsync(caller.TenantId, page ?? 0, Math.Clamp(pageSize ?? 50, 1, 200), ct);
            return Results.Ok(list.Select(r => r.ToResponse()));
        });

        group.MapGet("/{id}", async (string id, HttpContext ctx, IRunStore runs, CancellationToken ct) =>
        {
            var run = await runs.GetAsync(id, ct);
            if (run is null || run.TenantId != Caller(ctx).TenantId) return Results.NotFound();
            return Results.Ok(run.ToResponse());
        });

        group.MapGet("/{id}/trace", async (string id, HttpContext ctx, IRunStore runs, CancellationToken ct) =>
        {
            var run = await runs.GetAsync(id, ct);
            if (run is null || run.TenantId != Caller(ctx).TenantId) return Results.NotFound();
            var trace = await runs.GetTraceAsync(id, ct);
            return Results.Ok(new { runId = id, events = trace.Select(e => e.ToResponse()) });
        });

        group.MapPost("/{id}/cancel", async (string id, HttpContext ctx, WorkflowEngine engine, IRunStore runs, CancellationToken ct) =>
        {
            var run = await runs.GetAsync(id, ct);
            if (run is null || run.TenantId != Caller(ctx).TenantId) return Results.NotFound();
            var cancelled = await engine.CancelRunAsync(id, ct);
            return Results.Ok(new { id, cancelled });
        });

        group.MapPost("/{id}/resume", async (string id, HttpContext ctx, WorkflowEngine engine, IRunStore runs, CancellationToken ct) =>
        {
            var run = await runs.GetAsync(id, ct);
            if (run is null || run.TenantId != Caller(ctx).TenantId) return Results.NotFound();
            var result = await engine.ResumeRunAsync(id, ct);
            var updated = await runs.GetAsync(id, ct);
            return Results.Ok(updated!.ToResponse());
        });
    }

    // ---------------------------------------------------------------- approvals

    private static void MapApprovals(RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/approvals").RequireAuthorization(AgentPolicies.Approve).WithTags("Approvals");

        group.MapGet("/", async (HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
        {
            var pending = await approvals.ListPendingAsync(Caller(ctx).TenantId, ct);
            return Results.Ok(pending.Select(a => a.ToResponse()));
        });

        group.MapGet("/{id}", async (string id, HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
        {
            var approval = await approvals.GetAsync(id, ct);
            if (approval is null || approval.TenantId != Caller(ctx).TenantId) return Results.NotFound();
            return Results.Ok(approval.ToResponse());
        });

        group.MapPost("/{id}/approve", (string id, ApprovalDecisionRequest? body, HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
            Decide(id, ApprovalDecision.Approve, body, ctx, approvals, ct));

        group.MapPost("/{id}/reject", (string id, ApprovalDecisionRequest? body, HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
            Decide(id, ApprovalDecision.Reject, body, ctx, approvals, ct));

        group.MapPost("/{id}/modify", (string id, ApprovalDecisionRequest? body, HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
            Decide(id, ApprovalDecision.ModifyAndApprove, body, ctx, approvals, ct));
    }

    private static async Task<IResult> Decide(string id, ApprovalDecision decision, ApprovalDecisionRequest? body,
        HttpContext ctx, ApprovalService approvals, CancellationToken ct)
    {
        var command = new ApprovalCommand(id, decision, body?.Notes, body?.ModifiedArguments?.ToJsonString());
        var outcome = await approvals.DecideAsync(Caller(ctx), command, ct);
        if (!outcome.Applied)
            return Results.Problem(outcome.Error, statusCode: StatusCodes.Status400BadRequest);
        return Results.Ok(new { id, applied = true, run = outcome.Run });
    }

    // ---------------------------------------------------------------- tools + prompts

    private static void MapTools(RouteGroupBuilder v1)
    {
        v1.MapGet("/tools", (IToolRegistry tools) =>
                Results.Ok(tools.Descriptors.OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => d.ToResponse())))
            .RequireAuthorization(AgentPolicies.Run).WithTags("Tools");
    }

    private static void MapPrompts(RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/prompts").RequireAuthorization(AgentPolicies.Run).WithTags("Prompts");

        group.MapGet("/", (IPromptRegistry prompts) =>
            Results.Ok(prompts.All.Select(p => new PromptResponse(p.Id, p.Name, p.Version, p.Description, p.DeclaredVariables, p.Body))));

        group.MapGet("/{name}", (string name, IPromptRegistry prompts) =>
        {
            var versions = prompts.All.Where(p => p.Name == name)
                .OrderBy(p => p.Version)
                .Select(p => new PromptResponse(p.Id, p.Name, p.Version, p.Description, p.DeclaredVariables, p.Body))
                .ToList();
            return versions.Count == 0 ? Results.NotFound() : Results.Ok(versions);
        });
    }

    // ---------------------------------------------------------------- evals

    private static void MapEvals(RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/evals").WithTags("Evaluation");

        group.MapGet("/scenarios", (IEvalScenarioProvider provider) =>
        {
            var scenarios = provider.GetScenarios();
            return Results.Ok(new
            {
                total = scenarios.Count,
                byWorkflow = scenarios.GroupBy(s => s.WorkflowName).ToDictionary(g => g.Key, g => g.Count()),
                byCategory = scenarios.GroupBy(s => s.Category.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            });
        }).RequireAuthorization(AgentPolicies.Run);

        group.MapPost("/run", async (EvalRunRequest? body, EvaluationHarness harness, CancellationToken ct) =>
        {
            var report = await harness.RunAsync(string.IsNullOrWhiteSpace(body?.Workflow) ? null : body!.Workflow, ct);
            return Results.Ok(report);
        }).RequireAuthorization(AgentPolicies.Admin);

        group.MapPost("/compare", async (EvalRunRequest? body, EvaluationHarness harness, CancellationToken ct) =>
        {
            var report = await harness.RunAsync(string.IsNullOrWhiteSpace(body?.Workflow) ? null : body!.Workflow, ct);
            var gate = EvaluationHarness.CompareToBaseline(report, EvalBaselines.Default);
            return Results.Ok(new { report, gate });
        }).RequireAuthorization(AgentPolicies.Admin);
    }

    // ---------------------------------------------------------------- ops

    private static void MapOps(RouteGroupBuilder v1)
    {
        v1.MapGet("/metrics", (IAgentMetrics metrics) =>
        {
            if (metrics is AgentMetrics concrete)
                return Results.Ok(concrete.Snapshot());
            return Results.Ok(new Dictionary<string, long>());
        }).RequireAuthorization(AgentPolicies.Run).WithTags("Ops");
    }

    // ---------------------------------------------------------------- dev token

    private static void MapDevToken(RouteGroupBuilder v1, IServiceProvider services)
    {
        var options = services.GetRequiredService<JwtOptions>();
        if (!options.EnableDevTokenEndpoint) return;

        v1.MapPost("/dev/token", (DevTokenRequest? body, TokenService tokens) =>
        {
            var scopes = body?.Scopes is { Length: > 0 } s ? s : AgentPolicies.All;
            var token = tokens.Issue(
                body?.Subject ?? "dev-user",
                body?.Tenant ?? "tenant-alpha",
                scopes,
                TimeSpan.FromHours(8));
            return Results.Ok(new { access_token = token, token_type = "Bearer", scopes });
        }).AllowAnonymous().WithTags("Dev");
    }
}
