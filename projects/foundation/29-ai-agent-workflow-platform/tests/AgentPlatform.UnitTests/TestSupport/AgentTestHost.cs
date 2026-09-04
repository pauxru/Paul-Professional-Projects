using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Approvals;
using AgentPlatform.Application.Engine;
using AgentPlatform.Domain.Approvals;
using AgentPlatform.Domain.Budgets;
using AgentPlatform.Domain.Runs;
using AgentPlatform.Domain.Tracing;
using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.UnitTests.TestSupport;

/// <summary>
/// Spins up the full platform composition root over an in-memory SQLite database (a single shared
/// connection, so state survives across scopes and thus across a simulated crash). Backoff delays
/// are instant. The deterministic mock model is the default; a specific model or fault injector can
/// be supplied. Each helper opens its own DI scope, mirroring how the API handles a request.
/// </summary>
public sealed class AgentTestHost : IDisposable
{
    private readonly SqliteConnection _connection;

    public ServiceProvider Services { get; }

    public AgentTestHost(Action<IServiceCollection>? overrides = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatform(config, options => options.UseSqlite(_connection));
        services.AddSingleton<IDelayStrategy, NoDelayStrategy>();
        overrides?.Invoke(services);

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Seed();
    }

    private void Seed()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        db.Database.EnsureCreated();
        DatabaseSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public static AgentCaller Caller(params string[] scopes) =>
        new("tester", "tenant-test", new HashSet<string>(scopes.Length == 0 ? new[] { "agents:run", "agents:approve" } : scopes, StringComparer.Ordinal));

    public async Task<RunResult> StartAsync(string workflow, object inputs, string[]? scopes = null,
        string? idempotencyKey = null, int? version = null, BudgetLimits? budget = null, CancellationToken ct = default)
    {
        using var scope = Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
        var command = new StartRunCommand
        {
            WorkflowName = workflow,
            Version = version,
            Inputs = ToInputs(inputs),
            IdempotencyKey = idempotencyKey,
            CorrelationId = "test-correlation",
            BudgetOverride = budget,
        };
        return await engine.StartRunAsync(Caller(scopes ?? new[] { "agents:run", "agents:approve" }), command, ct);
    }

    public async Task<RunResult> ResumeAsync(string runId, CancellationToken ct = default)
    {
        using var scope = Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
        return await engine.ResumeRunAsync(runId, ct);
    }

    public async Task<WorkflowRun?> GetRunAsync(string runId)
    {
        using var scope = Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunStore>();
        return await runs.GetAsync(runId, default);
    }

    public async Task<IReadOnlyList<TraceEvent>> TraceAsync(string runId)
    {
        using var scope = Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunStore>();
        return await runs.GetTraceAsync(runId, default);
    }

    public async Task<ApprovalTask?> PendingApprovalAsync(string runId)
    {
        using var scope = Services.CreateScope();
        var approvals = scope.ServiceProvider.GetRequiredService<IApprovalStore>();
        return await approvals.GetPendingForRunAsync(runId, default);
    }

    public async Task<ApprovalOutcome> DecideAsync(string approvalId, ApprovalDecision decision,
        string[]? scopes = null, string? modifiedArgumentsJson = null)
    {
        using var scope = Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ApprovalService>();
        var command = new ApprovalCommand(approvalId, decision, "test-decision", modifiedArgumentsJson);
        return await service.DecideAsync(Caller(scopes ?? new[] { "agents:run", "agents:approve" }), command, default);
    }

    public T Resolve<T>() where T : notnull
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    public async Task<int> CountAsync<TEntity>() where TEntity : class
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        return await db.Set<TEntity>().CountAsync();
    }

    public static IReadOnlyDictionary<string, JsonNode?> ToInputs(object inputs)
    {
        var node = JsonSerializer.SerializeToNode(inputs) as JsonObject ?? new JsonObject();
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in node) result[key] = value?.DeepClone();
        return result;
    }

    public void Dispose()
    {
        Services.Dispose();
        _connection.Dispose();
    }
}
