using System.Text.Json.Nodes;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Tools;
using AgentPlatform.Domain.Workflows;
using AgentPlatform.Infrastructure.Catalog;
using AgentPlatform.UnitTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.UnitTests.Workflows;

/// <summary>Builds the graph validator with the platform's real known tool/prompt/transform names.</summary>
public sealed class ValidatorFixture : IDisposable
{
    private readonly AgentTestHost _host = new();

    public WorkflowGraphValidator Validator { get; }

    public ValidatorFixture()
    {
        using var scope = _host.Services.CreateScope();
        var tools = scope.ServiceProvider.GetServices<ITool>().Select(t => t.Descriptor.Name).ToArray();
        var transforms = scope.ServiceProvider.GetServices<IStateTransform>().Select(t => t.Id).ToArray();
        var prompts = PromptCatalog.All().Select(p => p.Name).Distinct(StringComparer.Ordinal).ToArray();
        Validator = new WorkflowGraphValidator(tools, prompts, transforms);
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>
/// Proves the workflow graph is validated at registration: the seeded catalog is valid, and
/// unreachable steps, unbounded cycles, missing terminals, dangling variable bindings and unknown
/// tool/prompt/transform references are all rejected before a workflow can ever run.
/// </summary>
public sealed class WorkflowGraphValidatorTests : IClassFixture<ValidatorFixture>
{
    private readonly WorkflowGraphValidator _validator;

    public WorkflowGraphValidatorTests(ValidatorFixture fixture) => _validator = fixture.Validator;

    private static ToolStep Tool(string id, string? next, string arg = "TCK-1") => new()
    {
        Id = id,
        ToolName = "get_ticket",
        ArgumentsTemplate = new JsonObject { ["ticket_id"] = arg },
        OutputVariable = id + "_out",
        Next = next,
    };

    private static TerminalStep Terminal(string id) => new() { Id = id, Outcome = WorkflowOutcome.Succeeded };

    private static WorkflowDefinition Def(string start, params WorkflowStep[] steps) => new()
    {
        Name = "test-wf",
        Version = 1,
        StartStepId = start,
        Steps = steps,
        InputVariables = Array.Empty<string>(),
    };

    [Fact]
    public void Seeded_catalog_workflows_are_valid()
    {
        foreach (var workflow in WorkflowCatalog.All())
        {
            var result = _validator.Validate(workflow);
            Assert.True(result.IsValid, $"{workflow.Key}: {result.Summary}");
        }
    }

    [Fact]
    public void Minimal_linear_workflow_is_valid()
        => Assert.True(_validator.Validate(Def("a", Tool("a", "end"), Terminal("end"))).IsValid);

    [Fact]
    public void Unreachable_step_is_rejected()
    {
        var result = _validator.Validate(Def("a", Tool("a", "end"), Terminal("end"), Terminal("orphan")));
        Assert.False(result.IsValid);
        Assert.Contains("unreachable", result.Summary);
    }

    [Fact]
    public void Unbounded_cycle_is_rejected()
    {
        var result = _validator.Validate(Def("a", Tool("a", "b"), Tool("b", "a")));
        Assert.False(result.IsValid);
        Assert.Contains("cycle", result.Summary);
    }

    [Fact]
    public void Missing_terminal_is_rejected()
    {
        var result = _validator.Validate(Def("a", Tool("a", null)));
        Assert.False(result.IsValid);
        Assert.Contains("terminal", result.Summary);
    }

    [Fact]
    public void Dangling_variable_binding_is_rejected()
    {
        var result = _validator.Validate(Def("a", Tool("a", "end", arg: "$ghost"), Terminal("end")));
        Assert.False(result.IsValid);
        Assert.Contains("missing variable", result.Summary);
    }

    [Fact]
    public void Unknown_tool_reference_is_rejected()
    {
        var bad = new ToolStep
        {
            Id = "a",
            ToolName = "nonexistent_tool",
            ArgumentsTemplate = new JsonObject(),
            OutputVariable = "o",
            Next = "end",
        };
        var result = _validator.Validate(Def("a", bad, Terminal("end")));
        Assert.False(result.IsValid);
        Assert.Contains("unknown tool", result.Summary);
    }

    [Fact]
    public void Unknown_transform_reference_is_rejected()
    {
        var bad = new TransformStep { Id = "a", TransformId = "no_such_transform", OutputVariable = "o", Next = "end" };
        var result = _validator.Validate(Def("a", bad, Terminal("end")));
        Assert.False(result.IsValid);
        Assert.Contains("unknown transform", result.Summary);
    }

    [Fact]
    public void Unknown_prompt_reference_is_rejected()
    {
        var bad = new ModelStep
        {
            Id = "a",
            PromptTemplateId = "no_such_prompt",
            OutputVariable = "o",
            MaxIterations = 2,
            Next = "end",
        };
        var result = _validator.Validate(Def("a", bad, Terminal("end")));
        Assert.False(result.IsValid);
        Assert.Contains("unknown prompt", result.Summary);
    }
}
