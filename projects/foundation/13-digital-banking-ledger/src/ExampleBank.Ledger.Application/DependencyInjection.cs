using ExampleBank.Ledger.Application.Accounts;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Application.Entries;
using ExampleBank.Ledger.Application.Fees;
using ExampleBank.Ledger.Application.Fx;
using ExampleBank.Ledger.Application.Holds;
using ExampleBank.Ledger.Application.Integrity;
using ExampleBank.Ledger.Application.Interest;
using ExampleBank.Ledger.Application.Reports;
using ExampleBank.Ledger.Application.Reversals;
using ExampleBank.Ledger.Application.Statements;
using ExampleBank.Ledger.Application.Transfers;
using Microsoft.Extensions.DependencyInjection;

namespace ExampleBank.Ledger.Application;

/// <summary>Registers the application services. Infrastructure supplies the ports they depend on.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddLedgerApplication(this IServiceCollection services)
    {
        services.AddScoped<LedgerCommandExecutor>();

        services.AddScoped<AccountService>();
        services.AddScoped<EntryService>();
        services.AddScoped<TransferService>();
        services.AddScoped<HoldService>();
        services.AddScoped<ReversalService>();
        services.AddScoped<FeeService>();
        services.AddScoped<InterestService>();
        services.AddScoped<FxService>();
        services.AddScoped<StatementService>();
        services.AddScoped<TrialBalanceService>();
        services.AddScoped<IntegrityService>();

        return services;
    }
}
