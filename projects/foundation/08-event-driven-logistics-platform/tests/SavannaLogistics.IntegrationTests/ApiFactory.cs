using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SavannaLogistics.Infrastructure;
using System.Net.Http.Json;
using System.Text.Json;

namespace SavannaLogistics.IntegrationTests;

public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // A private, named, shared-cache in-memory database. Using a connection *string* (rather than a
    // single SqliteConnection instance) lets EF open one connection per DbContext, exactly as the
    // production configuration does. SqliteConnection is not thread-safe, so sharing one instance
    // between the telemetry background service and the HTTP pipeline caused intermittent
    // SQLITE_BUSY failures. "Default Timeout" makes contending readers wait rather than throw.
    private readonly string _connectionString =
        $"Data Source=savanna-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=30";

    private readonly SqliteConnection _keepAlive;
    private readonly int _allowedLatenessSeconds;

    public ApiFactory() : this(0)
    {
    }

    protected ApiFactory(int allowedLatenessSeconds)
    {
        _allowedLatenessSeconds = allowedLatenessSeconds;

        // A shared-cache in-memory database is destroyed when its last connection closes, so hold
        // one open for the lifetime of the factory to keep the schema and data alive between requests.
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:Partitions"] = "1",
                ["Telemetry:ChannelCapacityPerPartition"] = "512",
                ["Telemetry:AllowedLatenessSeconds"] = _allowedLatenessSeconds.ToString(),
                ["Telemetry:OfflineAfterSeconds"] = "3600",
                ["Alerts:GeofenceEnterDwellSeconds"] = "0",
                ["Alerts:GeofenceExitDwellSeconds"] = "0",
                ["Alerts:SuppressionWindowSeconds"] = "120",
                ["Observability:ConsoleExporter"] = "false"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<LogisticsDbContext>>();
            services.AddDbContext<LogisticsDbContext>(options => options.UseSqlite(_connectionString));
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    public async Task<HttpClient> CreateAuthorizedClientAsync(string profile = "operator")
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { clientId = profile });
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = document.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public sealed class BufferedApiFactory : ApiFactory
{
    public BufferedApiFactory() : base(300)
    {
    }
}

[CollectionDefinition("api", DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
