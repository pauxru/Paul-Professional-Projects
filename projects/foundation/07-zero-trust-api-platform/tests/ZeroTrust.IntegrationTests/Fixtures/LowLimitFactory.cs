using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Infrastructure;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.Infrastructure.Security;
using ZeroTrust.Infrastructure.Time;

namespace ZeroTrust.IntegrationTests.Fixtures;

/// <summary>
/// A dedicated factory for tests that need to trigger the rate limiter. Configures a very
/// small permits-per-minute for the partner surface and a fresh per-class in-memory database.
/// </summary>
public sealed class LowLimitFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public FakeClock Clock { get; } = new(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        HmacWebhookVerifier.ClearReplayCacheForTests();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZeroTrustDbContext>();
        await Seeder.SeedAsync(db, Clock, CancellationToken.None);

        foreach (var p in db.Partners) p.SetRateLimit(6);
        await db.SaveChangesAsync();
    }

    public new async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:UseRs256"] = "true",
                ["Jwt:Issuer"] = "https://zero-trust-demo.localhost",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:AdminAccessTokenMinutes"] = "5",
                ["Jwt:RefreshTokenDays"] = "14",
                ["Jwt:HsSigningKey"] = "test-only-signing-key-not-a-real-secret-0123456789",
                ["Jwt:DefaultAudience"] = "ntsf-customer-api",
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = "Data Source=:memory:",
                ["RateLimiting:PartnerPermitsPerMinute"] = "6",
                ["RateLimiting:AdminPermitsPerMinute"] = "100000",
                ["RateLimiting:UserPermitsPerMinute"] = "100000",
                ["RateLimiting:PublicPermitsPerMinute"] = "100000",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ZeroTrustDbContext>>();
            services.RemoveAll<ZeroTrustDbContext>();
            services.AddDbContext<ZeroTrustDbContext>(o => o.UseSqlite(_connection));

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }
}
