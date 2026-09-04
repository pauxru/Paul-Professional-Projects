using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Application;

public sealed class RotationEngineOptions
{
    public TimeSpan AcknowledgementTimeout { get; set; } = TimeSpan.FromMinutes(15);
}

public interface IRotationStrategy
{
    RotationStrategyKind Kind { get; }
    bool RequiresAcknowledgements { get; }
    bool CanEnterPromotion(RotationOperation rotation, DateTimeOffset now);
    void Promote(SecretRecord secret, int newVersion, DateTimeOffset now);
    void Rollback(SecretRecord secret, int newVersion, DateTimeOffset now);
}

public sealed class DualWriteRotationStrategy : IRotationStrategy
{
    public RotationStrategyKind Kind => RotationStrategyKind.DualWrite;
    public bool RequiresAcknowledgements => true;

    public bool CanEnterPromotion(RotationOperation rotation, DateTimeOffset now) =>
        rotation.AllRequiredConsumersAcknowledged;

    public void Promote(SecretRecord secret, int newVersion, DateTimeOffset now) =>
        secret.Promote(newVersion, now);

    public void Rollback(SecretRecord secret, int newVersion, DateTimeOffset now) =>
        secret.Rollback(newVersion, now);
}

public sealed class SingleCutoverRotationStrategy : IRotationStrategy
{
    public RotationStrategyKind Kind => RotationStrategyKind.SingleCutover;
    public bool RequiresAcknowledgements => false;

    public bool CanEnterPromotion(RotationOperation rotation, DateTimeOffset now) =>
        rotation.MaintenanceWindowStart is not null && now >= rotation.MaintenanceWindowStart;

    public void Promote(SecretRecord secret, int newVersion, DateTimeOffset now) =>
        secret.Promote(newVersion, now);

    public void Rollback(SecretRecord secret, int newVersion, DateTimeOffset now) =>
        secret.Rollback(newVersion, now);
}

