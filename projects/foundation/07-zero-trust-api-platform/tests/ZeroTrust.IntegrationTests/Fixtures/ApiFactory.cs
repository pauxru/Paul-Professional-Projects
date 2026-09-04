using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure;
using ZeroTrust.Infrastructure.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.Infrastructure.Security;
using ZeroTrust.Infrastructure.Time;

namespace ZeroTrust.IntegrationTests.Fixtures;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public FakeClock Clock { get; } = new(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        HmacWebhookVerifier.ClearReplayCacheForTests();

        // Perform seeding once here so tests get a consistent starting state.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZeroTrustDbContext>();
        await Seeder.SeedAsync(db, Clock, CancellationToken.None);

        // Raise the per-partner rate limits so shared-client-per-class tests don't
        // exhaust the bucket. A dedicated rate limit test class lowers the value.
        foreach (var p in db.Partners)
        {
            p.SetRateLimit(100_000);
        }
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
                ["RateLimiting:PartnerPermitsPerMinute"] = "100000",
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

// Test tool for issuing tokens in a controlled way (audience, scopes, etc)
public static class TestTokens
{
    public static async Task<string> IssueUserAccess(this ApiFactory f, string subject, string password,
        string audience, string? scope = null, string? amr = null, string? acr = null)
    {
        using var scope1 = f.Services.CreateScope();
        var issuer = scope1.ServiceProvider.GetRequiredService<ITokenIssuer>();
        var result = await issuer.IssueAsync(new TokenRequest("password", subject, password, null, null,
            scope, audience, amr, acr, null), CancellationToken.None);
        return result.AccessToken;
    }

    public static async Task<TokenResponse> IssueUserFull(this ApiFactory f, string subject, string password,
        string audience, string? scope = null, string? amr = null, string? acr = null)
    {
        using var s = f.Services.CreateScope();
        var issuer = s.ServiceProvider.GetRequiredService<ITokenIssuer>();
        return await issuer.IssueAsync(new TokenRequest("password", subject, password, null, null,
            scope, audience, amr, acr, null), CancellationToken.None);
    }

    public static async Task<TokenResponse> IssueClient(this ApiFactory f, string clientId, string secret,
        string audience, string? scope = null)
    {
        using var s = f.Services.CreateScope();
        var issuer = s.ServiceProvider.GetRequiredService<ITokenIssuer>();
        return await issuer.IssueAsync(new TokenRequest("client_credentials", null, null, clientId, secret,
            scope, audience, null, null, null), CancellationToken.None);
    }

    public static async Task<TokenResponse> IssueService(this ApiFactory f, string spiffeId, string audience,
        string? scope = null)
    {
        using var s = f.Services.CreateScope();
        var issuer = s.ServiceProvider.GetRequiredService<ITokenIssuer>();
        return await issuer.IssueAsync(new TokenRequest("service_account", spiffeId, null, null, null,
            scope, audience, null, null, null), CancellationToken.None);
    }
}
