using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Tools;
using AgentPlatform.Domain.Abstractions;
using AgentPlatform.Domain.Tools;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.UnitTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.UnitTests.Tools;

/// <summary>
/// Proves the ToolInvoker choke point enforces, in order: registration, authorisation, JSON parsing,
/// schema validation, the approval gate, at-most-once execution, and the output-size cap — every
/// failure surfaced as structured data, never an exception the model could exploit.
/// </summary>
public sealed class ToolInvokerTests
{
    private static ToolExecutionContext Context(IServiceProvider sp, params string[] scopes) =>
        new(AgentTestHost.Caller(scopes.Length == 0 ? new[] { "agents:run", "agents:approve" } : scopes),
            "run-1", "step-1", "corr-1", sp.GetRequiredService<IClock>());

    [Fact]
    public async Task Unauthorised_caller_is_blocked_with_structured_error()
    {
        using var host = new AgentTestHost();
        using var scope = host.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<ToolInvoker>();

        var outcome = await invoker.InvokeAsync(
            new ToolInvocationRequest("calculate", "{\"expression\":\"1+1\"}", Context(scope.ServiceProvider, "agents:approve")),
            default);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(ToolErrorCodes.Unauthorized, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task Unknown_tool_is_rejected()
    {
        using var host = new AgentTestHost();
        using var scope = host.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<ToolInvoker>();

        var outcome = await invoker.InvokeAsync(
            new ToolInvocationRequest("definitely_not_registered", "{}", Context(scope.ServiceProvider)),
            default);

        Assert.Equal(ToolErrorCodes.UnknownTool, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task Malformed_json_arguments_are_rejected()
    {
        using var host = new AgentTestHost();
        using var scope = host.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<ToolInvoker>();

        var outcome = await invoker.InvokeAsync(
            new ToolInvocationRequest("calculate", "{ this is not : json", Context(scope.ServiceProvider)),
            default);

        Assert.Equal(ToolErrorCodes.InvalidArguments, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task Schema_violation_is_rejected()
    {
        using var host = new AgentTestHost();
        using var scope = host.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<ToolInvoker>();

        // Missing the required "expression" property.
        var outcome = await invoker.InvokeAsync(
            new ToolInvocationRequest("calculate", "{}", Context(scope.ServiceProvider)),
            default);

        Assert.Equal(ToolErrorCodes.InvalidArguments, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task Valid_arguments_are_coerced_and_executed()
    {
        using var host = new AgentTestHost();
        using var scope = host.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<ToolInvoker>();

        // top_k supplied as a string is safely coerced to an integer by the schema layer.
        var outcome = await invoker.InvokeAsync(
            new ToolInvocationRequest("search_knowledge_base", "{\"query\":\"refund\",\"top_k\":\"2\"}", Context(scope.ServiceProvider)),
            default);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
    }

    [Fact]
    public async Task Approval_required_tool_cannot_auto_execute()
    {
        using var host = new AgentTestHost();
        using var scope = host.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<ToolInvoker>();

        var outcome = await invoker.InvokeAsync(
            new ToolInvocationRequest("create_refund_request",
                "{\"customer_id\":\"CUST-001\",\"amount_usd\":25,\"reason\":\"test\"}",
                Context(scope.ServiceProvider), ApprovalSatisfied: false),
            default);

        Assert.Equal(ToolErrorCodes.ApprovalRequired, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task Mutating_tool_executes_at_most_once_under_idempotency()
    {
        using var host = new AgentTestHost();
        using var scope = host.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<ToolInvoker>();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var request = new ToolInvocationRequest("create_refund_request",
            "{\"customer_id\":\"CUST-001\",\"amount_usd\":25,\"reason\":\"duplicate protection\"}",
            Context(scope.ServiceProvider), ApprovalSatisfied: true);

        var first = await invoker.InvokeAsync(request, default);
        var second = await invoker.InvokeAsync(request, default);

        Assert.True(first.Result.Succeeded);
        Assert.True(second.Result.Succeeded);
        Assert.False(first.Result.FromIdempotencyCache);
        Assert.True(second.Result.FromIdempotencyCache);
        Assert.Equal(1, await db.RefundRequests.CountAsync());
    }

    [Fact]
    public async Task Output_over_cap_is_rejected()
    {
        using var host = new AgentTestHost();
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var invoker = new ToolInvoker(
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<ToolRateLimiter>(),
            sp.GetRequiredService<IIdempotencyStore>(),
            sp.GetRequiredService<IUnitOfWork>(),
            maxOutputBytes: 4);

        var outcome = await invoker.InvokeAsync(
            new ToolInvocationRequest("calculate", "{\"expression\":\"1+1\"}", Context(sp)),
            default);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(ToolErrorCodes.OutputTooLarge, outcome.Result.Error!.Code);
    }
}
