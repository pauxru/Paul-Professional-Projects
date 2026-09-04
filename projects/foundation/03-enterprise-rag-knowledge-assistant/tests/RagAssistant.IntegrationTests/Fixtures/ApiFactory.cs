using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RagAssistant.Infrastructure.Persistence;
using RagAssistant.Infrastructure.Seeding;

namespace RagAssistant.IntegrationTests.Fixtures;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = "DataSource=:memory:",
                ["Jwt:Issuer"] = "rag-assistant-tests",
                ["Jwt:Audience"] = "rag-assistant-tests-clients",
                ["Jwt:SigningKey"] = "dev-only-tests-signing-key-0123456789ABCDEF",
                ["Ai:Provider"] = "Local",
                ["Ai:EmbeddingDimensions"] = "256",
                ["Budget:DailyLimitUsd"] = "1000",
                ["Budget:DailyRequestLimit"] = "10000",
                ["Rag:MinRetrievalScore"] = "0.02",
                ["Rag:MinSupportForSentence"] = "0.15",
                ["Rag:MinSupportRatio"] = "0.2",
                ["Rag:TopK"] = "4",
                ["RateLimiting:PermitsPerMinute"] = "1000",
            });
        });
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextFactory<RagDbContext>>();
            services.RemoveAll<RagDbContext>();
            services.RemoveAll<DbContextOptions<RagDbContext>>();

            services.AddDbContextFactory<RagDbContext>(options =>
            {
                options.UseSqlite(_connection);
            });
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<RagDbContext>>().CreateDbContext());
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        var scope = Services.CreateAsyncScope();
        await using (scope)
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RagDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            var seeder = scope.ServiceProvider.GetRequiredService<CorpusSeeder>();
            await seeder.SeedAsync(CancellationToken.None);
        }
    }

    public new async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await base.DisposeAsync();
    }
}
