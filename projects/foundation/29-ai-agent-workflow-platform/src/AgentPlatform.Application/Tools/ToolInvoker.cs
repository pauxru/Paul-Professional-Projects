using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Domain.Json;
using AgentPlatform.Domain.Tools;

namespace AgentPlatform.Application.Tools;

/// <summary>A request to invoke a tool, carrying the raw (untrusted) arguments from the model.</summary>
public sealed record ToolInvocationRequest(
    string ToolName,
    string RawArgumentsJson,
    ToolExecutionContext Context,
    bool ApprovalSatisfied = false,
    bool ModelInitiated = true);

/// <summary>Result of an invocation plus the metadata the engine needs for tracing and guardrails.</summary>
public sealed record ToolInvocationOutcome(
    ToolResult Result,
    string CanonicalArguments,
    ToolDescriptor? Descriptor)
{
    public bool ApprovalRequired => !Result.Succeeded && Result.Error?.Code == ToolErrorCodes.ApprovalRequired;
}

/// <summary>
/// The single choke point through which every tool call passes. It enforces, <b>in order</b>:
/// tool exists → caller is authorised (scopes) → arguments parse → arguments satisfy the JSON-schema
/// contract (with safe coercion) → rate limit → approval gate → idempotency (for non-idempotent
/// tools) → bounded-timeout execution → output-size cap. Every failure is returned as a structured
/// <see cref="ToolResult"/>; the model can never trigger an exception, code path or effect that is
/// not on this list. This is where "model output is data, not code" is enforced.
/// </summary>
public sealed class ToolInvoker
{
    private readonly IToolRegistry _registry;
    private readonly ToolRateLimiter _rateLimiter;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly int _maxOutputBytes;

    public ToolInvoker(IToolRegistry registry, ToolRateLimiter rateLimiter,
        IIdempotencyStore idempotency, IUnitOfWork unitOfWork, int maxOutputBytes = 64 * 1024)
    {
        _registry = registry;
        _rateLimiter = rateLimiter;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _maxOutputBytes = maxOutputBytes;
    }

    public async Task<ToolInvocationOutcome> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken)
    {
        // 1. Tool must be a registered, statically-known tool.
        if (!_registry.TryGet(request.ToolName, out var tool))
            return Fail(ToolError.Unknown(request.ToolName), "{}", null);

        var descriptor = tool.Descriptor;

        // 2. Authorisation: the caller must hold every required scope.
        var missingScopes = descriptor.RequiredScopes.Where(s => !request.Context.Caller.HasScope(s)).ToArray();
        if (missingScopes.Length > 0)
            return Fail(ToolError.Unauthorized(descriptor.Name, missingScopes), "{}", descriptor);

        // 3. Arguments must be well-formed JSON.
        JsonObject? parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(request.RawArgumentsJson)
                ? new JsonObject()
                : JsonNode.Parse(request.RawArgumentsJson) as JsonObject;
        }
        catch (JsonException ex)
        {
            return Fail(ToolError.Invalid($"Arguments are not valid JSON: {ex.Message}"), "{}", descriptor);
        }

        if (parsed is null)
            return Fail(ToolError.Invalid("Arguments must be a JSON object."), "{}", descriptor);

        // 4. Arguments must satisfy the tool's JSON-schema contract (coerce first, then validate).
        var (coerced, validation) = descriptor.ParameterSchema.CoerceAndValidate(parsed);
        var canonical = CanonicalJson.Serialize(coerced);
        if (!validation.IsValid)
            return Fail(ToolError.Invalid($"Arguments failed schema validation: {validation.Summary}"), canonical, descriptor);

        var arguments = coerced as JsonObject ?? new JsonObject();

        // 5. Rate limit per tenant + tool.
        var rateKey = $"{request.Context.Caller.TenantId}:{descriptor.Name}";
        if (!_rateLimiter.TryAcquire(rateKey, descriptor.RateLimitPerMinute))
            return Fail(new ToolError(ToolErrorCodes.RateLimited, $"Rate limit for '{descriptor.Name}' exceeded.", Transient: true), canonical, descriptor);

        // 6. Approval gate: a tool that requires approval can never be auto-executed by the model.
        if (descriptor.RequiresApproval && !request.ApprovalSatisfied)
        {
            return Fail(new ToolError(ToolErrorCodes.ApprovalRequired,
                $"'{descriptor.Name}' requires human approval before execution."), canonical, descriptor);
        }

        // 7. Idempotency for non-idempotent (typically mutating/external) tools.
        var needsIdempotency = !descriptor.IsNaturallyIdempotent;
        var idempotencyKey = $"{request.Context.RunId}:{request.Context.StepId}:{descriptor.Name}:{canonical}";

        if (needsIdempotency)
        {
            var cached = await _idempotency.TryGetResultAsync(idempotencyKey, cancellationToken);
            if (cached is not null)
            {
                var cachedResult = ToolResult.Ok(cached) with { FromIdempotencyCache = true, Cost = 0m };
                return new ToolInvocationOutcome(cachedResult, canonical, descriptor);
            }
        }

        // 8. Execute under a hard per-tool timeout.
        var result = await ExecuteWithTimeoutAsync(tool, request.Context, arguments, descriptor, cancellationToken);

        // 9. Output-size cap.
        if (result.Succeeded && result.Output is not null)
        {
            var bytes = Encoding.UTF8.GetByteCount(result.Output);
            if (_maxOutputBytes > 0 && bytes > _maxOutputBytes)
                return Fail(new ToolError(ToolErrorCodes.OutputTooLarge,
                    $"Tool output {bytes} bytes exceeds cap {_maxOutputBytes}."), canonical, descriptor);
        }

        // Persist idempotency record atomically with the tool's staged side effects (commit A).
        if (needsIdempotency && result.Succeeded)
        {
            _idempotency.Add(idempotencyKey, request.Context.RunId, descriptor.Name, result.Output ?? "{}", request.Context.Clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new ToolInvocationOutcome(result, canonical, descriptor);
    }

    private static async Task<ToolResult> ExecuteWithTimeoutAsync(ITool tool, ToolExecutionContext context,
        JsonObject arguments, ToolDescriptor descriptor, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (descriptor.Timeout > TimeSpan.Zero) timeoutCts.CancelAfter(descriptor.Timeout);

        var startedAt = context.Clock.UtcNow;
        try
        {
            var result = await tool.ExecuteAsync(context, arguments, timeoutCts.Token);
            return result with { Duration = context.Clock.UtcNow - startedAt };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Fail(new ToolError(ToolErrorCodes.Timeout,
                $"'{descriptor.Name}' timed out after {descriptor.Timeout.TotalSeconds:0.#}s.", Transient: true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Fail(new ToolError(ToolErrorCodes.Internal,
                $"'{descriptor.Name}' threw: {ex.Message}", Transient: true));
        }
    }

    private static ToolInvocationOutcome Fail(ToolError error, string canonical, ToolDescriptor? descriptor) =>
        new(ToolResult.Fail(error), canonical, descriptor);
}
