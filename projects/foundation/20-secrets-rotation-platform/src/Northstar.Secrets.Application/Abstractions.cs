using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public readonly record struct SecretBinding(
    Guid SecretId,
    string Name,
    SecretType Type,
    int VersionNumber);

public sealed record EncryptedPayload(
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] AuthenticationTag,
    byte[] WrappedDataEncryptionKey,
    string KeyVersion);

public sealed record WrappedKey(byte[] Value, string KeyVersion);

public interface IKeyProvider
{
    string CurrentKeyVersion { get; }
    WrappedKey WrapKey(ReadOnlySpan<byte> dataEncryptionKey);
    byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey, string keyVersion);
    void RotateTo(string keyVersion, ReadOnlySpan<byte> keyMaterial);
}

public interface ISecretCipher
{
    EncryptedPayload Encrypt(string plaintext, SecretBinding binding);
    string Decrypt(EncryptedPayload payload, SecretBinding binding);
    WrappedKey RewrapDataEncryptionKey(byte[] wrappedKey, string previousKeyVersion);
}

public sealed record GeneratedSecret(
    string Value,
    string Format,
    IReadOnlyDictionary<string, string> Metadata);

public interface ISecretGenerator
{
    IReadOnlySet<SecretType> SupportedTypes { get; }
    GeneratedSecret Generate(SecretType type, DateTimeOffset now);
}

public interface ISecretGeneratorRegistry
{
    GeneratedSecret Generate(SecretType type, DateTimeOffset now);
}

public sealed record VerificationResult(bool Succeeded, string Detail)
{
    public static VerificationResult Success(string detail = "Verification passed.") => new(true, detail);
    public static VerificationResult Failure(string detail) => new(false, detail);
}

public interface ISecretVerifier
{
    Task<VerificationResult> VerifyAsync(
        SecretRecord secret,
        SecretVersion candidate,
        string plaintext,
        CancellationToken cancellationToken);
}

public sealed record RotationNotice(
    Guid RotationId,
    Guid ConsumerId,
    string SecretName,
    string SecretReference,
    RotationStrategyKind Strategy,
    DateTimeOffset AcknowledgementDeadline,
    DateTimeOffset? MaintenanceWindowStart,
    string CorrelationId);

public sealed record NotificationDeliveryResult(bool Accepted, int Attempts, string? Failure);

public interface INotificationChannel
{
    string Name { get; }
    Task<NotificationDeliveryResult> SendAsync(
        Consumer consumer,
        RotationNotice notice,
        CancellationToken cancellationToken);
}

public interface IExternalSecretStore
{
    Task SetVersionAsync(
        string hierarchicalName,
        int version,
        string value,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken);

    Task<string?> GetVersionAsync(
        string hierarchicalName,
        int version,
        CancellationToken cancellationToken);

    Task DisableVersionAsync(
        string hierarchicalName,
        int version,
        CancellationToken cancellationToken);
}

public interface ISecretRedactionRegistry
{
    void Register(string secretValue);
    string Redact(string value);
}

public interface IPlatformMetrics
{
    void RotationCompleted(RotationState outcome, RotationStrategyKind strategy);
    void ValueRead(string environment, SecretType type, bool allowed);
    void AcknowledgementObserved(TimeSpan latency);
    void ObserveSecretAge(string secretName, TimeSpan age);
}

public sealed class NullPlatformMetrics : IPlatformMetrics
{
    public void RotationCompleted(RotationState outcome, RotationStrategyKind strategy) { }
    public void ValueRead(string environment, SecretType type, bool allowed) { }
    public void AcknowledgementObserved(TimeSpan latency) { }
    public void ObserveSecretAge(string secretName, TimeSpan age) { }
}

