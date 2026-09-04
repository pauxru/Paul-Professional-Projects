using JobScheduler.Application.Abstractions;
using JobScheduler.Application.Options;
using JobScheduler.Domain;
using JobScheduler.Domain.Entities;
using JobScheduler.Infrastructure;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JobScheduler.IntegrationTests.TestSupport;

/// <summary>
/// Boots the real infrastructure (stores, EF Core, SQLite) against a temporary FILE database so
/// coordination operations experience genuine multi-connection writer contention — the only way to
/// honestly prove the claim/lease/fencing protocol. Background hosted services are intentionally
/// NOT started; tests drive the stores directly for deterministic control with a <see cref="FakeClock"/>.
/// </summary>
public sealed class SchedulerTestHost : IAsyncDisposable
{
    private readonly string _dbPath;

    public ServiceProvider Provider { get; }
    public FakeClock Clock { get; }

    public SchedulerTestHost(Action<EngineOptions>? configureEngine = null, DateTimeOffset? start = null)
    {
        _dbPath = Path.Combine(AppContext.BaseDirectory, $"itest-{Guid.NewGuid():N}.db");
        Clock = new FakeClock(start ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IClock>(Clock); // registered before core so TryAddSingleton keeps ours
        services.AddSchedulerPersistence(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSchedulerCore(config);
        if (configureEngine is not null)
        {
            services.Configure(configureEngine);
        }

        Provider = services.BuildServiceProvider();
    }

    public async Task InitializeAsync()
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public IServiceScope CreateScope() => Provider.CreateScope();

    public EngineOptions Engine =>
        Provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<EngineOptions>>().Value;

    /// <summary>Runs <paramref name="work"/> inside a fresh DI scope with its own DbContext/connection.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Provider.CreateScope();
        return await work(scope.ServiceProvider);
    }

    public async Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = Provider.CreateScope();
        await work(scope.ServiceProvider);
    }

    /// <summary>Persists a job definition using the current fake time.</summary>
    public Task<JobDefinition> AddDefinitionAsync(
        string name = "job",
        string handler = "report-generator",
        bool singleton = false,
        int concurrencyLimit = 1,
        int priority = 0,
        int maxAttempts = 3,
        int timeoutSeconds = 300,
        string payloadJson = "{}",
        IEnumerable<string>? tags = null)
        => InScopeAsync(async sp =>
        {
            var store = sp.GetRequiredService<Application.Abstractions.IJobDefinitionStore>();
            var def = JobDefinition.Create(name, handler, Clock.UtcNow,
                payloadJson: payloadJson,
                singleton: singleton, concurrencyLimit: concurrencyLimit, priority: priority,
                maxAttempts: maxAttempts, timeoutSeconds: timeoutSeconds, tags: tags);
            await store.AddAsync(def, default);
            await store.SaveChangesAsync(default);
            return def;
        });

    /// <summary>Persists a Pending run for a definition, scheduled one second in the past (i.e. due).</summary>
    public Task<Guid> AddDueRunAsync(JobDefinition def, DateTimeOffset? scheduledAt = null)
        => InScopeAsync(async sp =>
        {
            var runs = sp.GetRequiredService<Application.Abstractions.IJobRunStore>();
            var when = scheduledAt ?? Clock.UtcNow.AddSeconds(-1);
            var run = JobRun.Create(def, when, Clock.UtcNow, $"idem-{Guid.NewGuid():N}", Guid.NewGuid().ToString("N"), "manual");
            await runs.AddAsync(run, default);
            await runs.SaveChangesAsync(default);
            return run.Id;
        });

    public Task<JobRun?> GetRunAsync(Guid runId)
        => InScopeAsync(sp => sp.GetRequiredService<Application.Abstractions.IJobRunStore>().GetAsync(runId, default));

    /// <summary>
    /// Claims a due run and runs it to a terminal outcome via the real <c>RunExecutor</c>. Each call
    /// is a single attempt, so tests can loop (advancing the clock) to exercise the retry lifecycle.
    /// </summary>
    public async Task ClaimAndExecuteAsync(Guid runId, string nodeId = "node-A", CancellationToken ct = default)
    {
        var (token, fencing) = await InScopeAsync(async sp =>
        {
            var runs = sp.GetRequiredService<Application.Abstractions.IJobRunStore>();
            var t = Guid.NewGuid();
            var claim = await runs.TryClaimAsync(runId, nodeId, t, Clock.UtcNow, Engine.Lease, singleton: false, ct);
            if (!claim.Claimed)
            {
                throw new InvalidOperationException($"Run {runId} could not be claimed for execution.");
            }
            return (t, claim.FencingToken);
        });

        using var scope = Provider.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<Infrastructure.Engine.RunExecutor>();
        await executor.ExecuteAsync(runId, token, fencing, nodeId, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort cleanup */ }
        }
    }
}
