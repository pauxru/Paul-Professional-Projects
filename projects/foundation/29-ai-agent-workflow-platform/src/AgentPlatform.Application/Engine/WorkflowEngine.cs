using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Diagnostics;
using AgentPlatform.Application.Tools;
using AgentPlatform.Domain.Abstractions;
using AgentPlatform.Domain.Approvals;
using AgentPlatform.Domain.Budgets;
using AgentPlatform.Domain.Json;
using AgentPlatform.Domain.Models;
using AgentPlatform.Domain.Prompts;
using AgentPlatform.Domain.Runs;
using AgentPlatform.Domain.Tools;
using AgentPlatform.Domain.Tracing;
using AgentPlatform.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Application.Engine;

/// <summary>
/// The deterministic orchestration engine. It interprets a validated workflow graph, calling the
/// model only where a step declares it, and applying budgets, loop detection, retries, timeouts,
/// approvals and a full execution trace around every action. State and position are persisted after
/// every step so a run resumes exactly where it stopped.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly IWorkflowRegistry _workflows;
    private readonly IPromptRegistry _prompts;
    private readonly ITransformRegistry _transforms;
    private readonly IToolRegistry _tools;
    private readonly ToolInvoker _invoker;
    private readonly IChatModel _model;
    private readonly IRunStore _runs;
    private readonly IApprovalStore _approvals;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly IFaultInjector _faults;
    private readonly IDelayStrategy _delay;
    private readonly IAgentMetrics _metrics;
    private readonly EngineOptions _options;
    private readonly ModelPricing _pricing;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IWorkflowRegistry workflows, IPromptRegistry prompts, ITransformRegistry transforms,
        IToolRegistry tools, ToolInvoker invoker, IChatModel model, IRunStore runs,
        IApprovalStore approvals, IUnitOfWork unitOfWork, IClock clock, IIdGenerator ids,
        IFaultInjector faults, IDelayStrategy delay, IAgentMetrics metrics,
        EngineOptions options, ModelPricing pricing, ILogger<WorkflowEngine> logger)
    {
        _workflows = workflows;
        _prompts = prompts;
        _transforms = transforms;
        _tools = tools;
        _invoker = invoker;
        _model = model;
        _runs = runs;
        _approvals = approvals;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _ids = ids;
        _faults = faults;
        _delay = delay;
        _metrics = metrics;
        _options = options;
        _pricing = pricing;
        _logger = logger;
    }

    // ---------------------------------------------------------------- public API

    public async Task<RunResult> StartRunAsync(AgentCaller caller, StartRunCommand command, CancellationToken cancellationToken)
    {
        var workflow = command.Version is int v
            ? _workflows.Get(command.WorkflowName, v)
            : _workflows.GetLatest(command.WorkflowName);
        if (workflow is null)
            throw new InvalidOperationException($"Workflow '{command.WorkflowName}' (version {command.Version?.ToString() ?? "latest"}) is not registered.");

        if (!caller.HasScope("agents:run"))
            throw new UnauthorizedAccessException("Caller lacks the 'agents:run' scope.");

        // Idempotent run creation: an identical key returns the existing run.
        if (command.IdempotencyKey is { Length: > 0 } key)
        {
            var existing = await _runs.GetByIdempotencyKeyAsync(caller.TenantId, key, cancellationToken);
            if (existing is not null)
                return new RunResult(existing.Id, existing.Status, existing.Outcome, existing.ResultMessage);
        }

        var state = new JsonObject();
        foreach (var (k, value) in command.Inputs) state[k] = value?.DeepClone();

        var budget = command.BudgetOverride ?? workflow.DefaultBudget;
        var run = new WorkflowRun(
            id: _ids.NewId(),
            workflowName: workflow.Name,
            workflowVersion: workflow.Version,
            tenantId: caller.TenantId,
            createdBy: caller.UserId,
            startStepId: workflow.StartStepId,
            stateJson: state.ToJsonString(),
            budgetJson: JsonSerializer.Serialize(budget),
            correlationId: command.CorrelationId ?? _ids.NewId(),
            now: _clock.UtcNow,
            idempotencyKey: command.IdempotencyKey,
            grantedScopes: string.Join(' ', caller.Scopes));

        _runs.Add(run);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _metrics.RunStarted(workflow.Name);

        return await RunLoopAsync(run, workflow, caller, cancellationToken);
    }

    public async Task<RunResult> ResumeRunAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException($"Run '{runId}' not found.");
        if (run.IsTerminal)
            return new RunResult(run.Id, run.Status, run.Outcome, run.ResultMessage);
        if (run.Status == RunState.WaitingForApproval)
            return await ContinueAfterApprovalAsync(runId, cancellationToken);

        var workflow = RequireWorkflow(run);
        return await RunLoopAsync(run, workflow, ResumeCaller(run), cancellationToken);
    }

    public async Task<RunResult> ContinueAfterApprovalAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException($"Run '{runId}' not found.");
        if (run.Status != RunState.WaitingForApproval)
            return new RunResult(run.Id, run.Status, run.Outcome, run.ResultMessage);

        var approval = await _approvals.GetLatestForRunAsync(runId, cancellationToken);
        // Apply timeout default if the pending approval has expired.
        if (approval is not null && approval.IsPending && approval.IsExpired(_clock.UtcNow))
        {
            approval.Expire(_clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        else if (approval is not null && approval.IsPending)
        {
            // Still awaiting a human decision.
            return new RunResult(run.Id, run.Status, run.Outcome, "Awaiting approval decision.");
        }

        var workflow = RequireWorkflow(run);
        var step = workflow.FindStep(run.CurrentStepId!) as HumanApprovalStep
            ?? throw new InvalidOperationException($"Run '{runId}' is paused on a non-approval step.");

        var decidedApproval = approval;
        var (nextStepId, granted) = ResolveApprovalOutcome(step, decidedApproval);

        var state = ParseState(run);
        if (granted && decidedApproval is not null)
        {
            state["__approved"] = decidedApproval.Id;
            // Carry a modified-arguments override if the approver adjusted the action.
            if (decidedApproval.WasModified && decidedApproval.ModifiedArgumentsJson is not null)
                state["__approved_arguments"] = JsonNode.Parse(decidedApproval.ModifiedArgumentsJson);
        }

        run.ResumeFromApproval(nextStepId, state.ToJsonString(), _clock.UtcNow);
        if (decidedApproval is not null)
        {
            var latency = (_clock.UtcNow - decidedApproval.RequestedAt).TotalSeconds;
            _metrics.ApprovalDecided(run.WorkflowName, decidedApproval.Status.ToString(), latency);
            RecordTrace(run, TraceEventType.ApprovalDecided, step.Id, new JsonObject
            {
                ["approvalId"] = decidedApproval.Id,
                ["status"] = decidedApproval.Status.ToString(),
                ["decidedBy"] = decidedApproval.DecidedBy,
                ["granted"] = granted,
            });
        }
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await RunLoopAsync(run, workflow, ResumeCaller(run), cancellationToken);
    }

    public async Task<bool> CancelRunAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(runId, cancellationToken);
        if (run is null) return false;
        if (!run.Cancel(_clock.UtcNow)) return false;
        RecordTrace(run, TraceEventType.RunCompleted, run.CurrentStepId, new JsonObject { ["cancelled"] = true });
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _metrics.RunCompleted(run.WorkflowName, "Cancelled", (run.CompletedAt!.Value - run.CreatedAt).TotalSeconds);
        return true;
    }

    // ---------------------------------------------------------------- run loop

    private async Task<RunResult> RunLoopAsync(WorkflowRun run, WorkflowDefinition workflow, AgentCaller caller, CancellationToken cancellationToken)
    {
        using var runActivity = AgentTelemetry.Source.StartActivity($"workflow:{workflow.Name}");
        runActivity?.SetTag("run.id", run.Id);
        runActivity?.SetTag("workflow.name", workflow.Name);
        runActivity?.SetTag("workflow.version", workflow.Version);
        runActivity?.SetTag("tenant.id", run.TenantId);

        if (run.Status == RunState.Pending)
        {
            run.MarkRunning(_clock.UtcNow);
            RecordTrace(run, TraceEventType.RunStarted, run.CurrentStepId, new JsonObject { ["workflow"] = workflow.Key });
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        else if (run.Status == RunState.WaitingForApproval)
        {
            return new RunResult(run.Id, run.Status, run.Outcome, run.ResultMessage);
        }
        else
        {
            run.MarkRunning(_clock.UtcNow);
        }

        // Per-run wall-clock timeout via a linked token.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var budget = JsonSerializer.Deserialize<BudgetLimits>(run.BudgetJson) ?? BudgetLimits.Default;
        if (budget.MaxWallClockSeconds > 0)
        {
            var remaining = TimeSpan.FromSeconds(budget.MaxWallClockSeconds) - (_clock.UtcNow - (run.StartedAt ?? run.CreatedAt));
            if (remaining <= TimeSpan.Zero) remaining = TimeSpan.FromMilliseconds(1);
            runCts.CancelAfter(remaining);
        }

        var transitions = 0;
        while (run.Status == RunState.Running && run.CurrentStepId is not null)
        {
            if (++transitions > _options.MaxStepTransitions)
            {
                await HaltAsync(run, workflow, BudgetHaltReason.LoopDetected, "Exceeded maximum step transitions.", cancellationToken);
                break;
            }

            var step = workflow.FindStep(run.CurrentStepId)
                ?? throw new InvalidOperationException($"Run '{run.Id}' references unknown step '{run.CurrentStepId}'.");

            using var stepActivity = AgentTelemetry.Source.StartActivity($"step:{step.Kind}");
            stepActivity?.SetTag("run.id", run.Id);
            stepActivity?.SetTag("step.id", step.Id);
            stepActivity?.SetTag("step.kind", step.Kind.ToString());

            var result = await ExecuteStepWithRetryAsync(run, workflow, caller, step, budget, runCts.Token, cancellationToken);
            stepActivity?.SetTag("step.result", result.Kind.ToString());
            if (result.ErrorCode is not null) stepActivity?.SetTag("step.error_code", result.ErrorCode);

            if (result.Kind == StepResultKind.Paused) break;
            if (result.Kind is StepResultKind.Terminal or StepResultKind.Halted or StepResultKind.PermanentFailure) break;
        }

        return new RunResult(run.Id, run.Status, run.Outcome, run.ResultMessage);
    }

    private async Task<StepResult> ExecuteStepWithRetryAsync(WorkflowRun run, WorkflowDefinition workflow, AgentCaller caller,
        WorkflowStep step, BudgetLimits budget, CancellationToken runToken, CancellationToken outerToken)
    {
        StepResult result = StepResult.Transient("not started", null);

        for (var attempt = 1; attempt <= _options.MaxStepAttempts; attempt++)
        {
            var stepExec = new StepExecution(_ids.NewId(), run.Id, step.Id, step.Kind, attempt, run.TakeOrdinal());
            var stateJson = run.StateJson;
            stepExec.Begin(_clock.UtcNow, stateJson.Length > 4096 ? null : stateJson);
            _runs.AddStepExecution(stepExec);
            RecordTrace(run, TraceEventType.StepStarted, step.Id, new JsonObject { ["kind"] = step.Kind.ToString(), ["attempt"] = attempt });

            var execution = new RunExecution(run, workflow, caller, ParseState(run),
                RestoreBudget(run, budget), budget);

            // Per-step timeout, bounded by the per-run token.
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            if (_options.StepTimeout > TimeSpan.Zero) stepCts.CancelAfter(_options.StepTimeout);

            try
            {
                result = await ExecuteStepAsync(execution, step, stepCts.Token);
            }
            catch (OperationCanceledException) when (stepCts.IsCancellationRequested && !outerToken.IsCancellationRequested)
            {
                // A step or run timeout. Run-level timeout is fatal; step-level is transient.
                if (runToken.IsCancellationRequested)
                {
                    stepExec.Fail(_clock.UtcNow, "Run wall-clock timeout.", "timeout");
                    await CommitAsync(run, cancellationToken: outerToken);
                    return await HaltAsync(run, workflow, BudgetHaltReason.WallClockExceeded, "Run exceeded its wall-clock budget.", outerToken);
                }
                result = StepResult.Transient($"Step '{step.Id}' timed out.", "timeout");
            }

            // Persist budget snapshot from this attempt.
            ApplyBudgetSnapshot(run, execution);

            switch (result.Kind)
            {
                case StepResultKind.Advanced:
                    stepExec.Complete(_clock.UtcNow, Truncate(execution.State.ToJsonString()));
                    run.AdvanceTo(result.NextStepId, execution.State.ToJsonString(), _clock.UtcNow);
                    RecordTrace(run, TraceEventType.StepCompleted, step.Id, new JsonObject { ["next"] = result.NextStepId });
                    _metrics.StepExecuted(workflow.Name, step.Kind.ToString(), stepExec.DurationMs);
                    await _faults.SignalAsync("before-step-commit", run, step.Id, outerToken);
                    await CommitAsync(run, outerToken);
                    return result;

                case StepResultKind.Terminal:
                    stepExec.Complete(_clock.UtcNow, Truncate(execution.State.ToJsonString()));
                    run.AdvanceTo(null, execution.State.ToJsonString(), _clock.UtcNow);
                    run.Complete(result.Outcome!.Value, result.Message, _clock.UtcNow);
                    RecordTrace(run, TraceEventType.RunCompleted, step.Id, new JsonObject { ["outcome"] = result.Outcome.ToString() });
                    await _faults.SignalAsync("before-step-commit", run, step.Id, outerToken);
                    await CommitAsync(run, outerToken);
                    _metrics.RunCompleted(workflow.Name, result.Outcome.ToString()!, (run.CompletedAt!.Value - run.CreatedAt).TotalSeconds);
                    return result;

                case StepResultKind.Paused:
                    stepExec.MarkWaitingForApproval(_clock.UtcNow);
                    run.AdvanceTo(step.Id, execution.State.ToJsonString(), _clock.UtcNow);
                    run.PauseForApproval(_clock.UtcNow);
                    await CommitAsync(run, outerToken);
                    return result;

                case StepResultKind.Halted:
                    stepExec.Complete(_clock.UtcNow, Truncate(execution.State.ToJsonString()));
                    run.AdvanceTo(step.Id, execution.State.ToJsonString(), _clock.UtcNow);
                    await HaltAsync(run, workflow, result.HaltReason, result.Message ?? "Halted.", outerToken, commitStep: true);
                    return result;

                case StepResultKind.PermanentFailure:
                    stepExec.Fail(_clock.UtcNow, result.Message ?? "Step failed.", result.ErrorCode);
                    run.Fail(result.Message ?? "Step failed.", _clock.UtcNow);
                    RecordTrace(run, TraceEventType.StepFailed, step.Id, new JsonObject { ["error"] = result.Message, ["code"] = result.ErrorCode });
                    await CommitAsync(run, outerToken);
                    return result;

                case StepResultKind.TransientFailure:
                    stepExec.Fail(_clock.UtcNow, result.Message ?? "Transient failure.", result.ErrorCode);
                    RecordTrace(run, TraceEventType.Retry, step.Id, new JsonObject { ["attempt"] = attempt, ["error"] = result.Message });
                    await CommitAsync(run, outerToken);
                    if (attempt < _options.MaxStepAttempts)
                    {
                        var backoff = TimeSpan.FromMilliseconds(_options.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                        await _delay.DelayAsync(backoff, outerToken);
                    }
                    break;
            }
        }

        // Retries exhausted.
        run.Fail(result.Message ?? "Step failed after retries.", _clock.UtcNow);
        RecordTrace(run, TraceEventType.StepFailed, step.Id, new JsonObject { ["error"] = result.Message, ["exhausted"] = true });
        await CommitAsync(run, outerToken);
        return StepResult.Permanent(result.Message ?? "exhausted", result.ErrorCode);
    }

    // ---------------------------------------------------------------- step dispatch

    private Task<StepResult> ExecuteStepAsync(RunExecution ctx, WorkflowStep step, CancellationToken cancellationToken) => step switch
    {
        ModelStep m => ExecuteModelStepAsync(ctx, m, cancellationToken),
        ToolStep t => ExecuteToolStepAsync(ctx, t, cancellationToken),
        ConditionStep c => Task.FromResult(ExecuteConditionStep(ctx, c)),
        TransformStep tr => Task.FromResult(ExecuteTransformStep(ctx, tr)),
        HumanApprovalStep h => ExecuteHumanApprovalStepAsync(ctx, h, cancellationToken),
        ParallelStep p => ExecuteParallelStepAsync(ctx, p, cancellationToken),
        LoopStep l => ExecuteLoopStepAsync(ctx, l, cancellationToken),
        TerminalStep term => Task.FromResult(ExecuteTerminalStep(ctx, term)),
        _ => throw new NotSupportedException($"Step kind {step.Kind} is not supported."),
    };

    private async Task<StepResult> ExecuteModelStepAsync(RunExecution ctx, ModelStep step, CancellationToken cancellationToken)
    {
        var prompt = _prompts.GetLatest(step.PromptTemplateId)
            ?? throw new InvalidOperationException($"Prompt '{step.PromptTemplateId}' not found.");

        string systemPrompt;
        try
        {
            systemPrompt = prompt.Render(BuildPromptValues(ctx.State, prompt.DeclaredVariables));
        }
        catch (PromptRenderException ex)
        {
            return StepResult.Permanent(ex.Message, "prompt_render");
        }

        var messages = new List<ChatMessage> { ChatMessage.System(systemPrompt) };
        var userText = step.InputVariable is not null ? ValueToString(ResolvePath(step.InputVariable, ctx.State)) : "Proceed.";
        messages.Add(ChatMessage.User(userText));

        var toolDefs = step.AllowedTools
            .Select(name => _tools.Find(name)?.Descriptor)
            .Where(d => d is not null)
            .Select(d => new ChatToolDefinition(d!.Name, d.Description, d.ParameterSchema.ToJsonString()))
            .ToArray();

        var loopDetector = new LoopDetector();

        for (var iteration = 0; iteration < step.MaxIterations; iteration++)
        {
            var request = new ChatRequest
            {
                Messages = messages,
                Tools = toolDefs,
                Options = new ChatOptions { PromptVersion = prompt.Key, MaxOutputTokens = 800 },
                WorkflowIntent = ctx.Workflow.Name,
            };

            ChatCompletion completion;
            var startedAt = _clock.UtcNow;
            try
            {
                using var modelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_options.ModelCallTimeout > TimeSpan.Zero) modelCts.CancelAfter(_options.ModelCallTimeout);
                completion = await _model.CompleteAsync(request, modelCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return StepResult.Transient("Model call timed out.", "timeout");
            }

            var cost = _pricing.Cost(completion.Usage);
            ctx.Budget.AddModelUsage(completion.Usage.TotalTokens, cost);
            _metrics.ModelCalled(_model.ModelId, completion.Usage.TotalTokens);
            RecordTrace(ctx.Run, TraceEventType.ModelCall, step.Id, new JsonObject
            {
                ["model"] = _model.ModelId,
                ["finishReason"] = completion.FinishReason.ToString(),
                ["promptTokens"] = completion.Usage.PromptTokens,
                ["completionTokens"] = completion.Usage.CompletionTokens,
                ["toolCalls"] = completion.ToolCalls.Count,
                ["content"] = Truncate(completion.Content, 500),
            }, durationMs: (long)(_clock.UtcNow - startedAt).TotalMilliseconds,
               promptTokens: completion.Usage.PromptTokens, completionTokens: completion.Usage.CompletionTokens,
               cost: cost, promptVersion: prompt.Key);

            var halt = ctx.Budget.Evaluate(_clock.UtcNow);
            if (halt != BudgetHaltReason.None) return StepResult.Halt(halt, $"Budget halt: {halt}.");

            if (completion.FinishReason == FinishReason.Refusal)
            {
                ctx.State[step.OutputVariable] = completion.Content ?? "The model declined to proceed.";
                ctx.State[step.OutputVariable + "_refused"] = true;
                return StepResult.Advance(step.Next);
            }

            if (!completion.HasToolCalls)
            {
                ctx.State[step.OutputVariable] = completion.Content ?? string.Empty;
                return StepResult.Advance(step.Next);
            }

            // Assistant turn requesting tools.
            messages.Add(ChatMessage.AssistantToolCalls(completion.ToolCalls));

            foreach (var call in completion.ToolCalls)
            {
                var allowed = step.AllowedTools.Contains(call.ToolName, StringComparer.Ordinal);
                ToolInvocationOutcome outcome;
                if (!allowed)
                {
                    // The model tried a tool outside this step's allow-list — a blocked escalation.
                    outcome = new ToolInvocationOutcome(
                        ToolResult.Fail(ToolError.Policy($"Tool '{call.ToolName}' is not permitted in this step.")),
                        "{}", null);
                }
                else
                {
                    var context = new ToolExecutionContext(ctx.Caller, ctx.Run.Id, step.Id, ctx.Run.CorrelationId, _clock);
                    outcome = await _invoker.InvokeAsync(
                        new ToolInvocationRequest(call.ToolName, call.ArgumentsJson, context, ApprovalSatisfied: false, ModelInitiated: true),
                        cancellationToken);
                }

                var toolCost = outcome.Descriptor?.CostWeight ?? 0m;
                var outputBytes = System.Text.Encoding.UTF8.GetByteCount(outcome.Result.ToModelContent());
                ctx.Budget.AddToolUsage(toolCost, outputBytes);
                RecordTrace(ctx.Run, TraceEventType.ToolCall, step.Id, new JsonObject
                {
                    ["tool"] = call.ToolName,
                    ["arguments"] = Truncate(outcome.CanonicalArguments, 800),
                    ["success"] = outcome.Result.Succeeded,
                    ["errorCode"] = outcome.Result.Error?.Code,
                    ["fromCache"] = outcome.Result.FromIdempotencyCache,
                }, toolName: call.ToolName, success: outcome.Result.Succeeded, cost: toolCost);
                _metrics.ToolInvoked(call.ToolName, outcome.Result.Succeeded);

                messages.Add(ChatMessage.ToolResult(call.Id, call.ToolName, outcome.Result.ToModelContent()));

                // Oscillation guard.
                if (loopDetector.RecordAndCheck(call.ToolName, outcome.CanonicalArguments))
                    return StepResult.Halt(BudgetHaltReason.LoopDetected,
                        $"Loop detected: '{call.ToolName}' repeated with identical arguments.");

                var haltAfterTool = ctx.Budget.Evaluate(_clock.UtcNow);
                if (haltAfterTool != BudgetHaltReason.None) return StepResult.Halt(haltAfterTool, $"Budget halt: {haltAfterTool}.");
            }
        }

        // Iteration cap reached without a final answer.
        return StepResult.Halt(BudgetHaltReason.LoopDetected,
            $"Model step '{step.Id}' did not converge within {step.MaxIterations} iterations.");
    }

    private async Task<StepResult> ExecuteToolStepAsync(RunExecution ctx, ToolStep step, CancellationToken cancellationToken)
    {
        var descriptor = _tools.Find(step.ToolName)?.Descriptor;
        if (descriptor is null) return StepResult.Permanent($"Unknown tool '{step.ToolName}'.", ToolErrorCodes.UnknownTool);

        var arguments = ResolveArguments(step.ArgumentsTemplate, ctx.State);

        // Honour an approver's modified arguments, then consume the approval grant.
        var approvalSatisfied = false;
        if (descriptor.RequiresApproval)
        {
            if (ctx.State["__approved"] is JsonValue) approvalSatisfied = true;
            if (ctx.State["__approved_arguments"] is JsonObject modified) arguments = (JsonObject)modified.DeepClone();
        }

        var context = new ToolExecutionContext(ctx.Caller, ctx.Run.Id, step.Id, ctx.Run.CorrelationId, _clock);
        var outcome = await _invoker.InvokeAsync(
            new ToolInvocationRequest(step.ToolName, arguments.ToJsonString(), context, approvalSatisfied, ModelInitiated: false),
            cancellationToken);

        var toolCost = outcome.Descriptor?.CostWeight ?? 0m;
        var outputBytes = System.Text.Encoding.UTF8.GetByteCount(outcome.Result.ToModelContent());
        ctx.Budget.AddToolUsage(toolCost, outputBytes);
        RecordTrace(ctx.Run, TraceEventType.ToolCall, step.Id, new JsonObject
        {
            ["tool"] = step.ToolName,
            ["arguments"] = Truncate(outcome.CanonicalArguments, 800),
            ["success"] = outcome.Result.Succeeded,
            ["errorCode"] = outcome.Result.Error?.Code,
            ["fromCache"] = outcome.Result.FromIdempotencyCache,
        }, toolName: step.ToolName, success: outcome.Result.Succeeded, cost: toolCost);
        _metrics.ToolInvoked(step.ToolName, outcome.Result.Succeeded);

        if (outcome.Result.Succeeded)
        {
            // Clear consumed approval grant.
            ctx.State.Remove("__approved");
            ctx.State.Remove("__approved_arguments");
            ctx.State[step.OutputVariable] = string.IsNullOrEmpty(outcome.Result.Output) ? new JsonObject() : JsonNode.Parse(outcome.Result.Output!);
            var halt = ctx.Budget.Evaluate(_clock.UtcNow);
            if (halt != BudgetHaltReason.None) return StepResult.Halt(halt, $"Budget halt: {halt}.");
            return StepResult.Advance(step.Next);
        }

        var error = outcome.Result.Error!;
        return error.Transient
            ? StepResult.Transient(error.Message, error.Code)
            : StepResult.Permanent(error.Message, error.Code);
    }

    private StepResult ExecuteConditionStep(RunExecution ctx, ConditionStep step)
    {
        var actual = ResolvePath(step.Variable, ctx.State);
        var branch = ConditionEvaluator.Evaluate(actual, step.Operator, step.Value);
        RecordTrace(ctx.Run, TraceEventType.Decision, step.Id, new JsonObject
        {
            ["variable"] = step.Variable,
            ["operator"] = step.Operator.ToString(),
            ["value"] = step.Value?.DeepClone(),
            ["result"] = branch,
            ["next"] = branch ? step.WhenTrue : step.WhenFalse,
        });
        return StepResult.Advance(branch ? step.WhenTrue : step.WhenFalse);
    }

    private StepResult ExecuteTransformStep(RunExecution ctx, TransformStep step)
    {
        if (!_transforms.TryGet(step.TransformId, out var transform))
            return StepResult.Permanent($"Unknown transform '{step.TransformId}'.", "unknown_transform");

        JsonNode? output;
        try
        {
            output = transform.Apply(ctx.State);
        }
        catch (Exception ex)
        {
            return StepResult.Permanent($"Transform '{step.TransformId}' failed: {ex.Message}", "transform_error");
        }

        ctx.State[step.OutputVariable] = output;
        RecordTrace(ctx.Run, TraceEventType.StateTransition, step.Id, new JsonObject
        {
            ["transform"] = step.TransformId,
            ["output"] = step.OutputVariable,
        });
        return StepResult.Advance(step.Next);
    }

    private async Task<StepResult> ExecuteHumanApprovalStepAsync(RunExecution ctx, HumanApprovalStep step, CancellationToken cancellationToken)
    {
        var proposed = ctx.State[step.ProposedActionVariable] ?? new JsonObject();
        var reasoning = ValueToString(ctx.State["reasoning"]) is { Length: > 0 } r ? r : "See proposed action and trace.";
        var risk = proposed is JsonObject po && po["riskLevel"]?.GetValue<string>() is string rl && Enum.TryParse<ToolRiskLevel>(rl, true, out var parsed)
            ? parsed
            : ToolRiskLevel.High;

        var approval = new ApprovalTask(
            id: _ids.NewId(),
            runId: ctx.Run.Id,
            stepId: step.Id,
            tenantId: ctx.Run.TenantId,
            title: step.ActionTitle,
            proposedActionJson: proposed.ToJsonString(),
            reasoningTrace: reasoning,
            riskLevel: risk,
            requestedAt: _clock.UtcNow,
            expiresAt: _clock.UtcNow.AddSeconds(step.TimeoutSeconds));

        _approvals.Add(approval);
        RecordTrace(ctx.Run, TraceEventType.ApprovalRequested, step.Id, new JsonObject
        {
            ["approvalId"] = approval.Id,
            ["title"] = step.ActionTitle,
            ["risk"] = risk.ToString(),
        });
        _metrics.ApprovalRequested(ctx.Workflow.Name);
        await Task.CompletedTask;
        return StepResult.Pause();
    }

    private async Task<StepResult> ExecuteParallelStepAsync(RunExecution ctx, ParallelStep step, CancellationToken cancellationToken)
    {
        // Branches are independent by construction; the reference engine runs them sequentially to
        // preserve single-context transactional integrity (a distributed executor would fan out).
        foreach (var branch in step.Branches)
        {
            var result = await ExecuteInlineAsync(ctx, branch, cancellationToken);
            if (result.Kind != StepResultKind.Advanced) return result;
        }
        RecordTrace(ctx.Run, TraceEventType.StateTransition, step.Id, new JsonObject { ["parallelBranches"] = step.Branches.Count });
        return StepResult.Advance(step.Next);
    }

    private async Task<StepResult> ExecuteLoopStepAsync(RunExecution ctx, LoopStep step, CancellationToken cancellationToken)
    {
        var items = step.OverVariable is not null ? ctx.State[step.OverVariable] as JsonArray : null;
        var count = items is not null ? Math.Min(items.Count, step.MaxIterations) : step.MaxIterations;

        for (var i = 0; i < count; i++)
        {
            if (items is not null && step.ItemVariable is not null)
                ctx.State[step.ItemVariable] = items[i]?.DeepClone();
            ctx.State["__loopIndex"] = i;

            foreach (var body in step.Body)
            {
                var result = await ExecuteInlineAsync(ctx, body, cancellationToken);
                if (result.Kind != StepResultKind.Advanced) return result;
            }
        }

        ctx.State.Remove("__loopIndex");
        RecordTrace(ctx.Run, TraceEventType.StateTransition, step.Id, new JsonObject { ["iterations"] = count });
        return StepResult.Advance(step.Next);
    }

    private StepResult ExecuteTerminalStep(RunExecution ctx, TerminalStep step)
    {
        var message = step.MessageVariable is not null ? ValueToString(ctx.State[step.MessageVariable]) : step.Outcome.ToString();
        return StepResult.Terminate(step.Outcome, message);
    }

    /// <summary>Execute an inline body step (inside Parallel/Loop) without top-level transitions.</summary>
    private async Task<StepResult> ExecuteInlineAsync(RunExecution ctx, WorkflowStep step, CancellationToken cancellationToken) => step switch
    {
        ToolStep t => await ExecuteToolStepAsync(ctx, t with { Next = null }, cancellationToken),
        TransformStep tr => ExecuteTransformStep(ctx, tr with { Next = null }),
        ModelStep m => await ExecuteModelStepAsync(ctx, m with { Next = null }, cancellationToken),
        _ => StepResult.Permanent($"Step kind {step.Kind} cannot be nested.", "invalid_nesting"),
    };

    // ---------------------------------------------------------------- helpers

    private static (string NextStepId, bool Granted) ResolveApprovalOutcome(HumanApprovalStep step, ApprovalTask? approval)
    {
        if (approval is null) return (step.OnReject, false);
        return approval.Status switch
        {
            ApprovalStatus.Approved => (step.OnApprove, true),
            ApprovalStatus.Rejected => (step.OnReject, false),
            ApprovalStatus.Expired => step.DefaultOnTimeout switch
            {
                ApprovalDefault.Approve => (step.OnApprove, true),
                _ => (step.OnReject, false),
            },
            _ => (step.OnReject, false),
        };
    }

    private async Task<StepResult> HaltAsync(WorkflowRun run, WorkflowDefinition workflow, BudgetHaltReason reason,
        string message, CancellationToken cancellationToken, bool commitStep = false)
    {
        run.Halt(reason, message, _clock.UtcNow);
        RecordTrace(run, TraceEventType.BudgetHalt, run.CurrentStepId, new JsonObject { ["reason"] = reason.ToString(), ["message"] = message });
        _metrics.BudgetHalt(workflow.Name, reason.ToString());
        _metrics.RunCompleted(workflow.Name, "Halted", (run.CompletedAt!.Value - run.CreatedAt).TotalSeconds);
        await CommitAsync(run, cancellationToken);
        return StepResult.Halt(reason, message);
    }

    private BudgetTracker RestoreBudget(WorkflowRun run, BudgetLimits limits) => new(
        limits, run.StartedAt ?? run.CreatedAt, currency: "USD",
        tokensUsed: run.TokensUsed, costUsed: run.CostUsed, toolCalls: run.ToolCalls,
        modelCalls: run.ModelCalls, outputBytes: run.OutputBytes);

    private void ApplyBudgetSnapshot(WorkflowRun run, RunExecution execution)
    {
        var b = execution.Budget;
        run.UpdateBudget(b.TokensUsed, b.CostUsed, b.ToolCalls, b.ModelCalls, b.OutputBytes,
            JsonSerializer.Serialize(execution.Limits), _clock.UtcNow);
    }

    private async Task CommitAsync(WorkflowRun run, CancellationToken cancellationToken)
        => await _unitOfWork.SaveChangesAsync(cancellationToken);

    private void RecordTrace(WorkflowRun run, TraceEventType type, string? stepId, JsonObject data,
        long durationMs = 0, int promptTokens = 0, int completionTokens = 0, decimal cost = 0m,
        string? promptVersion = null, string? toolName = null, bool success = true)
    {
        var traceEvent = new TraceEvent(_ids.NewId(), run.Id, run.TakeOrdinal(), type, stepId,
            _clock.UtcNow, data.ToJsonString(), durationMs, promptTokens, completionTokens, cost,
            promptVersion, toolName, success);
        _runs.AddTraceEvent(traceEvent);
    }

    private WorkflowDefinition RequireWorkflow(WorkflowRun run) =>
        _workflows.Get(run.WorkflowName, run.WorkflowVersion)
        ?? throw new InvalidOperationException($"Workflow '{run.WorkflowName}@v{run.WorkflowVersion}' is not registered.");

    private static AgentCaller ResumeCaller(WorkflowRun run)
    {
        var scopes = run.GrantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new AgentCaller(run.CreatedBy, run.TenantId, new HashSet<string>(scopes, StringComparer.Ordinal));
    }

    private static JsonObject ParseState(WorkflowRun run) =>
        JsonNode.Parse(run.StateJson) as JsonObject ?? new JsonObject();

    private static JsonObject ResolveArguments(JsonObject template, JsonObject state)
    {
        var result = new JsonObject();
        foreach (var (key, value) in template)
            result[key] = ResolveNode(value, state);
        return result;
    }

    private static JsonNode? ResolveNode(JsonNode? node, JsonObject state)
    {
        switch (node)
        {
            case JsonObject obj:
                var newObj = new JsonObject();
                foreach (var (k, v) in obj) newObj[k] = ResolveNode(v, state);
                return newObj;
            case JsonArray arr:
                var newArr = new JsonArray();
                foreach (var item in arr) newArr.Add(ResolveNode(item, state));
                return newArr;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith('$'):
                return ResolvePath(s[1..], state);
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>Resolve a <c>var.child.grandchild</c> path against the state bag (object traversal only).</summary>
    private static JsonNode? ResolvePath(string path, JsonObject state)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        JsonNode? current = state[segments[0]];
        for (var i = 1; i < segments.Length && current is not null; i++)
            current = current is JsonObject obj ? obj[segments[i]] : null;
        return current?.DeepClone();
    }

    private static Dictionary<string, string> BuildPromptValues(JsonObject state, IReadOnlyList<string> declared)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in declared)
            values[name] = ValueToString(state[name]);
        return values;
    }

    private static string ValueToString(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    private static string? Truncate(string? value, int max = 4000)
        => value is null ? null : value.Length <= max ? value : value[..max] + "…";
}