public interface ISecretsRepository
{
    Task<bool> AnySecretsAsync(CancellationToken cancellationToken);
    Task AddSecretAsync(SecretRecord secret, CancellationToken cancellationToken);
    Task AddSecretVersionAsync(SecretVersion version, CancellationToken cancellationToken);
    Task<SecretRecord?> GetSecretAsync(string normalizedName, CancellationToken cancellationToken);
    Task<SecretRecord?> GetSecretByIdAsync(Guid secretId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SecretRecord>> ListSecretsAsync(CancellationToken cancellationToken);

    Task AddConsumerAsync(Consumer consumer, CancellationToken cancellationToken);
    Task<Consumer?> GetConsumerAsync(Guid consumerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Consumer>> ListConsumersAsync(CancellationToken cancellationToken);

    Task AddRotationAsync(RotationOperation rotation, CancellationToken cancellationToken);
    Task AddConsumerAcknowledgementAsync(
        ConsumerAcknowledgement acknowledgement,
        CancellationToken cancellationToken);
    Task<RotationOperation?> GetRotationAsync(Guid rotationId, CancellationToken cancellationToken);
    Task<RotationOperation?> FindRotationByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<RotationOperation>> ListRotationsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RotationOperation>> ListPendingRotationsForConsumerAsync(
        Guid consumerId,
        CancellationToken cancellationToken);

    Task AddAccessPolicyAsync(AccessPolicy policy, CancellationToken cancellationToken);
    Task<IReadOnlyList<AccessPolicy>> ListAccessPoliciesAsync(
        string? subject,
        CancellationToken cancellationToken);

    Task AddAuditAsync(AuditRecord auditRecord, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditRecord>> ListAuditsAsync(
        Guid? secretId,
        DateTimeOffset? since,
        CancellationToken cancellationToken);

    Task AddApprovalAsync(ApprovalRequest approval, CancellationToken cancellationToken);
    Task<ApprovalRequest?> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class ApplicationValidationException(
    string message,
    IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } =
        errors ?? new Dictionary<string, string[]> { ["request"] = [message] };
}

public sealed class ResourceNotFoundException(string message) : Exception(message);
public sealed class ForbiddenOperationException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);

public sealed record RegisterSecretCommand(
    string Name,
    SecretType Type,
    string OwnerTeam,
    string Environment,
    SecretCriticality Criticality,
    IReadOnlyList<string> Tags,
    string Description,
    int RotationIntervalHours,
    int MaxAgeHours,
    int GracePeriodHours,
    IReadOnlyList<Guid>? ConsumerIds = null);

public sealed record RegisteredSecret(
    Guid Id,
    string Name,
    string Reference,
    SecretType Type,
    DateTimeOffset ExpiresAt);

public sealed record SecretValueResult(
    string Name,
    int Version,
    string Reference,
    string Value,
    DateTimeOffset ExpiresAt);

public sealed record RequestRotationCommand(
    string SecretName,
    RotationStrategyKind Strategy,
    string IdempotencyKey,
    string RequestedBy,
    string CorrelationId,
    DateTimeOffset? MaintenanceWindowStart = null);

public sealed record AccessContext(
    string Actor,
    string Reason,
    string CorrelationId,
    string? SourceAddress = null,
    string? UserAgent = null);

public sealed record SecretMetadata(
    Guid Id,
    string Name,
    SecretType Type,
    string OwnerTeam,
    string Environment,
    SecretCriticality Criticality,
    IReadOnlyList<string> Tags,
    string Description,
    string Reference,
    int? CurrentVersion,
    DateTimeOffset? CurrentExpiresAt,
    int ConsumerCount);

public static class SecretMappings
{
    public static SecretMetadata ToMetadata(this SecretRecord secret) =>
        new(
            secret.Id,
            secret.Name,
            secret.Type,
            secret.OwnerTeam,
            secret.Environment,
            secret.Criticality,
            secret.Tags.Select(x => x.Value).Order().ToArray(),
            secret.Description,
            secret.Reference,
            secret.CurrentVersion?.VersionNumber,
            secret.CurrentVersion?.ExpiresAt,
            secret.Consumers.Count);

    public static EncryptedPayload ToEncryptedPayload(this SecretVersion version) =>
        new(
            version.Ciphertext,
            version.Nonce,
            version.AuthenticationTag,
            version.WrappedDataEncryptionKey,
            version.KeyVersion);

    public static SecretBinding ToBinding(this SecretRecord secret, int versionNumber) =>
        new(secret.Id, secret.Name, secret.Type, versionNumber);
}
