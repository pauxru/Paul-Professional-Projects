using Idp.Api.Auth;
using Idp.Application.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Idp.IntegrationTests;

/// <summary>
/// Boots the real API in-memory against a throwaway file-backed SQLite database, seeded with the
/// deterministic synthetic corpus. Tokens are minted with the production <see cref="TokenFactory"/>
/// so authentication/authorization is exercised for real.
/// </summary>
public sealed class IdpApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath =
        Path.Combine(AppContext.BaseDirectory, $"it-{Guid.NewGuid():N}.db");
    private readonly string _artifacts =
        Path.Combine(AppContext.BaseDirectory, $"it-artifacts-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed"] = "true",
                ["Database:ConnectionString"] = $"Data Source={_dbPath}",
                ["Storage:RootPath"] = Path.Combine(_artifacts, "object-store"),
                ["Export:OutboxPath"] = Path.Combine(_artifacts, "outbox"),
                ["Export:DeadLetterPath"] = Path.Combine(_artifacts, "deadletter"),
                ["Ingestion:DropFolderPath"] = Path.Combine(_artifacts, "drop"),
                ["Ingestion:DropFolderEnabled"] = "false"
            });
        });
    }

    /// <summary>Mint a bearer token; with no permissions supplied, grants all four policies.</summary>
    public string MintToken(params string[] permissions)
    {
        var options = Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var perms = permissions.Length == 0 ? Permissions.All : permissions;
        return TokenFactory.Create(options, "it-user", perms);
    }

    public HttpClient AuthedClient(params string[] permissions)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MintToken(permissions));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_artifacts)) Directory.Delete(_artifacts, true); }
        catch { /* best effort */ }
    }
}
