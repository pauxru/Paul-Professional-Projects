using Northstar.Secrets.Domain;
using System.Text.RegularExpressions;

namespace Northstar.Secrets.Application;

public enum SecretPermission
{
    ManageMetadata,
    ReadValue,
    OperateRotation,
    BreakGlass
}

public sealed class PathPolicyEvaluator
{
    public bool IsAllowed(
        IEnumerable<AccessPolicy> policies,
        string subject,
        string secretName,
        SecretPermission permission)
    {
        var normalizedName = SecretRecord.NormalizeName(secretName);
        return policies.Any(policy =>
            (policy.Subject == "*" ||
             string.Equals(policy.Subject, subject, StringComparison.OrdinalIgnoreCase)) &&
            Matches(policy.PathPattern, normalizedName) &&
            HasPermission(policy, permission));
    }

    public bool Matches(string pattern, string secretName)
    {
        if (pattern is "*" or "**")
        {
            return true;
        }

        var patternSegments = pattern.Trim('/').Split('/');
        var nameSegments = secretName.Trim('/').Split('/');
        for (var index = 0; index < patternSegments.Length; index++)
        {
            if (patternSegments[index] == "**")
            {
                return true;
            }

            if (index >= nameSegments.Length)
            {
                return false;
            }

            if (!SegmentMatches(patternSegments[index], nameSegments[index]))
            {
                return false;
            }
        }

        return patternSegments.Length == nameSegments.Length;
    }

    private static bool SegmentMatches(string pattern, string value)
    {
        if (pattern == "*")
        {
            return true;
        }

        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasPermission(AccessPolicy policy, SecretPermission permission) =>
        permission switch
        {
            SecretPermission.ManageMetadata => policy.CanManageMetadata,
            SecretPermission.ReadValue => policy.CanReadValues,
            SecretPermission.OperateRotation => policy.CanOperateRotations,
            SecretPermission.BreakGlass => policy.CanBreakGlass,
            _ => false
        };
}

public sealed class PathAuthorizationService(
    ISecretsRepository repository,
    PathPolicyEvaluator evaluator)
{
    public async Task DemandAsync(
        string subject,
        string secretName,
        SecretPermission permission,
        CancellationToken cancellationToken)
    {
        var policies = await repository.ListAccessPoliciesAsync(subject, cancellationToken);
        if (!evaluator.IsAllowed(policies, subject, secretName, permission))
        {
            throw new ForbiddenOperationException(
                $"The actor is not permitted to perform {permission} on this secret path.");
        }
    }

    public async Task<IReadOnlyList<SecretMetadata>> FilterAsync(
        string subject,
        IEnumerable<SecretMetadata> secrets,
        SecretPermission permission,
        CancellationToken cancellationToken)
    {
        var policies = await repository.ListAccessPoliciesAsync(subject, cancellationToken);
        return secrets.Where(secret =>
                evaluator.IsAllowed(policies, subject, secret.Name, permission))
            .ToArray();
    }
}

public sealed class SecretLifecycleService(
    ISecretsRepository repository,
    ISecretGeneratorRegistry generators,
    ISecretCipher cipher,
    IKeyProvider keyProvider,
    IClock clock,
    PathPolicyEvaluator policyEvaluator,
    ISecretRedactionRegistry redactionRegistry,
    IPlatformMetrics metrics)
{
    public async Task<RegisteredSecret> RegisterAsync(
        RegisterSecretCommand command,
        CancellationToken cancellationToken)
    {
        ValidateRegistration(command);
        var normalizedName = SecretRecord.NormalizeName(command.Name);
        if (await repository.GetSecretAsync(normalizedName, cancellationToken) is not null)
        {
            throw new ConflictException($"Secret '{normalizedName}' is already registered.");
        }

        var now = clock.UtcNow;
        var secret = SecretRecord.Register(
            Guid.NewGuid(),
            normalizedName,
            command.Type,
            command.OwnerTeam,
            command.Environment,
            command.Criticality,
            command.Tags,
            command.Description,
            TimeSpan.FromHours(command.RotationIntervalHours),
            TimeSpan.FromHours(command.MaxAgeHours),
            TimeSpan.FromHours(command.GracePeriodHours),
            now);

        if (command.ConsumerIds is not null)
        {
            foreach (var consumerId in command.ConsumerIds.Distinct())
            {
                if (await repository.GetConsumerAsync(consumerId, cancellationToken) is null)
                {
                    throw new ApplicationValidationException($"Consumer '{consumerId}' does not exist.");
                }

                secret.LinkConsumer(consumerId);
            }
        }

        var generated = generators.Generate(secret.Type, now);
        redactionRegistry.Register(generated.Value);
        var payload = cipher.Encrypt(generated.Value, secret.ToBinding(1));
        var version = secret.StageVersion(
            payload.Ciphertext,
            payload.Nonce,
            payload.AuthenticationTag,
            payload.WrappedDataEncryptionKey,
            payload.KeyVersion,
            now,
            now.Add(secret.MaxAge));
        secret.Promote(version.VersionNumber, now);

        await repository.AddSecretAsync(secret, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return new RegisteredSecret(secret.Id, secret.Name, secret.Reference, secret.Type, version.ExpiresAt);
    }

    public async Task<IReadOnlyList<SecretMetadata>> ListAsync(CancellationToken cancellationToken)
    {
        var secrets = await repository.ListSecretsAsync(cancellationToken);
        foreach (var secret in secrets)
        {
            if (secret.CurrentVersion?.ActivatedAt is { } activated)
            {
                metrics.ObserveSecretAge(secret.Name, clock.UtcNow - activated);
            }
        }

        return secrets.Select(x => x.ToMetadata()).OrderBy(x => x.Name).ToArray();
    }

    public async Task<SecretMetadata> GetMetadataAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var secret = await GetRequiredSecretAsync(name, cancellationToken);
        return secret.ToMetadata();
    }

    public async Task<IReadOnlyList<SecretVersion>> GetVersionsAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var secret = await GetRequiredSecretAsync(name, cancellationToken);
        return secret.Versions.OrderByDescending(x => x.VersionNumber).ToArray();
    }

