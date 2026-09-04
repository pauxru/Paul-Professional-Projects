using System.Text.Json;

namespace IntegrationHub.Domain;

public enum ConnectorAuthKind
{
    None,
    ApiKey,
    Basic,
    Bearer,
    OAuth2ClientCredentials,
    Hmac
}

public enum PaginationStyle
{
    None,
    PageNumber,
    Offset,
    Cursor,
    LinkHeader
}

public enum ContractValueType
{
    String,
    Number,
    Integer,
    Boolean,
    Object,
    Array,
    Null
}

public enum FlowTriggerKind
{
    Manual,
    Webhook,
    Schedule
}

public enum FlowStepKind
{
    Trigger,
    Fetch,
    Transform,
    Filter,
    Enrich,
    Route,
    Load,
    Respond
}

public enum RunStatus
{
    Pending,
    Running,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Cancelled
}

public enum DeadLetterStatus
{
    Pending,
    Replayed,
    Resolved
}

public sealed record ContractField(
    string Path,
    ContractValueType Type,
    bool Required = true,
    bool ContainsPii = false);

public sealed record JsonContract(
    string Name,
    IReadOnlyList<ContractField> Fields,
    bool AllowAdditionalFields = true);

public sealed record RateLimitDescriptor(int Requests, TimeSpan Window, int MaxConcurrency);

public sealed record ConnectorOperationDescriptor(
    string Name,
    string Method,
    string PathTemplate,
    JsonContract? InputContract,
    JsonContract? OutputContract,
    bool IsIdempotent,
    string? Description = null);

public sealed record ConnectorDescriptor(
    string Id,
    string DisplayName,
    string Version,
    ConnectorAuthKind AuthKind,
    IReadOnlyList<ConnectorOperationDescriptor> Operations,
    RateLimitDescriptor RateLimit,
    PaginationStyle PaginationStyle,
    string Description);

public sealed record TriggerDefinition(
    FlowTriggerKind Kind,
    string? Cron = null,
    string CatchUpPolicy = "latest",
    string? WebhookPath = null);

public sealed record FlowStepDefinition(
    string Id,
    FlowStepKind Kind,
    IReadOnlyDictionary<string, string> Settings,
    int TimeoutSeconds = 30,
    int RetryCount = 3);

public sealed record FlowDefinition(
    string Name,
    TriggerDefinition Trigger,
    IReadOnlyList<FlowStepDefinition> Steps)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new DomainValidationException("Flow name is required.");
        }

        if (Steps.Count == 0)
        {
            throw new DomainValidationException("A flow requires at least one step.");
        }

        if (Steps.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Steps.Count)
        {
            throw new DomainValidationException("Flow step ids must be unique.");
        }

        if (Steps.Any(x => x.TimeoutSeconds is < 1 or > 600 || x.RetryCount is < 0 or > 10))
        {
            throw new DomainValidationException("Step timeout or retry limit is outside the supported range.");
        }
    }
}

public sealed record FlowVersion(
    int Version,
    string Format,
    string Definition,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed class IntegrationFlow
{
    private readonly List<FlowVersion> _versions = [];

    public IntegrationFlow(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new DomainValidationException("Flow id is required.");
        }

        Id = id;
        Rename(name);
    }

    public Guid Id { get; }
    public string Name { get; private set; } = string.Empty;
    public int? ActiveVersion { get; private set; }
    public IReadOnlyList<FlowVersion> Versions => _versions;

    public FlowVersion AddVersion(string format, string definition, DateTimeOffset now, string actor)
    {
        if (format is not ("json" or "yaml"))
        {
            throw new DomainValidationException("Flow format must be json or yaml.");
        }

        if (string.IsNullOrWhiteSpace(definition) || definition.Length > 256_000)
        {
            throw new DomainValidationException("Flow definition is empty or too large.");
        }

        var version = new FlowVersion(_versions.Count + 1, format, definition, now, actor);
        _versions.Add(version);
        return version;
    }

    public void Activate(int version)
    {
        if (_versions.All(x => x.Version != version))
        {
            throw new DomainValidationException($"Flow version {version} does not exist.");
        }

        ActiveVersion = version;
    }

    public void Rollback()
    {
        if (ActiveVersion is null)
        {
            throw new DomainValidationException("The flow has no active version.");
        }

        var previous = _versions.Where(x => x.Version < ActiveVersion).MaxBy(x => x.Version);
        if (previous is null)
        {
            throw new DomainValidationException("There is no previous version to activate.");
        }

        ActiveVersion = previous.Version;
    }

    private void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160)
        {
            throw new DomainValidationException("Flow name must contain 1 to 160 characters.");
        }

        Name = name.Trim();
    }
}

public sealed class DomainValidationException(string message) : Exception(message);
