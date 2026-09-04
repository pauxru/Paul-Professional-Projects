using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Ingestion;
using ReconEngine.Domain.Abstractions;
using ReconEngine.Infrastructure.Ingestion;
using ReconEngine.Infrastructure.Persistence;
using ReconEngine.Infrastructure.Persistence.Stores;

namespace ReconEngine.Infrastructure;

/// <summary>Wires the infrastructure adapters: EF Core + SQLite, the stores, the clock and the tokenizers.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IImportStore, ImportStore>();
        services.AddScoped<IRuleSetStore, RuleSetStore>();
        services.AddScoped<IRecordStore, RecordStore>();
        services.AddScoped<IRunStore, RunStore>();
        services.AddScoped<IMatchStore, MatchStore>();
        services.AddScoped<IExceptionStore, ExceptionStore>();

        services.AddScoped<IRowTokenizer, CsvRowTokenizer>();
        services.AddScoped<IRowTokenizer, FixedWidthRowTokenizer>();

        return services;
    }
}
