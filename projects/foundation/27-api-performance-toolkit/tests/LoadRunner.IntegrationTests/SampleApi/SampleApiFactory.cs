using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SampleApi.Data;

namespace LoadRunner.IntegrationTests.SampleApi;

public sealed class SampleApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<CatalogDbContext>));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddDbContext<CatalogDbContext>(o => o.UseSqlite(_connection));
        });
    }

    public Task InitializeAsync() => InitializeInternalAsync();
    private async Task InitializeInternalAsync()
    {
        _connection.Open();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.EnsureCreatedAsync();
        await Seed.EnsureAsync(db, productCount: 100);
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeConnectionAsync();
    private async Task DisposeConnectionAsync()
    {
        try { await _connection.DisposeAsync(); } catch { }
        await base.DisposeAsync();
    }
}
