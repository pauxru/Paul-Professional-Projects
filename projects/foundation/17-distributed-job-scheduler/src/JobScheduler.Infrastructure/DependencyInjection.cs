using JobScheduler.Application.Abstractions;
using JobScheduler.Application.Options;
using JobScheduler.Application.Services;
using JobScheduler.Infrastructure.Engine;
using JobScheduler.Infrastructure.Execution;
using JobScheduler.Infrastructure.Execution.Handlers;
using JobScheduler.Infrastructure.Metrics;
using JobScheduler.Infrastructure.Persistence;
using JobScheduler.Infrastructure.Seed;
using JobScheduler.Infrastructure.Stores;
using JobScheduler.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobScheduler.Infrastructure;

/// <summary>
/// Composition root for the scheduler. Hosts call <see cref="AddSchedulerSqlite"/> +
/// <see cref="AddSchedulerCore"/> and, if they run the engine, <see cref="AddSchedulerEngine"/>.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Registers the <see cref="AppDbContext"/> against SQLite with the pragma interceptor.</summary>
    public static IServiceCollection AddSchedulerSqlite(this IServiceCollection services, string connectionString)
        => services.AddSchedulerPersistence(o => o.UseSqlite(connectionString));

    /// <summary>Registers the <see cref="AppDbContext"/> with a caller-supplied provider (used by tests).</summary>
    public static IServiceCollection AddSchedulerPersistence(
        this IServiceCollection services, Action<DbContextOptionsBuilder> configure)
    {
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) =>
        {
            configure(o);
            o.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
        });
        return services;
    }

    /// <summary>Registers options, clock, metrics, handlers (allow-list), stores and engine services.</summary>
    public static IServiceCollection AddSchedulerCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EngineOptions>()
            .Bind(configuration.GetSection(EngineOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<NodeOptions>()
            .Bind(configuration.GetSection(NodeOptions.SectionName));

        services.TryAddSingleton<IClock, SystemClock>();

        services.AddSingleton<SchedulerMetrics>();
        services.AddSingleton<ISchedulerMetrics>(sp => sp.GetRequiredService<SchedulerMetrics>());
        services.AddSingleton<IJobCircuitBreaker, InMemoryCircuitBreaker>();

        // The handler allow-list: the ONLY code a payload can cause to run.
        services.AddSingleton<IJobHandler, ReportGeneratorHandler>();
        services.AddSingleton<IJobHandler, CsvTransformHandler>();
        services.AddSingleton<IJobHandler, CleanupHandler>();
        services.AddSingleton<IJobHandler, FlakyHandler>();
        services.AddSingleton<IJobHandler, SlowHandler>();
        services.AddSingleton<IHandlerRegistry, HandlerRegistry>();

        services.AddScoped<IJobDefinitionStore, JobDefinitionStore>();
        services.AddScoped<IJobRunStore, JobRunStore>();
        services.AddScoped<IWorkerRegistry, WorkerRegistry>();
        services.AddScoped<ILeaderElectionStore, LeaderElectionStore>();
        services.AddScoped<IDeadLetterStore, DeadLetterStore>();
        services.AddScoped<IRunLogStore, RunLogStore>();

        services.AddScoped<SchedulerMaterializer>();
        services.AddScoped<DagOrchestrator>();
        services.AddScoped<RunExecutor>();
        services.AddScoped<DemoDataSeeder>();

        return services;
    }

    /// <summary>Adds the worker + leader background services that drive coordination.</summary>
    public static IServiceCollection AddSchedulerEngine(this IServiceCollection services)
    {
        services.AddHostedService<LeaderService>();
        services.AddHostedService<WorkerService>();
        return services;
    }

    /// <summary>Ensures the schema exists and seeds the demo catalogue. Safe to call on every host.</summary>
    public static async Task InitializeSchedulerAsync(this IServiceProvider provider, bool seedDemoData, CancellationToken ct = default)
    {
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync(ct);

        if (seedDemoData)
        {
            var seeder = sp.GetRequiredService<DemoDataSeeder>();
            await seeder.SeedAsync(ct);
        }
    }
}
