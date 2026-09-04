using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LoanOrigination.Api.Configuration;
using LoanOrigination.Application.Ports;
using LoanOrigination.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LoanOrigination.IntegrationTests.Api;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<LoanDbContext>>();
            services.RemoveAll<LoanDbContext>();
            services.AddDbContext<LoanDbContext>(options => options.UseSqlite(_connection));
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        using var scope = Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LoanDbContext>();
        await database.Database.EnsureCreatedAsync();
        await DemoDataSeeder.SeedAsync(
            scope.ServiceProvider.GetRequiredService<ILoanRepository>(),
            new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        _connection.Dispose();
        Dispose();
        return Task.CompletedTask;
    }

    public async Task<HttpClient> CreateAuthorizedClientAsync(params string[] scopes)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            subject = "integration-tester",
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
