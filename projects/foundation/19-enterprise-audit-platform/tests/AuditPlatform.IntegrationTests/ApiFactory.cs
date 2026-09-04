using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuditPlatform.Api.Options;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Events;
using AuditPlatform.Application.Retention;
using AuditPlatform.Domain.Time;
using AuditPlatform.Infrastructure.Persistence;
using AuditPlatform.Infrastructure.Persistence.Interceptors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AuditPlatform.IntegrationTests;

/// <summary>
/// Shared factory for API-level tests. Each factory instance gets its own SQLite in-memory
/// database (Data Source=file:...?mode=memory&cache=shared) whose connection is kept open for
/// the lifetime of the factory. Between test classes we swap the shared name so isolation is
/// preserved. Overrides the clock and DI graph so tests can rewind and advance time.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
    public string DatabaseName { get; }

    public ApiFactory()
    {
        DatabaseName = "audit-tests-" + Guid.NewGuid().ToString("n");
        _connection = new SqliteConnection($"Data Source={DatabaseName};Mode=Memory;Cache=Shared");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={DatabaseName};Mode=Memory;Cache=Shared",
                ["Jwt:Issuer"] = "audit-tests",
                ["Jwt:Audience"] = "audit-tests-clients",
                ["Jwt:SigningKey"] = "test-key-not-a-real-secret-change-me-0123456789-abcdef",
                ["Signing:KeyId"] = "test-key-1",
                ["Seed:Enabled"] = "false"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }

    public HttpClient CreateAuthClient(string scopes = "audit:read audit:write audit:verify audit:admin audit:export", string tenantId = "test-tenant", string clearance = "investigator")
    {
        var client = CreateClient();
        var token = MintToken(scopes, tenantId, clearance);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public string MintToken(string scopes, string tenantId, string clearance = "investigator")
    {
        using var scope = Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtOptions>();
        return Api.Auth.DevTokenIssuer.Issue(jwt, "test-user", tenantId,
            scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), clearance);
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
