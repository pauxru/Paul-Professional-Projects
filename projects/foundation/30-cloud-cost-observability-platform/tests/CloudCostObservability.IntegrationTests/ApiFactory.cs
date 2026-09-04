using CloudCostObservability.Application.Contracts;
using CloudCostObservability.Domain.Models;
using CloudCostObservability.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudCostObservability.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<FinOpsDbContext>>();
            services.AddDbContext<FinOpsDbContext>(options => options.UseSqlite(_connection));
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new FakeClock(new DateOnly(2026, 2, 15)));
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        await db.Database.EnsureCreatedAsync();
        var commerce = Resource("commerce-vm", "commerce");
        var data = Resource("data-vm", "data");
        db.Resources.AddRange(commerce, data);
        db.AllocationRules.Add(new AllocationRuleRow { Order = 10, Method = AllocationMethod.DirectTag });
        db.Costs.AddRange(
            Cost("commerce-vm", 100m, new DateOnly(2026, 2, 1), "Compute Hours"),
            Cost("data-vm", 200m, new DateOnly(2026, 2, 1), "Compute Hours"),
            Cost("commerce-vm", 110m, new DateOnly(2026, 2, 2), "Compute Hours"),
            Cost("data-vm", 210m, new DateOnly(2026, 2, 2), "Compute Hours"));
        await db.SaveChangesAsync();
    }

    public new async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        Dispose();
    }

    private static CloudResource Resource(string id, string team) => new(
        id, id, "Microsoft.Compute/virtualMachines", ResourceCategory.Compute, "westeurope", "sub-1", $"{team}-production-rg", "D4s", new DateOnly(2025, 1, 1),
        new Dictionary<string, string>
        {
            ["owner"] = $"owner-{team}",
            ["team"] = team,
            ["environment"] = "production",
            ["cost-centre"] = team == "commerce" ? "cc-200" : "cc-300",
            ["application"] = "order-hub"
        });

    private static CostRecord Cost(string resourceId, decimal amount, DateOnly date, string meter) =>
        new($"{date:yyyy-MM}", resourceId, date, meter, "Compute", ResourceCategory.Compute, 10m, "hour", amount / 10m, amount, amount);
}

public sealed class FakeClock(DateOnly today) : IClock
{
    public DateOnly Today => today;
    public DateTimeOffset UtcNow => new(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}
