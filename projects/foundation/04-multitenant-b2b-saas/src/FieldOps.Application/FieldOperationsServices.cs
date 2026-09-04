using FieldOps.Domain;

namespace FieldOps.Application;

public static class TenantGuard
{
    public static void EnsureSameTenant(Guid ambientTenantId, ITenantOwned entity)
    {
        if (ambientTenantId == Guid.Empty || entity.TenantId != ambientTenantId)
        {
            throw new CrossTenantAccessException("Cross-tenant aggregate access was rejected.");
        }
    }

    public static void EnsureSameTenant(Guid ambientTenantId, params ITenantOwned[] entities)
    {
        foreach (var entity in entities)
        {
            EnsureSameTenant(ambientTenantId, entity);
        }
    }
}

public sealed class JobApplicationService(
    ITenantContext tenantContext,
    IJobRepository jobs,
    IClock clock)
{
    public Task<Job?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        jobs.FindAsync(id, cancellationToken);

    public async Task<Job> CreateAsync(
        string title,
        string description,
        JobPriority priority,
        DateTimeOffset scheduleStart,
        DateTimeOffset scheduleEnd,
        DateTimeOffset slaDueAt,
        Asset? asset,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequiredTenantId;
        if (asset is not null)
        {
            TenantGuard.EnsureSameTenant(tenantId, asset);
        }

        var job = new Job(
            tenantId, title, description, priority, scheduleStart, scheduleEnd, slaDueAt, clock.UtcNow, asset?.Id);
        await jobs.AddAsync(job, cancellationToken);
        await jobs.SaveAsync(cancellationToken);
        return job;
    }

    public async Task<Job> TransitionAsync(Guid id, JobStatus status, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Job was not found.");
        TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, job);
        job.TransitionTo(status, clock.UtcNow);
        await jobs.SaveAsync(cancellationToken);
        return job;
    }

    public async Task<Job> AssignAsync(Guid id, Membership assignee, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Job was not found.");
        TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, job, assignee);
        job.Assign(assignee.UserId);
        await jobs.SaveAsync(cancellationToken);
        return job;
    }
}

public sealed record InspectionAnswerInput(Guid ItemId, string? Value);
public sealed record InspectionEvaluation(decimal Score, bool Passed, IReadOnlyList<(Guid ItemId, bool Passed, string? Value)> Answers);

public sealed class InspectionService(ITenantContext tenantContext, IInspectionRepository repository, IClock clock)
{
    public InspectionEvaluation Evaluate(InspectionTemplate template, IReadOnlyCollection<InspectionAnswerInput> inputs)
    {
        TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, template);
        var byId = inputs.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.ToList());
        if (byId.Any(x => x.Value.Count != 1))
        {
            throw new DomainRuleException("Each inspection item must have exactly one answer.");
        }

        var results = new List<(Guid, bool, string?)>();
        decimal earned = 0;
        decimal total = 0;

        foreach (var item in template.Items)
        {
            total += item.Weight;
            byId.TryGetValue(item.Id, out var matches);
            var value = matches?.Single().Value;
            if (item.Required && string.IsNullOrWhiteSpace(value))
            {
                throw new DomainRuleException($"Answer for '{item.Prompt}' is required.");
            }

            var passed = EvaluateItem(item, value);
            if (passed) earned += item.Weight;
            results.Add((item.Id, passed, value));
        }

        if (byId.Keys.Except(template.Items.Select(x => x.Id)).Any())
        {
            throw new DomainRuleException("An answer references an item outside the template.");
        }

        var score = total == 0 ? 100 : decimal.Round(earned / total * 100, 2);
        return new InspectionEvaluation(score, score >= template.PassingScore, results);
    }

    public async Task<InspectionSubmission> SubmitAsync(
        InspectionTemplate template,
        Guid jobId,
        Guid userId,
        IReadOnlyCollection<InspectionAnswerInput> inputs,
        CancellationToken cancellationToken)
    {
        var result = Evaluate(template, inputs);
        var submission = new InspectionSubmission(
            tenantContext.RequiredTenantId, template.Id, jobId, userId, result.Score, result.Passed, clock.UtcNow);
        foreach (var answer in result.Answers)
        {
            submission.AddAnswer(new InspectionAnswer(
                tenantContext.RequiredTenantId, submission.Id, answer.ItemId, answer.Value, answer.Passed));
        }

        await repository.AddSubmissionAsync(submission, cancellationToken);
        await repository.SaveAsync(cancellationToken);
        return submission;
    }

    private static bool EvaluateItem(InspectionTemplateItem item, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return !item.Required;
        return item.Type switch
        {
            InspectionItemType.Boolean => bool.TryParse(value, out var boolean) && boolean,
            InspectionItemType.Number => decimal.TryParse(value, out var number)
                && (!item.MinValue.HasValue || number >= item.MinValue)
                && (!item.MaxValue.HasValue || number <= item.MaxValue),
            InspectionItemType.Text => item.ExpectedText is null
                || string.Equals(value.Trim(), item.ExpectedText.Trim(), StringComparison.OrdinalIgnoreCase),
            InspectionItemType.PhotoReference => value.StartsWith("obj://", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
