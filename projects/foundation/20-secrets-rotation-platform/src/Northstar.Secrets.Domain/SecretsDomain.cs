using System.Text.RegularExpressions;

namespace Northstar.Secrets.Domain;

public enum SecretType
{
    ApiKey,
    DatabasePassword,
    ServiceAccountKey,
    SigningKey,
    ConnectionString,
    Certificate,
    WebhookSigningSecret,
    EncryptionKey
}

public enum SecretCriticality
{
    Low,
    Medium,
    High,
    Critical
}

public enum SecretVersionState
{
    Pending,
    Current,
    Previous,
    Deprecated,
    Revoked,
    Destroyed
}

public enum RotationState
{
    Requested,
    Generating,
    StagedNewVersion,
    NotifyingConsumers,
    AwaitingAcknowledgement,
    Promoting,
    Verifying,
    Completed,
    RolledBack,
    Failed
}

public enum RotationStrategyKind
{
    DualWrite,
    SingleCutover
}

public enum ConsumerAcknowledgementStatus
{
    Pending,
    Acknowledged,
    NotRequired,
    TimedOut
}

public enum ApprovalOperation
{
    DestroyVersion,
    BreakGlassRead,
    EmergencyRevoke,
    IncidentRotation
}

public sealed class SecretRecord
{
    private SecretRecord() { }

    private SecretRecord(
        Guid id,
        string name,
        SecretType type,
        string ownerTeam,
        string environment,
        SecretCriticality criticality,
        string description,
        TimeSpan rotationInterval,
        TimeSpan maxAge,
        TimeSpan gracePeriod,
        DateTimeOffset createdAt)
    {
        Id = id;
        Name = NormalizeName(name);
        Type = type;
        OwnerTeam = Required(ownerTeam, nameof(ownerTeam));
        Environment = Required(environment, nameof(environment));
        Criticality = criticality;
        Description = description?.Trim() ?? string.Empty;
        RotationInterval = Positive(rotationInterval, nameof(rotationInterval));
        MaxAge = Positive(maxAge, nameof(maxAge));
        GracePeriod = gracePeriod < TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(gracePeriod))
            : gracePeriod;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public SecretType Type { get; private set; }
    public string OwnerTeam { get; private set; } = string.Empty;
    public string Environment { get; private set; } = string.Empty;
    public SecretCriticality Criticality { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public TimeSpan RotationInterval { get; private set; }
    public TimeSpan MaxAge { get; private set; }
    public TimeSpan GracePeriod { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public long ConcurrencyVersion { get; private set; }
    public List<SecretVersion> Versions { get; private set; } = [];
    public List<SecretTag> Tags { get; private set; } = [];
    public List<SecretConsumer> Consumers { get; private set; } = [];

    public string Reference => SecretReference.Create(Name, CurrentVersion?.VersionNumber ?? 1).ToString();
    public SecretVersion? CurrentVersion => Versions.SingleOrDefault(x => x.State == SecretVersionState.Current);

    public static SecretRecord Register(
        Guid id,
        string name,
        SecretType type,
        string ownerTeam,
        string environment,
        SecretCriticality criticality,
        IEnumerable<string>? tags,
        string description,
        TimeSpan rotationInterval,
        TimeSpan maxAge,
        TimeSpan gracePeriod,
        DateTimeOffset createdAt)
    {
        var record = new SecretRecord(
            id, name, type, ownerTeam, environment, criticality, description,
            rotationInterval, maxAge, gracePeriod, createdAt);

        foreach (var tag in (tags ?? []).Select(x => x.Trim().ToLowerInvariant())
                     .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal))
        {
            record.Tags.Add(new SecretTag(record.Id, tag));
        }

        return record;
    }

    public SecretVersion StageVersion(
        byte[] ciphertext,
        byte[] nonce,
        byte[] authenticationTag,
        byte[] wrappedDataEncryptionKey,
        string keyVersion,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (expiresAt <= createdAt)
        {
            throw new DomainRuleException("A secret version must expire after it is created.");
        }

        var nextVersion = Versions.Count == 0 ? 1 : Versions.Max(x => x.VersionNumber) + 1;
        var version = SecretVersion.Create(
            Guid.NewGuid(), Id, nextVersion, ciphertext, nonce, authenticationTag,
            wrappedDataEncryptionKey, keyVersion, createdAt, expiresAt);
        Versions.Add(version);
        Touch();
        return version;
    }

    public void Promote(int versionNumber, DateTimeOffset activatedAt)
    {
        var candidate = Versions.SingleOrDefault(x => x.VersionNumber == versionNumber)
            ?? throw new DomainRuleException($"Secret version v{versionNumber} does not exist.");

        if (candidate.State is not SecretVersionState.Pending)
        {
            throw new DomainRuleException("Only a pending version can be promoted.");
        }

        foreach (var previous in Versions.Where(x => x.State == SecretVersionState.Previous))
        {
            previous.Deprecate();
        }

        CurrentVersion?.MarkPrevious();
        candidate.Activate(activatedAt);
        EnsureTwoActiveVersionWindow();
        Touch();
    }

    public void Rollback(int candidateVersion, DateTimeOffset now)
    {
        var candidate = Versions.SingleOrDefault(x => x.VersionNumber == candidateVersion)
            ?? throw new DomainRuleException($"Secret version v{candidateVersion} does not exist.");

        if (candidate.State == SecretVersionState.Current)
        {
            candidate.Revoke();
            var previous = Versions
                .Where(x => x.State == SecretVersionState.Previous)
                .OrderByDescending(x => x.VersionNumber)
                .FirstOrDefault();
            previous?.Activate(now);
        }
        else if (candidate.State is SecretVersionState.Pending or SecretVersionState.Deprecated)
        {
            candidate.Revoke();
        }

        EnsureTwoActiveVersionWindow();
        Touch();
    }

    public void RevokeAll(DateTimeOffset now)
    {
        foreach (var version in Versions.Where(x => x.State != SecretVersionState.Destroyed))
        {
            version.Revoke();
        }

        Touch();
    }

    public void DestroyVersion(int versionNumber, DateTimeOffset now)
    {
        var version = Versions.SingleOrDefault(x => x.VersionNumber == versionNumber)
            ?? throw new DomainRuleException($"Secret version v{versionNumber} does not exist.");
        if (version.State == SecretVersionState.Current)
        {
            throw new DomainRuleException("The current version cannot be destroyed.");
        }

        version.Destroy(now);
        Touch();
    }

    public SecretVersion GetReadableVersion(int? versionNumber, DateTimeOffset now)
    {
        var version = versionNumber is null
            ? CurrentVersion
            : Versions.SingleOrDefault(x => x.VersionNumber == versionNumber.Value);

        if (version is null || !version.IsReadableAt(now))
        {
            throw new DomainRuleException("The requested secret version is not available.");
        }

        return version;
    }

    public void LinkConsumer(Guid consumerId)
    {
        if (Consumers.All(x => x.ConsumerId != consumerId))
        {
            Consumers.Add(new SecretConsumer(Id, consumerId));
            Touch();
        }
    }

    public void ReplaceMetadata(
        string ownerTeam,
        SecretCriticality criticality,
        string description,
        TimeSpan rotationInterval,
        TimeSpan maxAge,
        TimeSpan gracePeriod)
    {
        OwnerTeam = Required(ownerTeam, nameof(ownerTeam));
        Criticality = criticality;
        Description = description?.Trim() ?? string.Empty;
        RotationInterval = Positive(rotationInterval, nameof(rotationInterval));
        MaxAge = Positive(maxAge, nameof(maxAge));
        GracePeriod = gracePeriod < TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(gracePeriod))
            : gracePeriod;
        Touch();
    }

