using LoanOrigination.Application.Ports;
using LoanOrigination.Infrastructure.Persistence;
using LoanOrigination.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LoanOrigination.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLoanInfrastructure(
        this IServiceCollection services,
        string connectionString,
        string objectStorePath)
    {
        services.AddDbContext<LoanDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<ILoanRepository, SqliteLoanRepository>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IObjectStore>(_ => new LocalFileObjectStore(objectStorePath));
        services.AddSingleton<IKycProvider, DeterministicKycProvider>();
        services.AddSingleton<IBureauProvider, DeterministicBureauProvider>();
        services.AddSingleton<IRiskScorer, WeightedRiskScorer>();
        services.AddSingleton<IDisbursementProvider, DeterministicDisbursementProvider>();
        services.AddScoped<IAuditWriter, HashChainAuditWriter>();
        return services;
    }
}
