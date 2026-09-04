using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReconEngine.Application.Common;
using ReconEngine.Application.RuleSets;

namespace ReconEngine.Infrastructure.Persistence;

/// <summary>
/// Creates the database schema if needed and seeds the default, active ruleset. Both steps are
/// idempotent so the API can call this safely on every startup and tests can call it per fixture.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync(ct);

        var ruleSets = sp.GetRequiredService<RuleSetService>();
        var options = sp.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
        await ruleSets.EnsureDefaultAsync(options.DefaultRuleSetName, ct);
    }
}
