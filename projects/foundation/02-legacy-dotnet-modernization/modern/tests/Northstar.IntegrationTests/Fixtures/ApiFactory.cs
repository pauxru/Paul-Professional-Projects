using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Northstar.Infrastructure.Persistence;
using Northstar.Infrastructure.Time;

namespace Northstar.IntegrationTests.Fixtures;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<NorthstarDbContext>>();
            services.AddDbContext<NorthstarDbContext>(options => options.UseSqlite(_connection));
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NorthstarDbContext>();
        await context.Database.EnsureCreatedAsync();
        await DemoDataSeeder.SeedAsync(context, new SystemClock(), CancellationToken.None);
    }

    public new async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        Dispose();
    }
}
