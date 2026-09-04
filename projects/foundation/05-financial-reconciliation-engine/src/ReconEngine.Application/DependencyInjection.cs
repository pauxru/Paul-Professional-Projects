using Microsoft.Extensions.DependencyInjection;
using ReconEngine.Application.Common;
using ReconEngine.Application.Exceptions;
using ReconEngine.Application.Ingestion;
using ReconEngine.Application.Matching;
using ReconEngine.Application.Reconciliation;
using ReconEngine.Application.Reporting;
using ReconEngine.Application.RuleSets;

namespace ReconEngine.Application;

/// <summary>Registers the application services (use-cases). Adapters live in the infrastructure layer.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Pure, stateless engine components.
        services.AddSingleton(MatchingEngine.CreateDefault());
        services.AddSingleton<ExceptionClassifier>();
        services.AddSingleton<ReconciliationCalculator>();

        // Use-case services (scoped: they consume per-request stores / unit of work).
        services.AddScoped<ImportService>();
        services.AddScoped<RuleSetService>();
        services.AddScoped<ReconciliationOrchestrator>();
        services.AddScoped<ExceptionWorkflowService>();
        services.AddScoped<ReportService>();

        // Bind defaults so IOptions<ReconciliationOptions> resolves; the API layer binds configuration
        // and adds DataAnnotations validation on top of this.
        services.AddOptions<ReconciliationOptions>();

        return services;
    }
}
