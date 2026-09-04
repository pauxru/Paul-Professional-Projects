using System.Net.Http.Json;
using System.Text.Json;
using FeatureFlags.Application;
using FeatureFlags.Domain;
using FeatureFlags.Infrastructure;
using FeatureFlags.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FeatureFlags.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public Task InitializeAsync()
    {
        _connection.Open();
        return Task.CompletedTask;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<FeatureFlagDbContext>>();
            services.RemoveAll<FeatureFlagDbContext>();
            services.AddDbContext<FeatureFlagDbContext>(options => options.UseSqlite(_connection));
        });
    }

    public async Task SeedAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FeatureFlagDbContext>();
        await db.Database.EnsureCreatedAsync();
        if (await db.Projects.AnyAsync(project => project.Key == "acme")) return;
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var project = new ProjectEntity { Id = Guid.NewGuid(), Key = "acme", Name = "Acme Manufacturing (fictional)", CreatedAt = now };
        db.Projects.Add(project);
        foreach (var environment in DemoConfiguration.Create("acme", now))
        {
            db.Environments.Add(new EnvironmentEntity
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, Key = environment.Key, Name = environment.Name,
                ServerSdkKey = environment.ServerSdkKey, ClientSdkKey = environment.ClientSdkKey, Version = environment.Configuration.Version,
                ConfigurationJson = JsonSerializer.Serialize(environment.Configuration, FeatureFlagJson.Options), UpdatedAt = now
            });
        }
        await db.SaveChangesAsync();
    }

    public async Task<HttpClient> CreateAuthorizedClientAsync(string actor = "test-admin", params string[] scopes)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { actor, scopes = scopes.Length == 0 ? new[] { "flags:read", "flags:write", "flags:approve" } : scopes });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", document.RootElement.GetProperty("accessToken").GetString());
        return client;
    }
}
