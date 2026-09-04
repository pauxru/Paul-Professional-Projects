using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReconEngine.Infrastructure.Persistence;

namespace ReconEngine.IntegrationTests.Support;

/// <summary>
/// Boots the real API in-process with <see cref="WebApplicationFactory{TEntryPoint}"/> against a private
/// SQLite in-memory database. The single open connection is kept alive for the lifetime of the factory so
/// the schema (created by the app's own <c>DatabaseInitializer</c> on start-up) and the seeded default
/// ruleset survive across request scopes. Each test constructs its own factory, so every test gets an
/// isolated database — no shared state, no external infrastructure.
/// </summary>
public sealed class ReconApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;

    public ReconApiFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open(); // keep open: a :memory: db is destroyed when the last connection closes
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Replace the file-based SQLite registration with the shared in-memory connection.
            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            services.RemoveAll(typeof(DbContextOptions));
            services.RemoveAll(typeof(AppDbContext));

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        });
    }

    /// <summary>Mint a JWT carrying the requested scopes and return a client that sends it as a Bearer token.</summary>
    public async Task<HttpClient> CreateAuthedClientAsync(params string[] scopes)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { subject = "test-user", scopes });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    public IServiceScope CreateScope() => Services.CreateScope();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _connection.Dispose();
    }

    private sealed record TokenDto(string AccessToken, DateTime ExpiresAtUtc, string TokenType);
}
