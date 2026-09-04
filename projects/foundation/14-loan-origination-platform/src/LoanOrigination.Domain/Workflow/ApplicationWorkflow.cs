using LoanOrigination.Domain.Models;

namespace LoanOrigination.Domain.Workflow;

public sealed record WorkflowSla(
    TimeSpan Draft,
    TimeSpan Submitted,
    TimeSpan DocumentsPending,
    TimeSpan KycInProgress,
    TimeSpan Screening,
    TimeSpan Underwriting,
    TimeSpan Offered)
{
    public static WorkflowSla Default { get; } = new(
        TimeSpan.FromHours(24),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(24),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(8),
        TimeSpan.FromHours(24),
        TimeSpan.FromDays(7));

    public DateTimeOffset? DueAt(ApplicationStage stage, DateTimeOffset enteredAt) => stage switch
    {
        ApplicationStage.Draft => enteredAt.Add(Draft),
        ApplicationStage.Submitted => enteredAt.Add(Submitted),
        ApplicationStage.DocumentsPending => enteredAt.Add(DocumentsPending),
        ApplicationStage.KycInProgress => enteredAt.Add(KycInProgress),
        ApplicationStage.Screening => enteredAt.Add(Screening),
        ApplicationStage.Underwriting => enteredAt.Add(Underwriting),
        ApplicationStage.Offered => enteredAt.Add(Offered),
        _ => null
    };
}

public static class ApplicationWorkflow
{
    private static readonly IReadOnlyDictionary<ApplicationStage, IReadOnlySet<ApplicationStage>> AllowedTransitions =
        new Dictionary<ApplicationStage, IReadOnlySet<ApplicationStage>>
        {
            [ApplicationStage.Draft] = Set(ApplicationStage.Submitted, ApplicationStage.Withdrawn),
            [ApplicationStage.Submitted] = Set(
                ApplicationStage.DocumentsPending,
                ApplicationStage.KycInProgress,
                ApplicationStage.Withdrawn,
                ApplicationStage.Expired),
            [ApplicationStage.DocumentsPending] = Set(
                ApplicationStage.KycInProgress,
                ApplicationStage.Withdrawn,
                ApplicationStage.Expired),
            [ApplicationStage.KycInProgress] = Set(
                ApplicationStage.Screening,
                ApplicationStage.DocumentsPending,
                ApplicationStage.Declined,
                ApplicationStage.Withdrawn,
                ApplicationStage.Expired),
            [ApplicationStage.Screening] = Set(
                ApplicationStage.Underwriting,
                ApplicationStage.Declined,
                ApplicationStage.DocumentsPending,
                ApplicationStage.Withdrawn,
                ApplicationStage.Expired),
            [ApplicationStage.Underwriting] = Set(
                ApplicationStage.Offered,
                ApplicationStage.Declined,
                ApplicationStage.DocumentsPending,
                ApplicationStage.Withdrawn,
                ApplicationStage.Expired),
            [ApplicationStage.Offered] = Set(
                ApplicationStage.Accepted,
                ApplicationStage.Withdrawn,
                ApplicationStage.Expired),
            [ApplicationStage.Accepted] = Set(ApplicationStage.Disbursed),
            [ApplicationStage.Disbursed] = Set(),
            [ApplicationStage.Declined] = Set(),
            [ApplicationStage.Withdrawn] = Set(),
            [ApplicationStage.Expired] = Set()
        };

    public static bool CanTransition(ApplicationStage from, ApplicationStage to) =>
        AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static LoanApplication Transition(
        LoanApplication application,
        ApplicationStage to,
        string actor,
        string reason,
        string correlationId,
        DateTimeOffset now,
        WorkflowSla? sla = null)
    {
        if (!CanTransition(application.Stage, to))
        {
            throw new DomainException($"Transition from {application.Stage} to {to} is not allowed.");
        }

        var eventEntry = new ApplicationEvent(
            Guid.NewGuid(),
            application.Stage,
            to,
            actor,
            reason,
            now,
            correlationId);
        var policy = sla ?? WorkflowSla.Default;
        return application with
        {
            Stage = to,
            StageEnteredAt = now,
            SlaDueAt = policy.DueAt(to, now),
            Events = application.Events.Append(eventEntry).ToArray(),
            Version = application.Version + 1
        };
    }

    private static IReadOnlySet<ApplicationStage> Set(params ApplicationStage[] values) =>
        new HashSet<ApplicationStage>(values);
}
