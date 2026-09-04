namespace FieldOps.Domain;

public sealed class Asset : ITenantOwned
{
    private Asset()
    {
    }

    public Asset(
        Guid tenantId,
        string assetTag,
        string name,
        string category,
        string location,
        DateOnly? nextMaintenanceDate)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        AssetTag = Required(assetTag, "Asset tag");
        Name = Required(name, "Asset name");
        Category = Required(category, "Category");
        Location = Required(location, "Location");
        Status = AssetStatus.Active;
        NextMaintenanceDate = nextMaintenanceDate;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string AssetTag { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public string Location { get; private set; } = string.Empty;
    public AssetStatus Status { get; private set; }
    public DateOnly? NextMaintenanceDate { get; private set; }

    public void SetStatus(AssetStatus status) => Status = status;

    private static string Required(string value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainRuleException($"{field} is required.") : value.Trim();
}

public sealed class Job : ITenantOwned
{
    private static readonly IReadOnlyDictionary<JobStatus, JobStatus[]> AllowedTransitions =
        new Dictionary<JobStatus, JobStatus[]>
        {
            [JobStatus.Draft] = [JobStatus.Scheduled, JobStatus.Cancelled],
            [JobStatus.Scheduled] = [JobStatus.Dispatched, JobStatus.Cancelled],
            [JobStatus.Dispatched] = [JobStatus.InProgress, JobStatus.Cancelled, JobStatus.Failed],
            [JobStatus.InProgress] = [JobStatus.Completed, JobStatus.Cancelled, JobStatus.Failed],
            [JobStatus.Completed] = [],
            [JobStatus.Cancelled] = [],
            [JobStatus.Failed] = []
        };

    private Job()
    {
    }

    public Job(
        Guid tenantId,
        string title,
        string description,
        JobPriority priority,
        DateTimeOffset scheduleStart,
        DateTimeOffset scheduleEnd,
        DateTimeOffset slaDueAt,
        DateTimeOffset createdAt,
        Guid? assetId = null)
    {
        if (scheduleEnd <= scheduleStart) throw new DomainRuleException("Schedule end must be after schedule start.");
        if (slaDueAt < scheduleStart) throw new DomainRuleException("SLA due date cannot precede the schedule.");
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Title = string.IsNullOrWhiteSpace(title) ? throw new DomainRuleException("Job title is required.") : title.Trim();
        Description = description?.Trim() ?? string.Empty;
        Priority = priority;
        ScheduleStart = scheduleStart;
        ScheduleEnd = scheduleEnd;
        SlaDueAt = slaDueAt;
        CreatedAt = createdAt;
        AssetId = assetId;
        Status = JobStatus.Draft;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public JobPriority Priority { get; private set; }
    public JobStatus Status { get; private set; }
    public DateTimeOffset ScheduleStart { get; private set; }
    public DateTimeOffset ScheduleEnd { get; private set; }
    public DateTimeOffset SlaDueAt { get; private set; }
    public bool IsSlaBreached { get; private set; }
    public Guid? AssigneeUserId { get; private set; }
    public Guid? AssetId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static bool CanTransition(JobStatus from, JobStatus to) =>
        AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public void Assign(Guid assigneeUserId)
    {
        if (assigneeUserId == Guid.Empty) throw new DomainRuleException("Assignee is required.");
        AssigneeUserId = assigneeUserId;
    }

    public void TransitionTo(JobStatus next, DateTimeOffset now)
    {
        if (!CanTransition(Status, next))
        {
            throw new DomainRuleException($"Transition from {Status} to {next} is not allowed.");
        }

        Status = next;
        IsSlaBreached = now > SlaDueAt && next is not JobStatus.Cancelled;
        if (next == JobStatus.Completed)
        {
            CompletedAt = now;
        }
    }

    public void RefreshSla(DateTimeOffset now) =>
        IsSlaBreached = now > SlaDueAt && Status is not (JobStatus.Completed or JobStatus.Cancelled);
}

public sealed class JobAttachment : ITenantOwned
{
    private JobAttachment()
    {
    }

    public JobAttachment(Guid tenantId, Guid jobId, string objectKey, string fileName, string contentType, long size, DateTimeOffset uploadedAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        JobId = jobId;
        ObjectKey = objectKey;
        FileName = fileName;
        ContentType = contentType;
        Size = size;
        UploadedAt = uploadedAt;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public Guid JobId { get; private set; }
    public string ObjectKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }
}
