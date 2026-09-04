using ExampleBank.Ledger.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ExampleBank.Ledger.IntegrationTests;

/// <summary>
/// Boots the real API in-process (TestServer) against a throwaway file-backed SQLite database, so
/// integration tests exercise genuine SQL semantics — including the concurrency protocol — without
/// any external infrastructure. Dev-token minting is enabled so tests can obtain scoped JWTs.
/// </summary>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        AppContext.BaseDirectory, $"itest-{Guid.NewGuid():n}.db");

    public string ConnectionString => $"Data Source={_dbPath}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ledger"] = ConnectionString,
                ["Auth:EnableDevTokens"] = "true",
                ["Auth:SigningKey"] = "integration-test-signing-key-please-override-0123456789",
                ["Serilog:MinimumLevel:Default"] = "Warning",
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        // Create the schema and seed the chart of accounts exactly once, after the host is built.
        host.Services.InitializeLedgerDatabaseAsync().GetAwaiter().GetResult();
        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            TryDeleteDatabaseFiles();
        }
    }

    private void TryDeleteDatabaseFiles()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + "-journal" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup; a locked temp file is harmless.
            }
        }
    }
}
