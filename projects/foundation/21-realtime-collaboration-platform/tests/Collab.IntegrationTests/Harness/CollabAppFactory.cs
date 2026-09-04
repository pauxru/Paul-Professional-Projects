using System.Collections.Generic;
using Collab.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Collab.IntegrationTests.Harness;

/// <summary>
/// Boots the real API in-process (TestServer) against an isolated, file-backed SQLite database so the
/// suite needs zero external infrastructure. Each factory instance gets its own database file and
/// deletes it on dispose. Demo seeding is disabled — every test provisions exactly the data it needs.
/// </summary>
public class CollabAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath =
        Path.Combine(AppContext.BaseDirectory, $"collab-it-{Guid.NewGuid():N}.db");

    /// <summary>Per-factory configuration overrides (rate limits, throttles, etc.).</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> Settings =>
        Array.Empty<KeyValuePair<string, string?>>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
        builder.UseSetting("Seed:Demo", "false");
        foreach (var kv in Settings)
            builder.UseSetting(kv.Key, kv.Value);
    }

    /// <summary>Run an action inside a fresh DI scope (for direct service/data setup in tests).</summary>
    public async Task InScopeAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider);
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>Factory variant with tight hub rate limits, used to exercise abuse disconnection.</summary>
public sealed class RateLimitedAppFactory : CollabAppFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> Settings => new Dictionary<string, string?>
    {
        ["Collaboration:OperationsPerSecondPerConnection"] = "1",
        ["Collaboration:OperationBurst"] = "3",
        ["Collaboration:MaxViolationsBeforeDisconnect"] = "3"
    };
}

/// <summary>Factory variant that snapshots aggressively so log-replay/checkpoint paths are covered.</summary>
public sealed class FrequentSnapshotAppFactory : CollabAppFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> Settings => new Dictionary<string, string?>
    {
        ["Collaboration:SnapshotEveryNOperations"] = "5"
    };
}
