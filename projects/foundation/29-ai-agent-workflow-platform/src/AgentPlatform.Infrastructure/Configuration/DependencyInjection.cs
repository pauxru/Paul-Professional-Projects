using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Approvals;
using AgentPlatform.Application.Engine;
using AgentPlatform.Application.Evaluation;
using AgentPlatform.Application.Tools;
using AgentPlatform.Domain.Abstractions;
using AgentPlatform.Domain.Security;
using AgentPlatform.Domain.Workflows;
using AgentPlatform.Infrastructure.Catalog;
using AgentPlatform.Infrastructure.Evaluation;
using AgentPlatform.Infrastructure.Models;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Stores;
using AgentPlatform.Infrastructure.Registries;
using AgentPlatform.Infrastructure.Tools;
using AgentPlatform.Infrastructure.Transforms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Infrastructure.Configuration;

/// <summary>
/// Composition root for the platform. Everything is statically wired here: the closed tool
/// allow-list, the deterministic transforms, the validated workflow catalog, and the default
/// deterministic mock model. There is no reflection-based discovery or dynamic loading anywhere.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddAgentPlatform(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DbContextOptionsBuilder>? configureDatabase = null)
    {
        // --- Persistence -----------------------------------------------------
        if (configureDatabase is not null)
        {
            services.AddDbContext<AgentDbContext>(configureDatabase);
        }
        else
        {
            var connectionString = configuration.GetConnectionString("AgentDb")
                ?? "Data Source=agentplatform.db";
            services.AddDbContext<AgentDbContext>(options => options.UseSqlite(connectionString));
        }

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IRunStore, RunStore>();
        services.AddScoped<IApprovalStore, ApprovalStore>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        // --- Deterministic ports --------------------------------------------
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IIdGenerator>(GuidIdGenerator.Instance);
        services.AddSingleton<IFaultInjector, NullFaultInjector>();
        services.AddSingleton<IDelayStrategy, RealDelayStrategy>();
        services.AddSingleton<IAgentMetrics, NullAgentMetrics>();
        services.AddSingleton(EngineOptions());
        services.AddSingleton(ModelPricing.Default);

        // --- Tools (the closed allow-list) ----------------------------------
        services.AddScoped<ITool, SearchKnowledgeBaseTool>();
        services.AddScoped<ITool, GetTicketTool>();
        services.AddScoped<ITool, LookupCustomerTool>();
        services.AddScoped<ITool, SummariseDocumentTool>();
        services.AddScoped<ITool, CalculateTool>();
        services.AddScoped<ITool, UpdateTicketStatusTool>();
        services.AddScoped<ITool, CreateRefundRequestTool>();
        services.AddScoped<ITool, SendEmailTool>();

        // http_get: SSRF-guarded, redirects disabled at the handler level.
        var allowedHosts = configuration.GetSection("Security:HttpAllowList").Get<string[]>()
            ?? new[] { "example.com", "www.example.com", "api.example.com" };
        services.AddSingleton(new UrlSecurityPolicy(allowedHosts));
        services.AddHttpClient("http_get")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<ITool>(sp => new HttpGetTool(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("http_get"),
            sp.GetRequiredService<UrlSecurityPolicy>()));

        services.AddScoped<IToolRegistry, ToolRegistry>();
        services.AddSingleton<ToolRateLimiter>();
        services.AddScoped<ToolInvoker>(sp => new ToolInvoker(
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<ToolRateLimiter>(),
            sp.GetRequiredService<IIdempotencyStore>(),
            sp.GetRequiredService<IUnitOfWork>()));

        // --- Transforms (deterministic, no model) ---------------------------
        services.AddSingleton<IStateTransform, ChunkDocumentTransform>();
        services.AddSingleton<IStateTransform, SummariseChunkTransform>();
        services.AddSingleton<IStateTransform, TriageContextTransform>();
        services.AddSingleton<IStateTransform, BuildRefundInputTransform>();
        services.AddSingleton<IStateTransform, ComputeRefundEligibilityTransform>();
        services.AddSingleton<IStateTransform, BuildProposedActionTransform>();
        services.AddSingleton<IStateTransform, ExtractFieldsTransform>();
        services.AddSingleton<IStateTransform, ValidateExtractionTransform>();
        services.AddSingleton<ITransformRegistry, TransformRegistry>();

        // --- Prompts + workflows (validated at composition) -----------------
        services.AddSingleton<IPromptRegistry>(new PromptRegistry(PromptCatalog.All()));
        services.AddSingleton<IWorkflowRegistry>(BuildWorkflowRegistry);

        // --- Model provider (deterministic mock is the default) -------------
        var modelOptions = new ModelProviderOptions();
        configuration.GetSection("Model").Bind(modelOptions);
        services.AddSingleton(modelOptions);
        services.AddHttpClient("openai");
        services.AddHttpClient("azure");
        services.AddScoped<IChatModel>(sp =>
        {
            var options = sp.GetRequiredService<ModelProviderOptions>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return options.Provider.Trim().ToLowerInvariant() switch
            {
                "openai" => new OpenAiChatModel(factory.CreateClient("openai"), options),
                "azure" => new AzureOpenAiChatModel(factory.CreateClient("azure"), options),
                _ => new DeterministicMockModel(),
            };
        });

        // --- Orchestration services -----------------------------------------
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<ApprovalService>();
        services.AddSingleton<IEvalScenarioProvider, EvalScenarioProvider>();
        services.AddScoped<EvaluationHarness>();

        return services;
    }

    private static EngineOptions EngineOptions() => new();

    private static WorkflowRegistry BuildWorkflowRegistry(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var toolNames = scope.ServiceProvider.GetServices<ITool>().Select(t => t.Descriptor.Name);
        var transformIds = scope.ServiceProvider.GetServices<IStateTransform>().Select(t => t.Id);
        var promptNames = PromptCatalog.All().Select(p => p.Name).Distinct(StringComparer.Ordinal);

        var validator = new WorkflowGraphValidator(toolNames, promptNames, transformIds);
        var definitions = WorkflowCatalog.All();
        foreach (var definition in definitions)
        {
            var result = validator.Validate(definition);
            if (!result.IsValid)
                throw new InvalidOperationException(
                    $"Workflow '{definition.Key}' failed graph validation: {result.Summary}");
        }

        return new WorkflowRegistry(definitions);
    }
}
