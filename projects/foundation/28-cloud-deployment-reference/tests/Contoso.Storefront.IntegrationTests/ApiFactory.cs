using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Domain;
using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contoso.Storefront.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly Guid ProductId =
        Guid.Parse("d3e44993-cdda-4b18-a4fe-652ad6123456");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ApiFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<StorefrontDbContext>>();
            services.RemoveAll<StorefrontDbContext>();
            services.AddSingleton(_connection);
            services.AddDbContext<StorefrontDbContext>(
                options => options.UseSqlite(_connection));

            services.RemoveAll<IMigrationStatus>();
            services.AddSingleton<TestMigrationStatus>();
            services.AddSingleton<IMigrationStatus>(
                provider => provider.GetRequiredService<TestMigrationStatus>());

            var options = new DbContextOptionsBuilder<StorefrontDbContext>()
                .UseSqlite(_connection)
                .Options;
            using var database = new StorefrontDbContext(options);
            database.Database.Migrate();
            if (!database.Products.Any())
            {
                database.Products.Add(
                    new Product(
                        ProductId,
                        "TEST-COFFEE-1",
                        "Integration Test Coffee",
                        "Fictional integration-test product.",
                        new Money(18.50m, "USD"),
                        new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero)));
                database.SaveChanges();
            }
        });
    }

}

public sealed class TestMigrationStatus : IMigrationStatus
{
    public bool IsCurrent { get; set; } = true;

    public Task<bool> AreAllMigrationsAppliedAsync(CancellationToken cancellationToken) =>
        Task.FromResult(IsCurrent);
}
