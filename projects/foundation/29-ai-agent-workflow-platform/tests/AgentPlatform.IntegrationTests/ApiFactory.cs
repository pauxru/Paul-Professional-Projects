using AgentPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.IntegrationTests;

/// <summary>
/// Hosts the real API in-process for integration tests. The default file-backed SQLite database is
/// swapped for a private in-memory SQLite connection kept open for the lifetime of the factory, so
/// the schema and deterministic seed created at startup survive across requests and each test class
/// gets an isolated database. The "Testing" environment disables OpenTelemetry, the rate limiter and
/// request logging so the tests are fully deterministic and offline.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // Remove the app's file-backed SQLite registration (the options descriptor and, on EF
            // Core 9+, the options-configuration descriptor) and point the context at a private
            // in-memory connection kept open for the factory's lifetime.
            foreach (var descriptor in services.Where(d =>
                         d.ServiceType == typeof(DbContextOptions<AgentDbContext>) ||
                         (d.ServiceType.IsGenericType &&
                          d.ServiceType.GetGenericTypeDefinition().Name.StartsWith("IDbContextOptionsConfiguration")))
                     .ToList())
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AgentDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
