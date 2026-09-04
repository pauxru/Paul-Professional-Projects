using Northstar.Reliability.Domain.Common;

namespace Northstar.Reliability.Domain.Postmortems;

public enum PostmortemReviewState
{
    Draft,
    InReview,
    Approved,
    Published
}

public sealed record ActionItem(
    Guid Id,
    string Description,
    string Owner,
    DateOnly DueDate,
    DateTimeOffset? CompletedAt)
{
    public bool IsComplete => CompletedAt.HasValue;
    public bool IsOverdue(DateTimeOffset now) => !IsComplete && DueDate < DateOnly.FromDateTime(now.UtcDateTime);

    public ActionItem Complete(DateTimeOffset at) => this with { CompletedAt = at };
}

public sealed record Postmortem(
    Guid Id,
    Guid IncidentId,
    string Title,
    string Summary,
    string Impact,
    IReadOnlyList<string> TimelineNarrative,
    IReadOnlyList<string> ContributingFactors,
    string WhatWentWell,
    string WhatWentPoorly,
    PostmortemReviewState ReviewState,
    IReadOnlyList<ActionItem> ActionItems,
    DateTimeOffset CreatedAt)
{
    public static Postmortem Create(
        Guid incidentId,
        string title,
        string summary,
        string impact,
        IEnumerable<string>? timelineNarrative,
        IEnumerable<string>? contributingFactors,
        string whatWentWell,
        string whatWentPoorly,
        DateTimeOffset now)
    {
        if (incidentId == Guid.Empty || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary) ||
            string.IsNullOrWhiteSpace(impact))
        {
            throw new DomainRuleViolationException("Postmortem incident, title, summary, and impact are required.");
        }

        return new Postmortem(
            Guid.NewGuid(),
            incidentId,
            title.Trim(),
            summary.Trim(),
            impact.Trim(),
            timelineNarrative?.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray() ?? [],
            contributingFactors?.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray() ?? [],
            whatWentWell?.Trim() ?? string.Empty,
            whatWentPoorly?.Trim() ?? string.Empty,
            PostmortemReviewState.Draft,
            [],
            now);
    }

    public Postmortem AddActionItem(string description, string owner, DateOnly dueDate)
    {
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(owner))
        {
            throw new DomainRuleViolationException("Action item description and owner are required.");
        }

        return this with
        {
            ActionItems = ActionItems.Append(new ActionItem(Guid.NewGuid(), description.Trim(), owner.Trim(), dueDate, null)).ToArray()
        };
    }

    public Postmortem CompleteActionItem(Guid actionItemId, DateTimeOffset at)
    {
        var action = ActionItems.SingleOrDefault(item => item.Id == actionItemId);
        if (action is null)
        {
            throw new DomainRuleViolationException("Action item was not found.");
        }

        return this with
        {
            ActionItems = ActionItems.Select(item => item.Id == actionItemId ? item.Complete(at) : item).ToArray()
        };
    }

    public Postmortem SubmitForReview()
    {
        EnsureState(PostmortemReviewState.Draft);
        return this with { ReviewState = PostmortemReviewState.InReview };
    }

    public Postmortem Approve()
    {
        EnsureState(PostmortemReviewState.InReview);
        return this with { ReviewState = PostmortemReviewState.Approved };
    }

    public Postmortem Publish()
    {
        EnsureState(PostmortemReviewState.Approved);
        return this with { ReviewState = PostmortemReviewState.Published };
    }

    private void EnsureState(PostmortemReviewState expected)
    {
        if (ReviewState != expected)
        {
            throw new DomainRuleViolationException($"Postmortem must be {expected} for this transition.");
        }
    }
}

public sealed record ThemeFrequency(string Factor, int Count);

public static class PostmortemThemeAnalyzer
{
    public static IReadOnlyList<ThemeFrequency> Analyze(IEnumerable<Postmortem> postmortems) =>
        postmortems
            .SelectMany(postmortem => postmortem.ContributingFactors)
            .GroupBy(factor => factor.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ThemeFrequency(group.Key, group.Count()))
            .OrderByDescending(theme => theme.Count)
            .ThenBy(theme => theme.Factor)
            .ToArray();

    public static IReadOnlyList<ActionItem> OverdueActions(IEnumerable<Postmortem> postmortems, DateTimeOffset now) =>
        postmortems.SelectMany(postmortem => postmortem.ActionItems)
            .Where(action => action.IsOverdue(now))
            .OrderBy(action => action.DueDate)
            .ToArray();
}