    public Task<SecretValueResult> ReadValueAsync(
        string name,
        int? version,
        AccessContext access,
        CancellationToken cancellationToken) =>
        ReadValueCoreAsync(name, version, access, bypassPolicy: false, cancellationToken);

    public Task<SecretValueResult> ReadValueWithApprovedBreakGlassAsync(
        string name,
        int? version,
        AccessContext access,
        CancellationToken cancellationToken) =>
        ReadValueCoreAsync(name, version, access, bypassPolicy: true, cancellationToken);

    public async Task<int> RotateMasterKeyAsync(
        string keyVersion,
        byte[] newKeyMaterial,
        CancellationToken cancellationToken)
    {
        if (newKeyMaterial.Length < 32)
        {
            throw new ApplicationValidationException("Master key material must contain at least 256 bits.");
        }

        keyProvider.RotateTo(keyVersion, newKeyMaterial);
        var secrets = await repository.ListSecretsAsync(cancellationToken);
        var rewrapped = 0;
        foreach (var version in secrets.SelectMany(x => x.Versions)
                     .Where(x => x.State != SecretVersionState.Destroyed))
        {
            var updated = cipher.RewrapDataEncryptionKey(
                version.WrappedDataEncryptionKey,
                version.KeyVersion);
            version.ReplaceWrappedDataEncryptionKey(updated.Value, updated.KeyVersion);
            rewrapped++;
        }

        await repository.SaveChangesAsync(cancellationToken);
        return rewrapped;
    }

