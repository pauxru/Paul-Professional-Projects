using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Northstar.Reliability.Infrastructure.Persistence;

namespace Northstar.Reliability.IntegrationTests.Fixtures;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ReliabilityDbContext>>();
            services.RemoveAll<ReliabilityDbContext>();
            services.AddDbContext<ReliabilityDbContext>(options => options.UseSqlite(connection));
        });
    }

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        using var scope = Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ReliabilityDbContext>();
        await database.Database.EnsureCreatedAsync();
    }

    public new async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await base.DisposeAsync();
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(params string[] scopes)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            subject = "integration.tester@northstar.invalid",
            scopes
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            document.RootElement.GetProperty("accessToken").GetString());
        return client;
    }
}
