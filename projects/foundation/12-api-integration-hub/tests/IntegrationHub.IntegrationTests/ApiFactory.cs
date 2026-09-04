using IntegrationHub.Api;
using IntegrationHub.Infrastructure;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntegrationHub.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<ApiMarker>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _secretPath = Path.Combine(
        AppContext.BaseDirectory,
        "test-artifacts",
        $"api-secrets-{Guid.NewGuid():N}.enc");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_secretPath)!);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:FilePath"] = _secretPath,
                ["Secrets:MasterKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Database:ConnectionString"] = "Data Source=:memory:"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<IntegrationHubDbContext>>();
            services.AddDbContext<IntegrationHubDbContext>(options => options.UseSqlite(_connection));
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
        if (File.Exists(_secretPath))
        {
            File.Delete(_secretPath);
        }
    }

    public async Task<string> GetTokenAsync(params string[] scopes)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            clientId = "demo-client",
            clientSecret = "dev-only-client-secret",
            scopes
        });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return json["access_token"]!.GetValue<string>();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
