using System.Text.Json.Nodes;
using IntegrationHub.Domain;

namespace IntegrationHub.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IIdGenerator
{
    Guid NewId();
}

public sealed record ConnectorExecutionContext(
    string CorrelationId,
    string? IdempotencyKey = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    int PageSize = 100);

public sealed record ConnectorResult(
    JsonNode? Payload,
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    int RecordsRead = 0,
    bool FromIdempotencyCache = false);

public interface IConnector
{
    ConnectorDescriptor Descriptor { get; }

    Task<ConnectorResult> ExecuteAsync(
        string operation,
        JsonNode? input,
        ConnectorExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IConnectorRegistry
{
    IReadOnlyCollection<ConnectorDescriptor> List();
    IConnector Get(string id, string? version = null);
}

public interface ISecretStore
{
    Task SetAsync(string name, string value, CancellationToken cancellationToken = default);
    Task<string> GetAsync(string name, CancellationToken cancellationToken = default);
    Task<int> RotateAsync(string name, string value, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed record SecretMetadata(string Name, int CurrentVersion, DateTimeOffset UpdatedAt);

public interface IWebhookNonceStore
{
    Task<bool> TryUseAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

public interface IExecutionStore
{
    Task CreateRunAsync(RunSnapshot run, CancellationToken cancellationToken);
    Task AppendStepAsync(StepSnapshot step, CancellationToken cancellationToken);
    Task CompleteRunAsync(Guid runId, RunStatus status, int recordsProcessed, string? error, CancellationToken cancellationToken);
    Task<IReadOnlyList<RunSnapshot>> SearchRunsAsync(RunSearch search, CancellationToken cancellationToken);
    Task<RunDetails?> GetRunAsync(Guid runId, CancellationToken cancellationToken);
    Task PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public sealed record RunSnapshot(
    Guid Id,
    Guid FlowId,
    int FlowVersion,
    RunStatus Status,
    string CorrelationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null,
    int RecordsProcessed = 0,
    string? Error = null);

public sealed record StepSnapshot(
    Guid Id,
    Guid RunId,
    string StepId,
    FlowStepKind Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? InputSnapshot,
    string? OutputSnapshot,
    int RecordsIn,
    int RecordsOut,
    string? Error);

public sealed record RunDetails(RunSnapshot Run, IReadOnlyList<StepSnapshot> Steps);

public sealed record RunSearch(
    RunStatus? Status = null,
    Guid? FlowId = null,
    string? CorrelationId = null,
    int Page = 1,
    int PageSize = 50);

public interface IDeadLetterStore
{
    Task<DeadLetterItem> AddAsync(DeadLetterItem item, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeadLetterItem>> ListAsync(DeadLetterStatus? status, CancellationToken cancellationToken);
    Task<DeadLetterItem?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task MarkReplayedAsync(Guid id, Guid replayRunId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeadLetterItem>> GetForReplayAsync(Guid? itemId, Guid? runId, string? batchKey, CancellationToken cancellationToken);
}

public sealed record DeadLetterItem(
    Guid Id,
    Guid RunId,
    string StepId,
    string BatchKey,
    string RecordKey,
    string Payload,
    string Error,
    DeadLetterStatus Status,
    DateTimeOffset CreatedAt,
    Guid? ReplayRunId = null);

public interface ICheckpointStore
{
    Task<int> GetNextIndexAsync(Guid runId, string stepId, string batchKey, CancellationToken cancellationToken);
    Task SaveAsync(Guid runId, string stepId, string batchKey, int nextIndex, CancellationToken cancellationToken);
}

public interface IIdempotencyStore
{
    Task<IdempotencyResult?> GetAsync(string scope, string key, CancellationToken cancellationToken);
    Task<bool> TryStoreAsync(string scope, string key, IdempotencyResult result, CancellationToken cancellationToken);
}

public sealed record IdempotencyResult(int StatusCode, string Payload, DateTimeOffset CreatedAt);

public interface IFlowStore
{
    Task<IntegrationFlow> CreateAsync(string name, string format, string definition, string actor, CancellationToken cancellationToken);
    Task<FlowVersion> AddVersionAsync(Guid flowId, string format, string definition, string actor, CancellationToken cancellationToken);
    Task ActivateAsync(Guid flowId, int version, CancellationToken cancellationToken);
    Task RollbackAsync(Guid flowId, CancellationToken cancellationToken);
    Task<IntegrationFlow?> GetAsync(Guid flowId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IntegrationFlow>> ListAsync(CancellationToken cancellationToken);
}

public interface IContractDriftStore
{
    Task AddAsync(ContractDriftAlert alert, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractDriftAlert>> ListAsync(bool unresolvedOnly, CancellationToken cancellationToken);
}

public sealed record ContractDriftAlert(
    Guid Id,
    string ConnectorId,
    string Operation,
    string FieldPath,
    string Change,
    DateTimeOffset DetectedAt,
    bool Resolved = false);

public interface IFlowRunner
{
    Task<RunSnapshot> RunAsync(Guid flowId, JsonNode? triggerPayload, string correlationId, CancellationToken cancellationToken);
    Task<RunSnapshot> ReplayAsync(Guid? itemId, Guid? runId, string? batchKey, string correlationId, CancellationToken cancellationToken);
}

public interface ISecretRedactor
{
    string Redact(string value);
    JsonNode? Redact(JsonNode? value);
}

public interface IFlowDefinitionParser
{
    FlowDefinition Parse(string format, string definition);
    string Serialize(string format, FlowDefinition definition);
}