    public async Task EmergencyRevokeAsync(
        string name,
        AccessContext access,
        CancellationToken cancellationToken)
    {
        var secret = await GetRequiredSecretAsync(name, cancellationToken);
        secret.RevokeAll(clock.UtcNow);
        await repository.AddAuditAsync(
            CreateAudit(secret, access, "secret.emergency-revoke", "allowed"),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task DestroyVersionAsync(
        string name,
        int version,
        AccessContext access,
        CancellationToken cancellationToken)
    {
        var secret = await GetRequiredSecretAsync(name, cancellationToken);
        secret.DestroyVersion(version, clock.UtcNow);
        await repository.AddAuditAsync(
            CreateAudit(secret, access, "secret.destroy-version", "allowed"),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task LinkConsumerAsync(
        string name,
        Guid consumerId,
        CancellationToken cancellationToken)
    {
        var secret = await GetRequiredSecretAsync(name, cancellationToken);
        if (await repository.GetConsumerAsync(consumerId, cancellationToken) is null)
        {
            throw new ResourceNotFoundException($"Consumer '{consumerId}' was not found.");
        }

        secret.LinkConsumer(consumerId);
        await repository.SaveChangesAsync(cancellationToken);
    }

    private async Task<SecretValueResult> ReadValueCoreAsync(
        string name,
        int? version,
        AccessContext access,
        bool bypassPolicy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(access.Reason))
        {
            throw new ApplicationValidationException("A reason is required for every value read.");
        }

        var secret = await GetRequiredSecretAsync(name, cancellationToken);
        var allowed = bypassPolicy;
        if (!bypassPolicy)
        {
            var policies = await repository.ListAccessPoliciesAsync(access.Actor, cancellationToken);
            allowed = policyEvaluator.IsAllowed(
                policies,
                access.Actor,
                secret.Name,
                SecretPermission.ReadValue);
        }

        if (!allowed)
        {
            await repository.AddAuditAsync(
                CreateAudit(secret, access, "secret.read-value", "denied"),
                cancellationToken);
            await repository.SaveChangesAsync(cancellationToken);
            metrics.ValueRead(secret.Environment, secret.Type, false);
            throw new ForbiddenOperationException("The actor is not permitted to read this secret path.");
        }

        var selected = secret.GetReadableVersion(version, clock.UtcNow);
        var value = cipher.Decrypt(selected.ToEncryptedPayload(), secret.ToBinding(selected.VersionNumber));
        redactionRegistry.Register(value);
        await repository.AddAuditAsync(
            CreateAudit(secret, access, "secret.read-value", "allowed"),
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        metrics.ValueRead(secret.Environment, secret.Type, true);
        return new SecretValueResult(
            secret.Name,
            selected.VersionNumber,
            SecretReference.Create(secret.Name, selected.VersionNumber).ToString(),
            value,
            selected.ExpiresAt);
    }

    private async Task<SecretRecord> GetRequiredSecretAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var normalized = SecretRecord.NormalizeName(name);
        return await repository.GetSecretAsync(normalized, cancellationToken)
               ?? throw new ResourceNotFoundException($"Secret '{normalized}' was not found.");
    }

    private AuditRecord CreateAudit(
        SecretRecord secret,
        AccessContext access,
        string action,
        string outcome) =>
        new(
            Guid.NewGuid(),
            secret.Id,
            access.Actor,
            action,
            secret.Name,
            access.Reason,
            access.CorrelationId,
            clock.UtcNow,
            outcome,
            access.SourceAddress,
            access.UserAgent);

    private static void ValidateRegistration(RegisterSecretCommand command)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            errors["name"] = ["Name is required."];
        }

        if (string.IsNullOrWhiteSpace(command.OwnerTeam))
        {
            errors["ownerTeam"] = ["Owner team is required."];
        }

        if (string.IsNullOrWhiteSpace(command.Environment))
        {
            errors["environment"] = ["Environment is required."];
        }

        if (command.RotationIntervalHours < 1)
        {
            errors["rotationIntervalHours"] = ["Rotation interval must be at least one hour."];
        }

        if (command.MaxAgeHours < 1)
        {
            errors["maxAgeHours"] = ["Maximum age must be at least one hour."];
        }

        if (command.GracePeriodHours < 0)
        {
            errors["gracePeriodHours"] = ["Grace period cannot be negative."];
        }

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException("Secret registration is invalid.", errors);
        }
    }
}
