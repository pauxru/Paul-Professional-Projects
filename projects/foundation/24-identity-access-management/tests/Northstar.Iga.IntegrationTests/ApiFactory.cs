using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Northstar.Iga.Application;
using Northstar.Iga.Infrastructure;

namespace Northstar.Iga.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "Northstar IGA API";
}

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _ = Services;
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
            services.RemoveAll<DbContextOptions<IgaDbContext>>();
            services.AddDbContext<IgaDbContext>(options => options.UseSqlite(_connection));
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.RemoveAll<IHostedService>();
            services.PostConfigure<ProvisioningOptions>(options => options.RetryDelayMilliseconds = 0);
        });
    }

    public async Task ResetAsync()
    {
        Clock.Set(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        scope.ServiceProvider.GetRequiredService<ISimulatedConnectorControl>().Reset();
    }
}

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
    public void Set(DateTimeOffset value) => UtcNow = value;
}
