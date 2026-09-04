using Idp.Application.Documents;
using Idp.Application.Exporting;
using Idp.Application.Metrics;
using Idp.Application.Pipeline;
using Idp.Application.Review;
using Idp.Application.Suppliers;
using Idp.Application.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Idp.Application;

/// <summary>Registers the application layer: pipeline, use-case services and the guardrail engine.</summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Deterministic validation guardrails (stateless -> singletons).
        services.AddSingleton<IValidationRule, LineItemArithmeticRule>();
        services.AddSingleton<IValidationRule, SubtotalConsistencyRule>();
        services.AddSingleton<IValidationRule, TaxCalculationRule>();
        services.AddSingleton<IValidationRule, TotalConsistencyRule>();
        services.AddSingleton<IValidationRule, DateSanityRule>();
        services.AddSingleton<IValidationRule, DuplicateInvoiceRule>();
        services.AddSingleton<IValidationRule, CurrencyConsistencyRule>();
        services.AddSingleton<IValidationRule, SupplierExistenceRule>();
        services.AddSingleton<IValidationRule, TaxIdFormatRule>();
        services.AddSingleton<IValidationRule, ThreeWayMatchRule>();
        services.AddSingleton<IValidationEngine, DeterministicValidationEngine>();

        // Pipeline + use-case services (scoped over the unit of work).
        services.AddScoped<DocumentPipeline>();
        services.AddScoped<DocumentIntakeService>();
        services.AddScoped<ExportService>();
        services.AddScoped<ReviewService>();
        services.AddScoped<SupplierService>();
        services.AddScoped<IStpMetricsService, StpMetricsService>();

        return services;
    }
}
