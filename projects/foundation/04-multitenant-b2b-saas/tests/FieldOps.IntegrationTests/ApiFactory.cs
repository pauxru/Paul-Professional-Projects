using System.Net.Http.Headers;
using System.Net.Http.Json;
using FieldOps.Api;
using FieldOps.Application;
using FieldOps.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FieldOps.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "FieldOps API";
}

public sealed class IntegrationClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
    public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
}

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public IntegrationClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<FieldOpsDbContext>>();
            services.RemoveAll<FieldOpsDbContext>();
            services.AddDbContext<FieldOpsDbContext>((sp, options) =>
                options.UseSqlite(_connection)
                    .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));
            services.RemoveAll<IClock>();
            services.AddSingleton(Clock);
            services.AddSingleton<IClock>(Clock);
        });
    }

    public async Task InitializeAsync()
    {
        _ = Services;
        await DemoData.SeedAsync(Services);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _connection.DisposeAsync();
        await base.DisposeAsync();
    }

    public async Task<HttpClient> CreateTenantClientAsync(
        string email = "owner@fieldops.demo",
        string tenant = "savanna-logistics",
        bool includeTenantHeader = true)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { email, tenantSlug = tenant });
        if (!response.IsSuccessStatusCode)
        {
            // EnsureSuccessStatusCode throws away the response body, which is where the
            // server just explained itself. Every integration test in this project funnels
            // through this helper, so discarding that body turns any server-side fault
            // into fifteen identical "500 Internal Server Error" stack traces that all
            // point here and none of which say why.
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"POST /api/v1/auth/token returned {(int)response.StatusCode} " +
                $"{response.StatusCode} for email='{email}' tenant='{tenant}'. Body: {body}");
        }
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>()
            ?? throw new InvalidOperationException("Token endpoint returned no payload.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);
        if (includeTenantHeader) client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private sealed record TokenResponse(string AccessToken);
}
