using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace JobScheduler.IntegrationTests.Api;

/// <summary>
/// Boots the real API via <see cref="WebApplicationFactory{TEntryPoint}"/> against a temporary file
/// SQLite database, in the <c>Testing</c> environment (so the dev token endpoint is available), with
/// demo seeding and the in-process worker/leader loops disabled for deterministic request tests.
/// </summary>
public sealed class SchedulerApiFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _dbPath = Path.Combine(AppContext.BaseDirectory, $"apitest-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={_dbPath};Cache=Shared",
                ["Seed:DemoData"] = "false",
                ["Node:RunWorker"] = "false",
                ["Node:RunLeader"] = "false"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
            }
        }
    }
}
