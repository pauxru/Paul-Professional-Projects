using Lab.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lab.IntegrationTests.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<LogisticsDbContext>>();
            services.RemoveAll<LogisticsDbContext>();
            services.AddDbContext<LogisticsDbContext>(options => options.UseSqlite(_connection));
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
        await NorthstarSeeder.SeedAsync(
            dbContext,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            CancellationToken.None);
    }

    public new async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        Dispose();
    }
}
