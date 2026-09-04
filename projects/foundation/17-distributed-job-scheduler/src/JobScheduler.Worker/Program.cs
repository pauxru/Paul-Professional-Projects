using JobScheduler.Application.Options;
using JobScheduler.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

// A worker node identity can be supplied as `--node-id w1 [--tags etl,reports]`.
string? nodeId = GetArg(args, "--node-id") ?? Environment.GetEnvironmentVariable("NODE_ID");
string? tags = GetArg(args, "--tags") ?? Environment.GetEnvironmentVariable("NODE_TAGS");

var overrides = new Dictionary<string, string?>
{
    ["Node:RunWorker"] = "true",
    ["Node:RunLeader"] = "true"
};
if (!string.IsNullOrWhiteSpace(nodeId))
{
    overrides["Node:NodeId"] = nodeId;
}
if (!string.IsNullOrWhiteSpace(tags))
{
    var parts = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    for (int i = 0; i < parts.Length; i++)
    {
        overrides[$"Node:Tags:{i}"] = parts[i];
    }
}
builder.Configuration.AddInMemoryCollection(overrides);

var connectionString = builder.Configuration.GetValue<string>("Database:ConnectionString")
                       ?? "Data Source=jobscheduler.db;Cache=Shared";

builder.Services.AddSchedulerSqlite(connectionString);
builder.Services.AddSchedulerCore(builder.Configuration);
builder.Services.AddSchedulerEngine();

var host = builder.Build();

// Ensure the schema exists (idempotent); the worker never seeds demo data — the API owns that.
await host.Services.InitializeSchedulerAsync(seedDemoData: false);

host.Run();

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}
