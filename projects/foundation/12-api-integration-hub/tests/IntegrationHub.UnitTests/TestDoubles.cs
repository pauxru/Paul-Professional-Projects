using System.Text.Json.Nodes;
using IntegrationHub.Application;
using IntegrationHub.Domain;

namespace IntegrationHub.UnitTests;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
}

internal sealed class SequentialIdGenerator : IIdGenerator
{
    private int _value;
    public Guid NewId()
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(Interlocked.Increment(ref _value)).CopyTo(bytes, 0);
        return new Guid(bytes);
    }
}

internal sealed class MemoryNonceStore : IWebhookNonceStore
{
    private readonly HashSet<string> _nonces = [];
    public Task<bool> TryUseAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        Task.FromResult(_nonces.Add(nonce));
}

internal sealed class MemoryIdempotencyStore : IIdempotencyStore
{
    private readonly Dictionary<(string Scope, string Key), IdempotencyResult> _items = [];
    public Task<IdempotencyResult?> GetAsync(string scope, string key, CancellationToken cancellationToken) =>
        Task.FromResult(_items.GetValueOrDefault((scope, key)));
    public Task<bool> TryStoreAsync(string scope, string key, IdempotencyResult result, CancellationToken cancellationToken) =>
        Task.FromResult(_items.TryAdd((scope, key), result));
}

internal sealed class MemoryCheckpointStore : ICheckpointStore
{
    private readonly Dictionary<(Guid RunId, string Step, string Batch), int> _items = [];
    public Task<int> GetNextIndexAsync(Guid runId, string stepId, string batchKey, CancellationToken cancellationToken) =>
        Task.FromResult(_items.GetValueOrDefault((runId, stepId, batchKey)));
    public Task SaveAsync(Guid runId, string stepId, string batchKey, int nextIndex, CancellationToken cancellationToken)
    {
        _items[(runId, stepId, batchKey)] = nextIndex;
        return Task.CompletedTask;
    }
}

internal sealed class MemoryDeadLetterStore : IDeadLetterStore
{
    public List<DeadLetterItem> Items { get; } = [];
    public Task<DeadLetterItem> AddAsync(DeadLetterItem item, CancellationToken cancellationToken)
    {
        var existing = Items.FirstOrDefault(x => x.RunId == item.RunId && x.StepId == item.StepId && x.RecordKey == item.RecordKey);
        if (existing is not null)
        {
            return Task.FromResult(existing);
        }
        Items.Add(item);
        return Task.FromResult(item);
    }
    public Task<IReadOnlyList<DeadLetterItem>> ListAsync(DeadLetterStatus? status, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeadLetterItem>>(Items.Where(x => status is null || x.Status == status).ToArray());
    public Task<DeadLetterItem?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(x => x.Id == id));
    public Task MarkReplayedAsync(Guid id, Guid replayRunId, CancellationToken cancellationToken)
    {
        var index = Items.FindIndex(x => x.Id == id);
        Items[index] = Items[index] with { Status = DeadLetterStatus.Replayed, ReplayRunId = replayRunId };
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<DeadLetterItem>> GetForReplayAsync(Guid? itemId, Guid? runId, string? batchKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeadLetterItem>>(Items.Where(x =>
            x.Status == DeadLetterStatus.Pending
            && (itemId is null || x.Id == itemId)
            && (runId is null || x.RunId == runId)
            && (batchKey is null || x.BatchKey == batchKey)).ToArray());
}

internal sealed class MemoryExecutionStore : IExecutionStore
{
    public List<RunSnapshot> Runs { get; } = [];
    public List<StepSnapshot> Steps { get; } = [];
    public Task CreateRunAsync(RunSnapshot run, CancellationToken cancellationToken)
    {
        Runs.Add(run);
        return Task.CompletedTask;
    }
    public Task AppendStepAsync(StepSnapshot step, CancellationToken cancellationToken)
    {
        Steps.Add(step);
        return Task.CompletedTask;
    }
    public Task CompleteRunAsync(Guid runId, RunStatus status, int recordsProcessed, string? error, CancellationToken cancellationToken)
    {
        var index = Runs.FindIndex(x => x.Id == runId);
        Runs[index] = Runs[index] with { Status = status, RecordsProcessed = recordsProcessed, Error = error };
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<RunSnapshot>> SearchRunsAsync(RunSearch search, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunSnapshot>>(Runs);
    public Task<RunDetails?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = Runs.FirstOrDefault(x => x.Id == runId);
        return Task.FromResult(run is null ? null : new RunDetails(run, Steps.Where(x => x.RunId == runId).ToArray()));
    }
    public Task PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class MemoryDriftStore : IContractDriftStore
{
    public List<ContractDriftAlert> Items { get; } = [];
    public Task AddAsync(ContractDriftAlert alert, CancellationToken cancellationToken)
    {
        Items.Add(alert);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<ContractDriftAlert>> ListAsync(bool unresolvedOnly, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContractDriftAlert>>(Items);
}

internal sealed class TestFlowStore : IFlowStore
{
    private readonly Dictionary<Guid, IntegrationFlow> _flows = [];
    public static TestFlowStore WithActiveFlow(Guid id, FlowDefinition definition)
    {
        var store = new TestFlowStore();
        var parser = new FlowDefinitionParser();
        var flow = new IntegrationFlow(id, definition.Name);
        flow.AddVersion("json", parser.Serialize("json", definition), DateTimeOffset.UtcNow, "test");
        flow.Activate(1);
        store._flows[id] = flow;
        return store;
    }
    public Task<IntegrationFlow> CreateAsync(string name, string format, string definition, string actor, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<FlowVersion> AddVersionAsync(Guid flowId, string format, string definition, string actor, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task ActivateAsync(Guid flowId, int version, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RollbackAsync(Guid flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IntegrationFlow?> GetAsync(Guid flowId, CancellationToken cancellationToken) =>
        Task.FromResult(_flows.GetValueOrDefault(flowId));
    public Task<IReadOnlyList<IntegrationFlow>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IntegrationFlow>>(_flows.Values.ToArray());
}

internal sealed class EmptyConnectorRegistry : IConnectorRegistry
{
    public IReadOnlyCollection<ConnectorDescriptor> List() => [];
    public IConnector Get(string id, string? version = null) => throw new KeyNotFoundException();
}

internal sealed class CountingConnector : IConnector
{
    public int Calls { get; private set; }
    public ConnectorDescriptor Descriptor { get; } = new(
        "target", "Target", "1.0.0", ConnectorAuthKind.None,
        [new ConnectorOperationDescriptor("write", "POST", "/", null, null, true)],
        new RateLimitDescriptor(100, TimeSpan.FromMinutes(1), 1),
        PaginationStyle.None,
        "Test target");
    public Task<ConnectorResult> ExecuteAsync(string operation, JsonNode? input, ConnectorExecutionContext context, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ConnectorResult(input?.DeepClone(), 201, new Dictionary<string, string>(), 1));
    }
}

internal sealed class MemorySecretStore(Dictionary<string, string> values) : ISecretStore
{
    public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        values[name] = value;
        return Task.CompletedTask;
    }
    public Task<string> GetAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(values[name]);
    public Task<int> RotateAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        values[name] = value;
        return Task.FromResult(2);
    }
    public Task<IReadOnlyCollection<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<SecretMetadata>>(values.Keys.Select(x => new SecretMetadata(x, 1, DateTimeOffset.UtcNow)).ToArray());
}