public sealed class RotationEngine(
    ISecretsRepository repository,
    ISecretGeneratorRegistry generators,
    ISecretCipher cipher,
    ISecretVerifier verifier,
    IEnumerable<IRotationStrategy> strategies,
    IEnumerable<INotificationChannel> notificationChannels,
    ISecretRedactionRegistry redactionRegistry,
    IPlatformMetrics metrics,
    IClock clock,
    RotationEngineOptions options)
{
    private readonly IReadOnlyDictionary<RotationStrategyKind, IRotationStrategy> _strategies =
        strategies.ToDictionary(x => x.Kind);
    private readonly IReadOnlyList<INotificationChannel> _notificationChannels =
        notificationChannels.ToArray();

    public async Task<RotationOperation> RequestAsync(
        RequestRotationCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            throw new ApplicationValidationException("An idempotency key is required.");
        }

        var existing = await repository.FindRotationByIdempotencyKeyAsync(
            command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var secret = await repository.GetSecretAsync(
            SecretRecord.NormalizeName(command.SecretName), cancellationToken)
            ?? throw new ResourceNotFoundException($"Secret '{command.SecretName}' was not found.");
        var rotations = await repository.ListRotationsAsync(cancellationToken);
        if (rotations.Any(x => x.SecretId == secret.Id && !x.IsTerminal))
        {
            throw new ConflictException("A rotation is already active for this secret.");
        }

        if (!_strategies.ContainsKey(command.Strategy))
        {
            throw new ApplicationValidationException($"Rotation strategy '{command.Strategy}' is unavailable.");
        }

        if (command.Strategy == RotationStrategyKind.SingleCutover &&
            command.MaintenanceWindowStart is null)
        {
            throw new ApplicationValidationException(
                "Single-cutover rotation requires a maintenance window.");
        }

        var now = clock.UtcNow;
        var rotation = new RotationOperation(
            Guid.NewGuid(),
            secret.Id,
            command.Strategy,
            command.IdempotencyKey,
            command.RequestedBy,
            command.CorrelationId,
            now,
            now.Add(options.AcknowledgementTimeout),
            command.MaintenanceWindowStart);
        await repository.AddRotationAsync(rotation, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return rotation;
    }

    public async Task<RotationOperation> AdvanceOneStepAsync(
        Guid rotationId,
        CancellationToken cancellationToken)
    {
        var rotation = await GetRequiredRotationAsync(rotationId, cancellationToken);
        if (rotation.IsTerminal)
        {
            return rotation;
        }

        var secret = await repository.GetSecretByIdAsync(rotation.SecretId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Secret '{rotation.SecretId}' was not found.");
        var strategy = _strategies[rotation.Strategy];
        var now = clock.UtcNow;

        if (rotation.CancellationRequested)
        {
            Rollback(secret, rotation, strategy, "Rotation was cancelled.", now);
            await repository.SaveChangesAsync(cancellationToken);
            return rotation;
        }

        try
        {
            switch (rotation.State)
            {
                case RotationState.Requested:
                    rotation.TransitionTo(RotationState.Generating, now);
                    break;

                case RotationState.Generating:
                    var generated = generators.Generate(secret.Type, now);
                    redactionRegistry.Register(generated.Value);
                    var nextVersion = secret.Versions.Count == 0
                        ? 1
                        : secret.Versions.Max(x => x.VersionNumber) + 1;
                    var payload = cipher.Encrypt(generated.Value, secret.ToBinding(nextVersion));
                    var staged = secret.StageVersion(
                        payload.Ciphertext,
                        payload.Nonce,
                        payload.AuthenticationTag,
                        payload.WrappedDataEncryptionKey,
                        payload.KeyVersion,
                        now,
                        now.Add(secret.MaxAge));
                    await repository.AddSecretVersionAsync(staged, cancellationToken);
                    rotation.SetGeneratedVersion(staged.VersionNumber, secret.CurrentVersion?.VersionNumber);
                    rotation.TransitionTo(RotationState.StagedNewVersion, now);
                    break;

                case RotationState.StagedNewVersion:
                    rotation.InitializeAcknowledgements(
                        secret.Consumers.Select(x => x.ConsumerId),
                        strategy.RequiresAcknowledgements);
                    foreach (var acknowledgement in rotation.Acknowledgements)
                    {
                        await repository.AddConsumerAcknowledgementAsync(
                            acknowledgement, cancellationToken);
                    }
                    rotation.TransitionTo(RotationState.NotifyingConsumers, now);
                    break;

                case RotationState.NotifyingConsumers:
                    await NotifyConsumersAsync(secret, rotation, cancellationToken);
                    rotation.TransitionTo(RotationState.AwaitingAcknowledgement, clock.UtcNow);
                    break;

                case RotationState.AwaitingAcknowledgement:
                    if (strategy.RequiresAcknowledgements &&
                        !rotation.AllRequiredConsumersAcknowledged &&
                        now >= rotation.AcknowledgementDeadline)
                    {
                        rotation.MarkTimedOut();
                        Rollback(
                            secret,
                            rotation,
                            strategy,
                            "Consumer acknowledgement deadline elapsed.",
                            now);
                    }
                    else if (strategy.CanEnterPromotion(rotation, now))
                    {
                        rotation.TransitionTo(RotationState.Promoting, now);
                    }
                    break;

                case RotationState.Promoting:
                    rotation.TransitionTo(RotationState.Verifying, now);
                    break;

                case RotationState.Verifying:
                    await VerifyAndPromoteAsync(secret, rotation, strategy, cancellationToken);
                    break;
            }

            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is not ApplicationValidationException &&
            exception is not ResourceNotFoundException)
        {
            if (rotation.NewVersionNumber is { } generatedVersion)
            {
                strategy.Rollback(secret, generatedVersion, now);
            }

            rotation.MarkFailed($"Rotation step failed: {exception.GetType().Name}", now);
            metrics.RotationCompleted(rotation.State, rotation.Strategy);
            await repository.SaveChangesAsync(cancellationToken);
        }

        return rotation;
    }

    public async Task<RotationOperation> RunToPauseOrTerminalAsync(
        Guid rotationId,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < 16; index++)
        {
            var before = await GetRequiredRotationAsync(rotationId, cancellationToken);
            var previousState = before.State;
            var advanced = await AdvanceOneStepAsync(rotationId, cancellationToken);
            if (advanced.IsTerminal || advanced.State == previousState)
            {
                return advanced;
            }
        }

        throw new InvalidOperationException("Rotation exceeded the maximum transition count.");
    }

    public async Task<RotationOperation> AcknowledgeAsync(
        Guid rotationId,
        Guid consumerId,
        CancellationToken cancellationToken)
    {
        var rotation = await GetRequiredRotationAsync(rotationId, cancellationToken);
        var now = clock.UtcNow;
        rotation.Acknowledge(consumerId, now);
        var acknowledgement = rotation.Acknowledgements.Single(x => x.ConsumerId == consumerId);
        if (acknowledgement.NotificationSentAt is { } sentAt)
        {
            metrics.AcknowledgementObserved(now - sentAt);
        }

        await repository.SaveChangesAsync(cancellationToken);
        return rotation;
    }

    public async Task<RotationOperation> CancelAsync(
        Guid rotationId,
        CancellationToken cancellationToken)
    {
        var rotation = await GetRequiredRotationAsync(rotationId, cancellationToken);
        rotation.RequestCancellation(clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        return await AdvanceOneStepAsync(rotationId, cancellationToken);
    }

    public async Task<RotationOperation> RollbackAsync(
        Guid rotationId,
        string reason,
        CancellationToken cancellationToken)
    {
        var rotation = await GetRequiredRotationAsync(rotationId, cancellationToken);
        if (rotation.State == RotationState.RolledBack)
        {
            return rotation;
        }

        var secret = await repository.GetSecretByIdAsync(rotation.SecretId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Secret '{rotation.SecretId}' was not found.");
        var strategy = _strategies[rotation.Strategy];
        Rollback(secret, rotation, strategy, reason, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        return rotation;
    }

    public async Task<RotationOperation> GetAsync(
        Guid rotationId,
        CancellationToken cancellationToken) =>
        await GetRequiredRotationAsync(rotationId, cancellationToken);

    public Task<IReadOnlyList<RotationOperation>> ListAsync(CancellationToken cancellationToken) =>
        repository.ListRotationsAsync(cancellationToken);

    private async Task NotifyConsumersAsync(
        SecretRecord secret,
        RotationOperation rotation,
        CancellationToken cancellationToken)
    {
        if (rotation.NewVersionNumber is null)
        {
            throw new DomainRuleException("The rotation has no staged version.");
        }

        foreach (var acknowledgement in rotation.Acknowledgements.Where(x =>
                     x.NotificationSentAt is null))
        {
            var consumer = await repository.GetConsumerAsync(
                acknowledgement.ConsumerId, cancellationToken);
            if (consumer is null)
            {
                continue;
            }

            var notice = new RotationNotice(
                rotation.Id,
                consumer.Id,
                secret.Name,
                SecretReference.Create(secret.Name, rotation.NewVersionNumber.Value).ToString(),
                rotation.Strategy,
                rotation.AcknowledgementDeadline,
                rotation.MaintenanceWindowStart,
                rotation.CorrelationId);
            var results = new List<NotificationDeliveryResult>();
            foreach (var channel in _notificationChannels)
            {
                results.Add(await channel.SendAsync(consumer, notice, cancellationToken));
            }

            if (results.Count == 0 || results.Any(x => x.Accepted))
            {
                rotation.MarkNotificationSent(consumer.Id, clock.UtcNow);
            }
        }
    }

    private async Task VerifyAndPromoteAsync(
        SecretRecord secret,
        RotationOperation rotation,
        IRotationStrategy strategy,
        CancellationToken cancellationToken)
    {
        var versionNumber = rotation.NewVersionNumber
            ?? throw new DomainRuleException("The rotation has no candidate version.");
        var candidate = secret.Versions.Single(x => x.VersionNumber == versionNumber);
        var value = cipher.Decrypt(candidate.ToEncryptedPayload(), secret.ToBinding(versionNumber));
        var verification = await verifier.VerifyAsync(secret, candidate, value, cancellationToken);
        if (!verification.Succeeded)
        {
            Rollback(secret, rotation, strategy, verification.Detail, clock.UtcNow);
            return;
        }

        strategy.Promote(secret, versionNumber, clock.UtcNow);
        rotation.TransitionTo(RotationState.Completed, clock.UtcNow);
        metrics.RotationCompleted(rotation.State, rotation.Strategy);
    }

    private void Rollback(
        SecretRecord secret,
        RotationOperation rotation,
        IRotationStrategy strategy,
        string reason,
        DateTimeOffset now)
    {
        if (rotation.NewVersionNumber is { } newVersion)
        {
            strategy.Rollback(secret, newVersion, now);
        }

        rotation.MarkRolledBack(reason, now);
        metrics.RotationCompleted(rotation.State, rotation.Strategy);
    }

    private async Task<RotationOperation> GetRequiredRotationAsync(
        Guid rotationId,
        CancellationToken cancellationToken) =>
        await repository.GetRotationAsync(rotationId, cancellationToken)
        ?? throw new ResourceNotFoundException($"Rotation '{rotationId}' was not found.");
}
