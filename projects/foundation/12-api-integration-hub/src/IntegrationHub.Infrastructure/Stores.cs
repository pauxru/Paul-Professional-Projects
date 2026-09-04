using IntegrationHub.Application;
using IntegrationHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}

public sealed class EfFlowStore(
    IntegrationHubDbContext db,
    IClock clock,
    IIdGenerator ids,
    IFlowDefinitionParser parser) : IFlowStore
{
    public async Task<IntegrationFlow> CreateAsync(
        string name,
        string format,
        string definition,
        string actor,
        CancellationToken cancellationToken)
    {
        parser.Parse(format, definition);
        var flow = new IntegrationFlow(ids.NewId(), name);
        var version = flow.AddVersion(format.ToLowerInvariant(), definition, clock.UtcNow, actor);
        var entity = new FlowEntity
        {
            Id = flow.Id,
            Name = flow.Name,
            Versions =
            [
                new FlowVersionEntity
                {
                    FlowId = flow.Id,
                    Version = version.Version,
                    Format = version.Format,
                    Definition = version.Definition,
                    CreatedAt = version.CreatedAt,
                    CreatedBy = version.CreatedBy
                }
            ]
        };
        db.Flows.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return flow;
    }

    public async Task<FlowVersion> AddVersionAsync(
        Guid flowId,
        string format,
        string definition,
        string actor,
        CancellationToken cancellationToken)
    {
        parser.Parse(format, definition);
        var entity = await db.Flows.Include(x => x.Versions)
                         .SingleOrDefaultAsync(x => x.Id == flowId, cancellationToken)
                     ?? throw new KeyNotFoundException("Flow not found.");
        var number = entity.Versions.Count == 0 ? 1 : entity.Versions.Max(x => x.Version) + 1;
        var version = new FlowVersion(number, format.ToLowerInvariant(), definition, clock.UtcNow, actor);
        entity.Versions.Add(new FlowVersionEntity
        {
            FlowId = flowId,
            Version = version.Version,
            Format = version.Format,
            Definition = version.Definition,
            CreatedAt = version.CreatedAt,
            CreatedBy = version.CreatedBy
        });
        await db.SaveChangesAsync(cancellationToken);
        return version;
    }

    public async Task ActivateAsync(Guid flowId, int version, CancellationToken cancellationToken)
    {
        var entity = await db.Flows.Include(x => x.Versions)
                         .SingleOrDefaultAsync(x => x.Id == flowId, cancellationToken)
                     ?? throw new KeyNotFoundException("Flow not found.");
        if (entity.Versions.All(x => x.Version != version))
        {
            throw new DomainValidationException($"Flow version {version} does not exist.");
        }
        entity.ActiveVersion = version;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RollbackAsync(Guid flowId, CancellationToken cancellationToken)
    {
        var entity = await db.Flows.Include(x => x.Versions)
                         .SingleOrDefaultAsync(x => x.Id == flowId, cancellationToken)
                     ?? throw new KeyNotFoundException("Flow not found.");
        if (entity.ActiveVersion is null)
        {
            throw new DomainValidationException("The flow has no active version.");
        }
        var previous = entity.Versions.Where(x => x.Version < entity.ActiveVersion).MaxBy(x => x.Version)
                       ?? throw new DomainValidationException("There is no previous version to activate.");
        entity.ActiveVersion = previous.Version;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IntegrationFlow?> GetAsync(Guid flowId, CancellationToken cancellationToken)
    {
        var entity = await db.Flows.AsNoTracking().Include(x => x.Versions)
            .SingleOrDefaultAsync(x => x.Id == flowId, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyList<IntegrationFlow>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Flows.AsNoTracking().Include(x => x.Versions).OrderBy(x => x.Name)
            .ToListAsync(cancellationToken)).Select(ToDomain).ToArray();

    private static IntegrationFlow ToDomain(FlowEntity entity)
    {
        var flow = new IntegrationFlow(entity.Id, entity.Name);
        foreach (var version in entity.Versions.OrderBy(x => x.Version))
        {
            flow.AddVersion(version.Format, version.Definition, version.CreatedAt, version.CreatedBy);
        }
        if (entity.ActiveVersion is int active)
        {
            flow.Activate(active);
        }
        return flow;
    }
}

public sealed class EfExecutionStore(IntegrationHubDbContext db, IClock clock) : IExecutionStore
{
    public async Task CreateRunAsync(RunSnapshot run, CancellationToken cancellationToken)
    {
        db.Runs.Add(new RunEntity
        {
            Id = run.Id,
            FlowId = run.FlowId,
            FlowVersion = run.FlowVersion,
            Status = run.Status,
            CorrelationId = run.CorrelationId,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            RecordsProcessed = run.RecordsProcessed,
            Error = run.Error
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AppendStepAsync(StepSnapshot step, CancellationToken cancellationToken)
    {
        db.Steps.Add(new StepEntity
        {
            Id = step.Id,
            RunId = step.RunId,
            StepId = step.StepId,
            Kind = step.Kind,
            StartedAt = step.StartedAt,
            CompletedAt = step.CompletedAt,
            InputSnapshot = step.InputSnapshot,
            OutputSnapshot = step.OutputSnapshot,
            RecordsIn = step.RecordsIn,
            RecordsOut = step.RecordsOut,
            Error = step.Error
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteRunAsync(
        Guid runId,
        RunStatus status,
        int recordsProcessed,
        string? error,
        CancellationToken cancellationToken)
    {
        var run = await db.Runs.SingleAsync(x => x.Id == runId, cancellationToken);
        run.Status = status;
        run.CompletedAt = clock.UtcNow;
        run.RecordsProcessed = recordsProcessed;
        run.Error = error;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RunSnapshot>> SearchRunsAsync(RunSearch search, CancellationToken cancellationToken)
    {
        var query = db.Runs.AsNoTracking().AsQueryable();
        if (search.Status is { } status)
        {
            query = query.Where(x => x.Status == status);
        }
        if (search.FlowId is { } flowId)
        {
            query = query.Where(x => x.FlowId == flowId);
        }
        if (!string.IsNullOrWhiteSpace(search.CorrelationId))
        {
            query = query.Where(x => x.CorrelationId == search.CorrelationId);
        }
        var rows = await query.ToListAsync(cancellationToken);
        return rows.OrderByDescending(x => x.StartedAt)
                .Skip((Math.Max(search.Page, 1) - 1) * Math.Clamp(search.PageSize, 1, 200))
                .Take(Math.Clamp(search.PageSize, 1, 200))
            .Select(ToSnapshot).ToArray();
    }

    public async Task<RunDetails?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }
        return new RunDetails(ToSnapshot(run), run.Steps.OrderBy(x => x.StartedAt).Select(x => new StepSnapshot(
            x.Id, x.RunId, x.StepId, x.Kind, x.StartedAt, x.CompletedAt, x.InputSnapshot, x.OutputSnapshot,
            x.RecordsIn, x.RecordsOut, x.Error)).ToArray());
    }

    public async Task PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var candidates = await db.Runs.Where(x => x.CompletedAt != null).ToListAsync(cancellationToken);
        var old = candidates.Where(x => x.CompletedAt < cutoff).ToArray();
        db.Runs.RemoveRange(old);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static RunSnapshot ToSnapshot(RunEntity x) =>
        new(x.Id, x.FlowId, x.FlowVersion, x.Status, x.CorrelationId, x.StartedAt, x.CompletedAt, x.RecordsProcessed, x.Error);
}

public sealed class EfDeadLetterStore(IntegrationHubDbContext db) : IDeadLetterStore
{
    public async Task<DeadLetterItem> AddAsync(DeadLetterItem item, CancellationToken cancellationToken)
    {
        var existing = await db.DeadLetters.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.RunId == item.RunId && x.StepId == item.StepId && x.RecordKey == item.RecordKey,
                cancellationToken);
        if (existing is not null)
        {
            return ToDomain(existing);
        }

        db.DeadLetters.Add(new DeadLetterEntity
        {
            Id = item.Id,
            RunId = item.RunId,
            StepId = item.StepId,
            BatchKey = item.BatchKey,
            RecordKey = item.RecordKey,
            Payload = item.Payload,
            Error = item.Error,
            Status = item.Status,
            CreatedAt = item.CreatedAt,
            ReplayRunId = item.ReplayRunId
        });
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<DeadLetterItem>> ListAsync(DeadLetterStatus? status, CancellationToken cancellationToken)
    {
        var query = db.DeadLetters.AsNoTracking().AsQueryable();
        if (status is { } selected)
        {
            query = query.Where(x => x.Status == selected);
        }
        return (await query.Take(500).ToListAsync(cancellationToken)).OrderByDescending(x => x.CreatedAt)
            .Select(ToDomain).ToArray();
    }

    public async Task<DeadLetterItem?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.DeadLetters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task MarkReplayedAsync(Guid id, Guid replayRunId, CancellationToken cancellationToken)
    {
        var entity = await db.DeadLetters.SingleAsync(x => x.Id == id, cancellationToken);
        entity.Status = DeadLetterStatus.Replayed;
        entity.ReplayRunId = replayRunId;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeadLetterItem>> GetForReplayAsync(
        Guid? itemId,
        Guid? runId,
        string? batchKey,
        CancellationToken cancellationToken)
    {
        var query = db.DeadLetters.AsNoTracking().Where(x => x.Status == DeadLetterStatus.Pending);
        if (itemId is { } id)
        {
            query = query.Where(x => x.Id == id);
        }
        if (runId is { } run)
        {
            query = query.Where(x => x.RunId == run);
        }
        if (!string.IsNullOrWhiteSpace(batchKey))
        {
            query = query.Where(x => x.BatchKey == batchKey);
        }
        return (await query.ToListAsync(cancellationToken)).OrderBy(x => x.CreatedAt).Select(ToDomain).ToArray();
    }

    private static DeadLetterItem ToDomain(DeadLetterEntity x) =>
        new(x.Id, x.RunId, x.StepId, x.BatchKey, x.RecordKey, x.Payload, x.Error, x.Status, x.CreatedAt, x.ReplayRunId);
}

public sealed class EfCheckpointStore(IntegrationHubDbContext db) : ICheckpointStore
{
    public async Task<int> GetNextIndexAsync(Guid runId, string stepId, string batchKey, CancellationToken cancellationToken) =>
        (await db.Checkpoints.AsNoTracking().SingleOrDefaultAsync(
            x => x.RunId == runId && x.StepId == stepId && x.BatchKey == batchKey,
            cancellationToken))?.NextIndex ?? 0;

    public async Task SaveAsync(Guid runId, string stepId, string batchKey, int nextIndex, CancellationToken cancellationToken)
    {
        var entity = await db.Checkpoints.SingleOrDefaultAsync(
            x => x.RunId == runId && x.StepId == stepId && x.BatchKey == batchKey,
            cancellationToken);
        if (entity is null)
        {
            db.Checkpoints.Add(new CheckpointEntity
            {
                RunId = runId,
                StepId = stepId,
                BatchKey = batchKey,
                NextIndex = nextIndex
            });
        }
        else
        {
            entity.NextIndex = Math.Max(entity.NextIndex, nextIndex);
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfIdempotencyStore(IntegrationHubDbContext db) : IIdempotencyStore
{
    public async Task<IdempotencyResult?> GetAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var entity = await db.IdempotencyKeys.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Scope == scope && x.Key == key, cancellationToken);
        return entity is null ? null : new IdempotencyResult(entity.StatusCode, entity.Payload, entity.CreatedAt);
    }

    public async Task<bool> TryStoreAsync(
        string scope,
        string key,
        IdempotencyResult result,
        CancellationToken cancellationToken)
    {
        if (await db.IdempotencyKeys.AnyAsync(x => x.Scope == scope && x.Key == key, cancellationToken))
        {
            return false;
        }
        db.IdempotencyKeys.Add(new IdempotencyEntity
        {
            Scope = scope,
            Key = key,
            StatusCode = result.StatusCode,
            Payload = result.Payload,
            CreatedAt = result.CreatedAt
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

public sealed class EfContractDriftStore(IntegrationHubDbContext db) : IContractDriftStore
{
    public async Task AddAsync(ContractDriftAlert alert, CancellationToken cancellationToken)
    {
        if (await db.DriftAlerts.AnyAsync(
                x => !x.Resolved
                     && x.ConnectorId == alert.ConnectorId
                     && x.Operation == alert.Operation
                     && x.FieldPath == alert.FieldPath
                     && x.Change == alert.Change,
                cancellationToken))
        {
            return;
        }
        db.DriftAlerts.Add(new DriftAlertEntity
        {
            Id = alert.Id,
            ConnectorId = alert.ConnectorId,
            Operation = alert.Operation,
            FieldPath = alert.FieldPath,
            Change = alert.Change,
            DetectedAt = alert.DetectedAt,
            Resolved = alert.Resolved
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContractDriftAlert>> ListAsync(bool unresolvedOnly, CancellationToken cancellationToken)
    {
        var query = db.DriftAlerts.AsNoTracking().AsQueryable();
        if (unresolvedOnly)
        {
            query = query.Where(x => !x.Resolved);
        }
        return (await query.ToListAsync(cancellationToken)).OrderByDescending(x => x.DetectedAt)
            .Select(x => new ContractDriftAlert(
                x.Id, x.ConnectorId, x.Operation, x.FieldPath, x.Change, x.DetectedAt, x.Resolved))
            .ToArray();
    }
}

public sealed class EfWebhookNonceStore(IntegrationHubDbContext db, IClock clock) : IWebhookNonceStore
{
    public async Task<bool> TryUseAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var nonceRows = await db.WebhookNonces.ToListAsync(cancellationToken);
        db.WebhookNonces.RemoveRange(nonceRows.Where(x => x.ExpiresAt < clock.UtcNow));
        if (await db.WebhookNonces.AnyAsync(x => x.Nonce == nonce, cancellationToken))
        {
            return false;
        }
        db.WebhookNonces.Add(new WebhookNonceEntity { Nonce = nonce, ExpiresAt = expiresAt });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
