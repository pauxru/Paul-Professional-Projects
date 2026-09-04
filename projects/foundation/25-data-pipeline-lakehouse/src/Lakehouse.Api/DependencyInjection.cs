using Lakehouse.Api.Configuration;
using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Observability;
using Lakehouse.Application.Orchestration;
using Lakehouse.Application.Quality;
using Lakehouse.Application.Serving;
using Lakehouse.Infrastructure.Pipeline;
using Lakehouse.Infrastructure.Quality;
using Lakehouse.Infrastructure.Serving;
using Lakehouse.Infrastructure.Sources;
using Lakehouse.Infrastructure.Storage;
using Lakehouse.Infrastructure.Time;

namespace Lakehouse.Api;

/// <summary>
/// Composition root: binds <see cref="LakehouseOptions"/> and registers every port with its filesystem
/// implementation. Paths are resolved against the content root so a relative default works everywhere
/// (dev, CI, tests) while an absolute override wins. Services are singletons: the engine is a
/// process-wide batch engine, not a per-request scoped resource.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddLakehouse(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        var options = config.GetSection("Lakehouse").Get<LakehouseOptions>() ?? new LakehouseOptions();
        var lakeRoot = Resolve(contentRoot, options.LakeRoot);
        var servingDb = Resolve(contentRoot, options.ServingDbPath);
        options.LakeRoot = lakeRoot;
        options.ServingDbPath = servingDb;

        services.AddSingleton(options);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IDataFileFormat, JsonlDataFileFormat>();
        services.AddSingleton<ILakehouse>(sp =>
            new FileSystemLakehouse(lakeRoot, sp.GetRequiredService<IDataFileFormat>(), sp.GetRequiredService<IClock>()));
        services.AddSingleton<ICheckpointStore>(_ => new FileCheckpointStore(lakeRoot));
        services.AddSingleton<IDataQualityStore>(_ => new FileDataQualityStore(lakeRoot));
        services.AddSingleton<IRunHistoryStore>(_ => new FileRunHistoryStore(lakeRoot));
        services.AddSingleton<ISourceFeedProvider>(_ => new ContosoFeedProvider(options.Generator.ToGeneratorOptions()));
        services.AddSingleton<DataQualityRunner>();
        services.AddSingleton<LakehousePipeline>();
        services.AddSingleton<DagRunner>();
        services.AddSingleton<ISqlQueryEngine>(sp => new SqliteQueryEngine(sp.GetRequiredService<ILakehouse>(), servingDb));
        services.AddSingleton<FreshnessService>();
        services.AddSingleton<Seed.Seeder>();
        return services;
    }

    private static string Resolve(string contentRoot, string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(contentRoot, path));
}
