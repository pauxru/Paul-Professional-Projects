using ReconEngine.Domain.Entities;

namespace ReconEngine.Api.Contracts;

/// <summary>Projects domain entities onto the API response DTOs (keeps navigation cycles out of JSON).</summary>
public static class Mappings
{
    public static RuleSetResponse ToResponse(this MatchingRuleSet rs) =>
        new(rs.Id, rs.Name, rs.Version, rs.VersionTag, rs.IsActive, rs.Description, rs.CreatedAtUtc);

    public static RunResponse ToResponse(this ReconciliationRun r) =>
        new(r.Id, r.RuleSetVersionTag, r.Status, r.WindowFrom, r.WindowTo, r.InputChecksum,
            r.InternalRecordCount, r.ExternalRecordCount, r.MatchCount, r.MatchedInternalCount,
            r.MatchedExternalCount, r.CarriedForwardCount, r.ExceptionCount, r.BalanceAssertionPassed,
            r.BalanceAssertionDetail, r.DurationMs, r.StartedAtUtc, r.CompletedAtUtc);

    public static ExceptionResponse ToResponse(this ReconciliationException e) =>
        new(e.Id, e.ExceptionKey, e.Type, e.Severity, e.Status, e.Currency, e.AmountMinor, e.SuggestedAction,
            e.AssignedTo, e.ApprovalRequired, e.ResolvedBy, e.ResolutionReasonCode, e.ApprovedBy,
            e.GetRecordIds(),
            e.CreatedAtUtc, e.UpdatedAtUtc,
            e.Comments.OrderBy(c => c.CreatedAtUtc)
                .Select(c => new ExceptionCommentResponse(c.Author, c.Text, c.CreatedAtUtc)).ToList(),
            e.AuditTrail.OrderBy(a => a.AtUtc)
                .Select(a => new ExceptionAuditResponse(a.Actor, a.Action, a.FromStatus.ToString(), a.ToStatus.ToString(), a.Detail, a.AtUtc)).ToList());
}
