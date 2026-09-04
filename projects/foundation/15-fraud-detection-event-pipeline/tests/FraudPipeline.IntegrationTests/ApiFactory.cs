using System.Data.Common;
using FraudPipeline.Api.Options;
using FraudPipeline.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FraudPipeline.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public ApiFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // NB: not "Testing" so startup seeding runs and gives us a live+shadow ruleset.
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "fraud-pipeline-tests",
                ["Jwt:Audience"] = "fraud-pipeline-tests",
                ["Jwt:SigningKey"] = "dev-only-signing-key-with-plenty-of-length-for-tests-0123456789",
                ["Jwt:AccessTokenLifetimeMinutes"] = "5",
                ["Ingestion:PartitionCount"] = "2",
                ["Scoring:LatencyBudgetMs"] = "500",
                ["Scoring:EnableShadow"] = "false",
                ["RateLimit:RequestsPerMinute"] = "10000",
                ["RateLimit:BurstSize"] = "1000",
                ["CaseManagement:AlertThresholdScore"] = "500",
                ["Logging:LogLevel:Default"] = "Warning"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove the file-based SQLite registration and use our in-memory one.
            var toRemove = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<FraudDbContext>) ||
                d.ServiceType == typeof(DbConnection) ||
                d.ServiceType == typeof(FraudDbContext)).ToList();
            foreach (var d in toRemove) services.Remove(d);

            services.AddSingleton<DbConnection>(_connection);
            services.AddDbContext<FraudDbContext>((sp, o) =>
            {
                o.UseSqlite(sp.GetRequiredService<DbConnection>());
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
