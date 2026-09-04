using System.Data.Common;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Api.Startup;
using Contoso.Payments.Domain.Catalog;
using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Inventory;
using Contoso.Payments.Infrastructure.Payments;
using Contoso.Payments.Infrastructure.Persistence;

namespace Contoso.Payments.IntegrationTests.Fixture;

/// <summary>
/// WebApplicationFactory that hosts the API against a shared in-memory SQLite connection so
/// every scope in a single test observes the same database.  The connection is held open for
/// the fixture lifetime; disposing it drops the schema.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private SqliteConnection? _keepAlive;
    public string ConnectionString { get; } =
        $"DataSource=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";

    public DeterministicPaymentProviderSimulator Simulator =>
        Services.GetRequiredService<DeterministicPaymentProviderSimulator>();

    public string DevSigningKey => AuthSetup.DefaultSigningKey;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Hold one connection open for the fixture lifetime so the shared-cache in-memory DB
        // survives between per-request connections that the DbContext opens and closes.
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = ConnectionString,
                ["PaymentProvider:Mode"] = "Deterministic",
                ["PaymentProvider:WebhookSigningSecret"] = "test-webhook-secret-that-is-32-chars-long-!",
                ["PaymentProvider:WebhookTimestampToleranceSeconds"] = "300",
                ["PaymentProvider:SimulatedLatencyMs"] = "0",
                ["Outbox:PollIntervalMilliseconds"] = "3600000",
                ["Outbox:MaxAttempts"] = "3",
                ["Outbox:BaseBackoffMilliseconds"] = "10",
                ["Jwt:SigningKey"] = AuthSetup.DefaultSigningKey,
                ["Jwt:Issuer"] = "https://contoso-payments.local",
                ["Jwt:Audience"] = "contoso-payments-api",
                ["Logging:LogLevel:Default"] = "Warning"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Swap in a fresh DbContextOptions bound to the shared-cache connection string —
            // each DbContext opens/closes its own connection, all seeing the same DB via cache.
            var dbDesc = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (dbDesc is not null) services.Remove(dbDesc);
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(ConnectionString));
        });

        var host = base.CreateHost(builder);

        // Create schema + seed a minimal catalog for the test.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        SeedTestData(db);
        return host;
    }

    private static void SeedTestData(AppDbContext db)
    {
        if (db.Products.Any()) return;
        var p1 = new Product(Guid.NewGuid(), "TEST-USD-1", "Test USD Widget", Money.Of(10.00m, "USD"));
        var p2 = new Product(Guid.NewGuid(), "TEST-USD-2", "Limited Stock Widget", Money.Of(5.00m, "USD"));
        var p3 = new Product(Guid.NewGuid(), "TEST-KES-1", "Test KES Item", Money.Of(500m, "KES"));
        db.Products.AddRange(p1, p2, p3);
        db.Inventory.AddRange(
            new InventoryItem(p1.Id, p1.Sku, 100),
            new InventoryItem(p2.Id, p2.Sku, 5),
            new InventoryItem(p3.Id, p3.Sku, 50));
        db.SaveChanges();
    }

    public string IssueToken(params string[] scopes)
    {
        var issuer = Services.GetRequiredService<DevTokenIssuer>();
        return issuer.Issue("test-user", scopes, TimeSpan.FromMinutes(30));
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        _keepAlive?.Dispose();
    }

    public Task InitializeAsync() => Task.CompletedTask;
}