    private void EnsureTwoActiveVersionWindow()
    {
        var activeCount = Versions.Count(x =>
            x.State is SecretVersionState.Current or SecretVersionState.Previous);
        if (activeCount > 2)
        {
            throw new DomainRuleException("At most two promoted secret versions may be active.");
        }

        if (Versions.Count(x => x.State == SecretVersionState.Current) > 1)
        {
            throw new DomainRuleException("Only one secret version may be current.");
        }
    }

    private void Touch() => ConcurrencyVersion++;

    public static string NormalizeName(string name)
    {
        var normalized = Required(name, nameof(name)).Trim().ToLowerInvariant();
        if (!Regex.IsMatch(normalized, "^[a-z0-9][a-z0-9-]{1,62}/[a-z0-9][a-z0-9-]{1,30}/[a-z0-9][a-z0-9-]{1,62}$"))
        {
            throw new DomainRuleException(
                "Secret names must use the hierarchical app/environment/purpose form.");
        }

        return normalized;
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", name)
            : value.Trim();

    private static TimeSpan Positive(TimeSpan value, string name) =>
        value <= TimeSpan.Zero ? throw new ArgumentOutOfRangeException(name) : value;
}

public sealed class SecretVersion
{
    private SecretVersion() { }

    private SecretVersion(
        Guid id,
        Guid secretId,
        int versionNumber,
        byte[] ciphertext,
        byte[] nonce,
        byte[] authenticationTag,
        byte[] wrappedDataEncryptionKey,
        string keyVersion,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        SecretId = secretId;
        VersionNumber = versionNumber;
        Ciphertext = ciphertext.ToArray();
        Nonce = nonce.ToArray();
        AuthenticationTag = authenticationTag.ToArray();
        WrappedDataEncryptionKey = wrappedDataEncryptionKey.ToArray();
        KeyVersion = keyVersion;
        State = SecretVersionState.Pending;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid SecretId { get; private set; }
    public int VersionNumber { get; private set; }
    public SecretVersionState State { get; private set; }
    public byte[] Ciphertext { get; private set; } = [];
    public byte[] Nonce { get; private set; } = [];
    public byte[] AuthenticationTag { get; private set; } = [];
    public byte[] WrappedDataEncryptionKey { get; private set; } = [];
    public string KeyVersion { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? DestroyedAt { get; private set; }

    internal static SecretVersion Create(
        Guid id,
        Guid secretId,
        int versionNumber,
        byte[] ciphertext,
        byte[] nonce,
        byte[] authenticationTag,
        byte[] wrappedDataEncryptionKey,
        string keyVersion,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt) =>
        new(id, secretId, versionNumber, ciphertext, nonce, authenticationTag,
            wrappedDataEncryptionKey, keyVersion, createdAt, expiresAt);

    public bool IsReadableAt(DateTimeOffset now) =>
        State is SecretVersionState.Pending or SecretVersionState.Current or SecretVersionState.Previous
        && ExpiresAt > now;

    internal void Activate(DateTimeOffset activatedAt)
    {
        if (State is SecretVersionState.Revoked or SecretVersionState.Destroyed)
        {
            throw new DomainRuleException("A revoked or destroyed secret version cannot be activated.");
        }

        State = SecretVersionState.Current;
        ActivatedAt = activatedAt;
    }

    internal void MarkPrevious()
    {
        if (State == SecretVersionState.Current)
        {
            State = SecretVersionState.Previous;
        }
    }

    internal void Deprecate()
    {
        if (State == SecretVersionState.Previous)
        {
            State = SecretVersionState.Deprecated;
        }
    }

    public void Revoke()
    {
        if (State != SecretVersionState.Destroyed)
        {
            State = SecretVersionState.Revoked;
        }
    }

    public void Destroy(DateTimeOffset destroyedAt)
    {
        if (State == SecretVersionState.Current)
        {
            throw new DomainRuleException("The current version cannot be destroyed.");
        }

        State = SecretVersionState.Destroyed;
        DestroyedAt = destroyedAt;
        Ciphertext = [];
        Nonce = [];
        AuthenticationTag = [];
        WrappedDataEncryptionKey = [];
        KeyVersion = string.Empty;
    }

    public void ReplaceWrappedDataEncryptionKey(byte[] wrappedKey, string keyVersion)
    {
        if (State == SecretVersionState.Destroyed)
        {
            return;
        }

        WrappedDataEncryptionKey = wrappedKey.ToArray();
        KeyVersion = keyVersion;
    }
}

public sealed class SecretTag
{
    private SecretTag() { }

    public SecretTag(Guid secretId, string value)
    {
        SecretId = secretId;
        Value = value;
    }

    public Guid SecretId { get; private set; }
    public string Value { get; private set; } = string.Empty;
}

public sealed class Consumer
{
    private Consumer() { }

    public Consumer(Guid id, string name, string application, string? webhookUrl, string? email)
    {
        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Name is required.") : name.Trim();
        Application = string.IsNullOrWhiteSpace(application)
            ? throw new ArgumentException("Application is required.")
            : application.Trim();
        WebhookUrl = webhookUrl?.Trim();
        Email = email?.Trim();
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Application { get; private set; } = string.Empty;
    public string? WebhookUrl { get; private set; }
    public string? Email { get; private set; }
    public List<SecretConsumer> SecretLinks { get; private set; } = [];
}

public sealed class SecretConsumer
{
    private SecretConsumer() { }

    public SecretConsumer(Guid secretId, Guid consumerId)
    {
        SecretId = secretId;
        ConsumerId = consumerId;
    }

    public Guid SecretId { get; private set; }
    public Guid ConsumerId { get; private set; }
    public SecretRecord? Secret { get; private set; }
    public Consumer? Consumer { get; private set; }
}

public sealed class RotationOperation
{
    private static readonly IReadOnlyDictionary<RotationState, RotationState[]> AllowedTransitions =
        new Dictionary<RotationState, RotationState[]>
        {
            [RotationState.Requested] = [RotationState.Generating],
            [RotationState.Generating] = [RotationState.StagedNewVersion],
            [RotationState.StagedNewVersion] = [RotationState.NotifyingConsumers],
            [RotationState.NotifyingConsumers] = [RotationState.AwaitingAcknowledgement],
            [RotationState.AwaitingAcknowledgement] = [RotationState.Promoting],
            [RotationState.Promoting] = [RotationState.Verifying],
            [RotationState.Verifying] = [RotationState.Completed],
        };

    private RotationOperation() { }

    public RotationOperation(
        Guid id,
        Guid secretId,
        RotationStrategyKind strategy,
        string idempotencyKey,
        string requestedBy,
        string correlationId,
        DateTimeOffset requestedAt,
        DateTimeOffset acknowledgementDeadline,
        DateTimeOffset? maintenanceWindowStart)
    {
        Id = id;
        SecretId = secretId;
        Strategy = strategy;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? throw new ArgumentException("An idempotency key is required.")
            : idempotencyKey.Trim();
        RequestedBy = requestedBy;
        CorrelationId = correlationId;
        RequestedAt = requestedAt;
        UpdatedAt = requestedAt;
        AcknowledgementDeadline = acknowledgementDeadline;
        MaintenanceWindowStart = maintenanceWindowStart;
        State = RotationState.Requested;
    }

    public Guid Id { get; private set; }
    public Guid SecretId { get; private set; }
    public RotationStrategyKind Strategy { get; private set; }
    public RotationState State { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestedBy { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset AcknowledgementDeadline { get; private set; }
    public DateTimeOffset? MaintenanceWindowStart { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public int? PreviousVersionNumber { get; private set; }
    public int? NewVersionNumber { get; private set; }
    public string? FailureReason { get; private set; }
    public bool CancellationRequested { get; private set; }
    public List<ConsumerAcknowledgement> Acknowledgements { get; private set; } = [];

    public bool IsTerminal =>
        State is RotationState.Completed or RotationState.RolledBack or RotationState.Failed;

    public void TransitionTo(RotationState next, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            throw new DomainRuleException("A terminal rotation cannot transition.");
        }

        if (!AllowedTransitions.TryGetValue(State, out var allowed) || !allowed.Contains(next))
        {
            throw new DomainRuleException($"Invalid rotation transition: {State} -> {next}.");
        }

        State = next;
        UpdatedAt = now;
        if (next == RotationState.Completed)
        {
            CompletedAt = now;
        }
    }

    public void SetGeneratedVersion(int versionNumber, int? previousVersionNumber)
    {
        if (State != RotationState.Generating)
        {
            throw new DomainRuleException("A generated version can only be recorded while generating.");
        }

        NewVersionNumber = versionNumber;
        PreviousVersionNumber = previousVersionNumber;
    }

    public void InitializeAcknowledgements(IEnumerable<Guid> consumerIds, bool required)
    {
        if (Acknowledgements.Count > 0)
        {
            return;
        }

        foreach (var consumerId in consumerIds.Distinct())
        {
            Acknowledgements.Add(new ConsumerAcknowledgement(
                Id,
                consumerId,
                required ? ConsumerAcknowledgementStatus.Pending : ConsumerAcknowledgementStatus.NotRequired));
        }
    }

    public void MarkNotificationSent(Guid consumerId, DateTimeOffset sentAt)
    {
        var acknowledgement = Acknowledgements.Single(x => x.ConsumerId == consumerId);
        acknowledgement.MarkNotificationSent(sentAt);
    }

    public void Acknowledge(Guid consumerId, DateTimeOffset acknowledgedAt)
    {
        if (State != RotationState.AwaitingAcknowledgement)
        {
            throw new DomainRuleException("The rotation is not awaiting acknowledgements.");
        }

        var acknowledgement = Acknowledgements.SingleOrDefault(x => x.ConsumerId == consumerId)
            ?? throw new DomainRuleException("The consumer is not linked to this rotation.");
        acknowledgement.Acknowledge(acknowledgedAt);
        UpdatedAt = acknowledgedAt;
    }

    public bool AllRequiredConsumersAcknowledged =>
        Acknowledgements.All(x =>
            x.Status is ConsumerAcknowledgementStatus.Acknowledged
                or ConsumerAcknowledgementStatus.NotRequired);

    public void MarkTimedOut()
    {
        foreach (var acknowledgement in Acknowledgements.Where(x =>
                     x.Status == ConsumerAcknowledgementStatus.Pending))
        {
            acknowledgement.MarkTimedOut();
        }
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        if (!IsTerminal)
        {
            CancellationRequested = true;
            UpdatedAt = now;
        }
    }

    public void MarkRolledBack(string reason, DateTimeOffset now)
    {
        State = RotationState.RolledBack;
        FailureReason = reason;
        CompletedAt = now;
        UpdatedAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        State = RotationState.Failed;
        FailureReason = reason;
        CompletedAt = now;
        UpdatedAt = now;
    }
}

public sealed class ConsumerAcknowledgement
{
    private ConsumerAcknowledgement() { }

    public ConsumerAcknowledgement(
        Guid rotationId,
        Guid consumerId,
        ConsumerAcknowledgementStatus status)
    {
        RotationId = rotationId;
        ConsumerId = consumerId;
        Status = status;
    }

    public Guid RotationId { get; private set; }
    public Guid ConsumerId { get; private set; }
    public ConsumerAcknowledgementStatus Status { get; private set; }
    public DateTimeOffset? NotificationSentAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public void MarkNotificationSent(DateTimeOffset sentAt) => NotificationSentAt ??= sentAt;

    public void Acknowledge(DateTimeOffset acknowledgedAt)
    {
        if (Status == ConsumerAcknowledgementStatus.NotRequired)
        {
            return;
        }

        Status = ConsumerAcknowledgementStatus.Acknowledged;
        AcknowledgedAt = acknowledgedAt;
    }

    public void MarkTimedOut() => Status = ConsumerAcknowledgementStatus.TimedOut;
}

public sealed class AccessPolicy
{
    private AccessPolicy() { }

    public AccessPolicy(
        Guid id,
        string subject,
        string pathPattern,
        bool canManageMetadata,
        bool canReadValues,
        bool canOperateRotations,
        bool canBreakGlass)
    {
        Id = id;
        Subject = string.IsNullOrWhiteSpace(subject) ? throw new ArgumentException("Subject is required.") : subject;
        PathPattern = string.IsNullOrWhiteSpace(pathPattern)
            ? throw new ArgumentException("Path pattern is required.")
            : pathPattern.ToLowerInvariant();
        CanManageMetadata = canManageMetadata;
        CanReadValues = canReadValues;
        CanOperateRotations = canOperateRotations;
        CanBreakGlass = canBreakGlass;
    }

    public Guid Id { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string PathPattern { get; private set; } = string.Empty;
    public bool CanManageMetadata { get; private set; }
    public bool CanReadValues { get; private set; }
    public bool CanOperateRotations { get; private set; }
    public bool CanBreakGlass { get; private set; }
}

public sealed class AuditRecord
{
    private AuditRecord() { }

    public AuditRecord(
        Guid id,
        Guid? secretId,
        string actor,
        string action,
        string resource,
        string reason,
        string correlationId,
        DateTimeOffset occurredAt,
        string outcome,
        string? sourceAddress = null,
        string? userAgent = null)
    {
        Id = id;
        SecretId = secretId;
        Actor = actor;
        Action = action;
        Resource = resource;
        Reason = reason;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
        Outcome = outcome;
        SourceAddress = sourceAddress;
        UserAgent = userAgent;
    }

    public Guid Id { get; private set; }
    public Guid? SecretId { get; private set; }
    public string Actor { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string Resource { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public string Outcome { get; private set; } = string.Empty;
    public string? SourceAddress { get; private set; }
    public string? UserAgent { get; private set; }
}

public sealed class ApprovalRequest
{
    private ApprovalRequest() { }

    public ApprovalRequest(
        Guid id,
        ApprovalOperation operation,
        string resource,
        string requestedBy,
        string reason,
        DateTimeOffset requestedAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        Operation = operation;
        Resource = resource;
        RequestedBy = requestedBy;
        Reason = string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("A reason is required.")
            : reason.Trim();
        RequestedAt = requestedAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public ApprovalOperation Operation { get; private set; }
    public string Resource { get; private set; } = string.Empty;
    public string RequestedBy { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset? ExecutedAt { get; private set; }

    public bool IsApproved => ApprovedBy is not null;

    public void Approve(string approver, DateTimeOffset now)
    {
        if (string.Equals(approver, RequestedBy, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleException("Four-eyes approval requires a different approver.");
        }

        if (now >= ExpiresAt)
        {
            throw new DomainRuleException("The approval request has expired.");
        }

        ApprovedBy = approver;
        ApprovedAt = now;
    }

    public void MarkExecuted(DateTimeOffset now)
    {
        if (!IsApproved)
        {
            throw new DomainRuleException("The destructive operation requires approval.");
        }

        if (ExecutedAt is not null)
        {
            throw new DomainRuleException("The approval has already been consumed.");
        }

        ExecutedAt = now;
    }
}

public sealed class DomainRuleException(string message) : Exception(message);
