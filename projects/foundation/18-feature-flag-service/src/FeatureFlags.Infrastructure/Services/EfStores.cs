using System.Text.Json;
using FeatureFlags.Application;
using FeatureFlags.Domain;
using FeatureFlags.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FeatureFlags.Infrastructure.Services;

public sealed class SqliteProjectEnvironmentStore(FeatureFlagDbContext db) : IProjectEnvironmentStore
{
    public async Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken cancellationToken) => await db.Projects.AsNoTracking()
        .OrderBy(project => project.Key)
        .Select(project => new ProjectInfo(project.Id, project.Key, project.Name, project.CreatedAt))
        .ToListAsync(cancellationToken);

    public async Task<ProjectInfo?> FindProjectAsync(string projectKey, CancellationToken cancellationToken) => await db.Projects.AsNoTracking()
        .Where(project => project.Key == projectKey)
        .Select(project => new ProjectInfo(project.Id, project.Key, project.Name, project.CreatedAt))
        .SingleOrDefaultAsync(cancellationToken);

    public async Task<ProjectInfo> CreateProjectAsync(string key, string name, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.Projects.AnyAsync(project => project.Key == key, cancellationToken))
        {
            throw new DomainValidationException([$"Project key '{key}' already exists."]);
        }

        var entity = new ProjectEntity { Id = Guid.NewGuid(), Key = key, Name = name, CreatedAt = now };
        db.Projects.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new ProjectInfo(entity.Id, entity.Key, entity.Name, entity.CreatedAt);
    }

    public async Task<StoredEnvironment?> FindEnvironmentAsync(string projectKey, string environmentKey, CancellationToken cancellationToken)
    {
        var entity = await db.Environments.Include(environment => environment.Project).AsNoTracking()
            .SingleOrDefaultAsync(environment => environment.Project.Key == projectKey && environment.Key == environmentKey, cancellationToken);
        return entity is null ? null : ToStored(entity);
    }

    public async Task<IReadOnlyList<EnvironmentInfo>> ListEnvironmentsAsync(string projectKey, CancellationToken cancellationToken) => await db.Environments
        .Include(environment => environment.Project).AsNoTracking()
        .Where(environment => environment.Project.Key == projectKey)
        .OrderBy(environment => environment.Key)
        .Select(environment => new EnvironmentInfo(environment.Id, environment.ProjectId, environment.Project.Key, environment.Key, environment.Name,
            string.Empty, string.Empty, environment.Version, environment.UpdatedAt))
        .ToListAsync(cancellationToken);

    public async Task<StoredEnvironment> CreateEnvironmentAsync(ProjectInfo project, string key, string name, string serverSdkKey, string clientSdkKey, EnvironmentConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.Environments.AnyAsync(environment => environment.ProjectId == project.Id && environment.Key == key, cancellationToken))
        {
            throw new DomainValidationException([$"Environment key '{key}' already exists in project '{project.Key}'."]);
        }

        var entity = new EnvironmentEntity
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Key = key, Name = name, ServerSdkKey = serverSdkKey, ClientSdkKey = clientSdkKey,
            Version = configuration.Version, ConfigurationJson = JsonSerializer.Serialize(configuration, FeatureFlagJson.Options), UpdatedAt = now
        };
        db.Environments.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new StoredEnvironment(new EnvironmentInfo(entity.Id, project.Id, project.Key, key, name, serverSdkKey, clientSdkKey, entity.Version, now), configuration);
    }

    public async Task SaveConfigurationAsync(EnvironmentInfo environment, EnvironmentConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entity = await db.Environments.SingleOrDefaultAsync(item => item.Id == environment.Id, cancellationToken)
            ?? throw new NotFoundException("Environment");
        entity.Version = configuration.Version;
        entity.ConfigurationJson = JsonSerializer.Serialize(configuration, FeatureFlagJson.Options);
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static StoredEnvironment ToStored(EnvironmentEntity entity)
    {
        var configuration = JsonSerializer.Deserialize<EnvironmentConfiguration>(entity.ConfigurationJson, FeatureFlagJson.Options)
            ?? throw new InvalidOperationException("Persisted environment configuration is invalid.");
        var environment = new EnvironmentInfo(entity.Id, entity.ProjectId, entity.Project.Key, entity.Key, entity.Name,
            entity.ServerSdkKey, entity.ClientSdkKey, entity.Version, entity.UpdatedAt);
        return new StoredEnvironment(environment, configuration);
    }
}

