using System.Net.Http.Headers;
using System.Text.Json;
using Healthcare.Application.Abstractions;
using Healthcare.Infrastructure.Adapters;
using Healthcare.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Healthcare.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero));
    public InMemorySmsReminderChannel Sms { get; } = new();
    public InMemoryEmailReminderChannel Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "healthcare-tests",
                ["Jwt:Audience"] = "healthcare-tests",
                ["Jwt:SigningKey"] = "test-only-secret-must-be-long-enough-0123456789ABCDEF"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.RemoveAll<InMemorySmsReminderChannel>();
            services.RemoveAll<InMemoryEmailReminderChannel>();
            services.AddSingleton(Sms);
            services.AddSingleton(Email);
            // The IReminderChannel registrations survive; we need to replace them via singletons that resolve to Sms/Email
            var toRemove = services.Where(d => d.ServiceType == typeof(IReminderChannel)).ToList();
            foreach (var d in toRemove) services.Remove(d);
            services.AddSingleton<IReminderChannel>(Sms);
            services.AddSingleton<IReminderChannel>(Email);
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public new async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await base.DisposeAsync();
    }

    public HttpClient CreateClientAs(string userId, string[] roles, Guid? clinicianId = null)
    {
        var client = CreateClient();
        var scope = Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<Healthcare.Api.Auth.TokenIssuer>();
        var token = issuer.Issue(userId, userId, roles, clinicianId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
