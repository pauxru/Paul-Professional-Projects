using Idp.Application.Abstractions;
using Idp.Application.Ai;
using Idp.Application.Classification;
using Idp.Application.Configuration;
using Idp.Application.Documents;
using Idp.Application.Exporting;
using Idp.Application.Extraction;
using Idp.Application.Review;
using Idp.Application.Suppliers;
using Idp.Infrastructure.Ai;
using Idp.Infrastructure.Classification;
using Idp.Infrastructure.Exporting;
using Idp.Infrastructure.Extraction;
using Idp.Infrastructure.Extraction.Parsing;
using Idp.Infrastructure.Generation;
using Idp.Infrastructure.Ingestion;
using Idp.Infrastructure.Persistence;
using Idp.Infrastructure.Storage;
using Idp.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Idp.Infrastructure;

/// <summary>Registers the infrastructure layer: persistence, adapters, generation and options binding.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        BindOptions(services, configuration);

        // Persistence.
        services.AddDbContext<IdpDbContext>((sp, options) =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlite(db.ConnectionString);
        });
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<IReviewRepository, ReviewRepository>();
        services.AddScoped<IExportRepository, ExportRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        // Time + storage + outbox.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IObjectStore, LocalFileSystemObjectStore>();
        services.AddSingleton<IExportOutbox, FileSystemExportOutbox>();

        // Simulated ERP (shared idempotency ledger + client).
        services.AddSingleton<SimulatedErpLedger>();
        services.AddSingleton<IErpExportClient, SimulatedErpExportClient>();

        // Parsers (order does not matter; the pipeline selects by CanParse).
        services.AddSingleton<IDocumentParser, OcrJsonParser>();
        services.AddSingleton<IDocumentParser, CsvParser>();
        services.AddSingleton<IDocumentParser, PlainTextParser>();

        // Classification (deterministic default; optional LLM adapter behind config).
        services.AddSingleton<IChatModel, StubChatModel>();
        services.AddSingleton<RulesDocumentClassifier>();
        services.AddSingleton<ChatModelClassifier>();
        services.AddSingleton<IDocumentClassifier>(sp =>
        {
            var provider = sp.GetRequiredService<IOptions<ClassifierOptions>>().Value.Provider;
            return string.Equals(provider, "Llm", StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<ChatModelClassifier>()
                : sp.GetRequiredService<RulesDocumentClassifier>();
        });

        // Extraction.
        services.AddSingleton<IFieldExtractor, DeterministicFieldExtractor>();

        // Generation + seeding.
        services.AddSingleton<DocumentGenerator>();
        services.AddSingleton<AccuracyEvaluator>();
        services.AddScoped<DemoDataSeeder>();

        // Drop-folder watcher (disabled by default).
        services.AddHostedService<DropFolderWatcher>();

        return services;
    }

    private static void BindOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PipelineOptions>()
            .Bind(configuration.GetSection(PipelineOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ValidationOptions>()
            .Bind(configuration.GetSection(ValidationOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ReviewOptions>()
            .Bind(configuration.GetSection(ReviewOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ExportOptions>()
            .Bind(configuration.GetSection(ExportOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ClassifierOptions>()
            .Bind(configuration.GetSection(ClassifierOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();
    }

    /// <summary>Create the SQLite schema (no migrations) and optionally seed the demo corpus.</summary>
    public static async Task InitializeDatabaseAsync(
        IServiceProvider services, bool seed, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdpDbContext>();
        await db.Database.EnsureCreatedAsync(ct);

        if (seed)
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
            await seeder.SeedAsync(ct);
        }
    }
}