public sealed class EfAuditStore(FeatureFlagDbContext db) : IAuditStore
{
    public async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        db.AuditEntries.Add(new AuditEntryEntity
        {
            Id = record.Id, ProjectKey = record.ProjectKey, EnvironmentKey = record.EnvironmentKey, Actor = record.Actor, Action = record.Action,
            Resource = record.Resource, BeforeJson = record.BeforeJson, AfterJson = record.AfterJson, DiffJson = record.DiffJson,
            Comment = record.Comment, TicketReference = record.TicketReference, CorrelationId = record.CorrelationId, SourceIp = record.SourceIp,
            UserAgent = record.UserAgent, OccurredAt = record.OccurredAt
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditRecord>> ListAsync(string projectKey, string environmentKey, CancellationToken cancellationToken) => (await db.AuditEntries.AsNoTracking()
        .Where(item => item.ProjectKey == projectKey && item.EnvironmentKey == environmentKey)
        .ToListAsync(cancellationToken)).OrderByDescending(item => item.OccurredAt).Select(ToModel).ToArray();

    public async Task<AuditRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.AuditEntries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : ToModel(entity);
    }

    private static AuditRecord ToModel(AuditEntryEntity item) => new(item.Id, item.ProjectKey, item.EnvironmentKey, item.Actor, item.Action,
        item.Resource, item.BeforeJson, item.AfterJson, item.DiffJson, item.Comment, item.TicketReference, item.CorrelationId, item.SourceIp,
        item.UserAgent, item.OccurredAt);
}

public sealed class EfApprovalStore(FeatureFlagDbContext db) : IApprovalStore
{
    public async Task AddAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        db.ApprovalRequests.Add(ToEntity(request));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ApprovalRequest?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<IReadOnlyList<ApprovalRequest>> ListAsync(string projectKey, string environmentKey, CancellationToken cancellationToken) =>
        (await db.ApprovalRequests.AsNoTracking().Where(item => item.ProjectKey == projectKey && item.EnvironmentKey == environmentKey)
            .ToListAsync(cancellationToken)).OrderByDescending(item => item.RequestedAt).Select(ToModel).ToArray();

    public async Task UpdateAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var entity = await db.ApprovalRequests.SingleOrDefaultAsync(item => item.Id == request.Id, cancellationToken) ?? throw new NotFoundException("Approval request");
        db.Entry(entity).CurrentValues.SetValues(ToEntity(request));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ApprovalRequestEntity ToEntity(ApprovalRequest item) => new()
    {
        Id = item.Id, ProjectKey = item.ProjectKey, EnvironmentKey = item.EnvironmentKey, Resource = item.Resource,
        ProposedConfigurationJson = item.ProposedConfigurationJson, RequestedBy = item.RequestedBy, RequestComment = item.RequestComment,
        Status = item.Status, ReviewedBy = item.ReviewedBy, ReviewComment = item.ReviewComment, RequestedAt = item.RequestedAt,
        ReviewedAt = item.ReviewedAt, AppliedAt = item.AppliedAt
    };

    private static ApprovalRequest ToModel(ApprovalRequestEntity item) => new(item.Id, item.ProjectKey, item.EnvironmentKey, item.Resource,
        item.ProposedConfigurationJson, item.RequestedBy, item.RequestComment, item.Status, item.ReviewedBy, item.ReviewComment,
        item.RequestedAt, item.ReviewedAt, item.AppliedAt);
}
