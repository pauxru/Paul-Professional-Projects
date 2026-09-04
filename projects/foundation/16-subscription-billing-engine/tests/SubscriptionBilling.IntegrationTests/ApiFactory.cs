using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubscriptionBilling.Application;
using SubscriptionBilling.Infrastructure;

namespace SubscriptionBilling.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TestClock Clock { get; } =
        new(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync() => await _connection.OpenAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<BillingDbContext>>();
            services.RemoveAll<BillingDbContext>();
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.AddDbContext<BillingDbContext>(
                options => options.UseSqlite(_connection));
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

public sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Set(DateTimeOffset value) => UtcNow = value;
    public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
}
