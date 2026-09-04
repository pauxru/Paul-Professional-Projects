using LoanOrigination.Application.Contracts;
using LoanOrigination.Application.Ports;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Rules;

namespace LoanOrigination.Application.Services;

public sealed class RulesetService(
    ILoanRepository repository,
    DeclarativeRulesEngine rulesEngine,
    IClock clock,
    IAuditWriter auditWriter)
{
    public async Task<RuleSetDefinition> CreateAsync(
        RuleSetDefinition candidate,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.Id) || candidate.Version <= 0 || candidate.Rules.Count == 0)
        {
            throw new DomainException("Ruleset id, positive version, and at least one rule are required.");
        }

        var existing = await repository.GetRulesetAsync(candidate.Id, candidate.Version, cancellationToken);
        if (existing is not null)
        {
            throw new DomainException("Ruleset version already exists and is immutable.");
        }

        // Evaluation validates field names, condition structure, typed values, and outcome payloads before publication.
        var validationFacts = new ApplicantFacts(35, 100_000m, 30_000m, 10_000m, 1, 36, 24, 0, 200_000m, 100_000m, 12, "B", "Passed", false);
        _ = rulesEngine.Evaluate(candidate, validationFacts);
        var stored = candidate with { EffectiveFrom = candidate.EffectiveFrom == default ? clock.UtcNow : candidate.EffectiveFrom };
        await repository.AddRulesetAsync(stored, cancellationToken);
        await auditWriter.WriteAsync(actor, "ruleset.version-published", $"rulesets/{stored.Id}/{stored.Version}", null, stored, correlationId, sourceIp, userAgent, cancellationToken);
        return stored;
    }

    public async Task<PagedResult<RuleSetDefinition>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var values = (await repository.ListRulesetsAsync(cancellationToken))
            .OrderBy(ruleset => ruleset.Id, StringComparer.Ordinal)
            .ThenByDescending(ruleset => ruleset.Version)
            .ToArray();
        return Pagination.Page(values, page, pageSize);
    }

    public async Task<DecisionTrace> EvaluateAsync(string id, int version, ApplicantFacts facts, CancellationToken cancellationToken)
    {
        var ruleset = await repository.GetRulesetAsync(id, version, cancellationToken)
            ?? throw new DomainException("Ruleset version was not found.");
        return rulesEngine.Evaluate(ruleset, facts);
    }

    public async Task<WhatIfReport> SimulateAsync(WhatIfRequest request, CancellationToken cancellationToken)
    {
        var applications = (await repository.ListApplicationsAsync(cancellationToken))
            .Where(application => application.DecisionTrace is not null)
            .OrderBy(application => application.CreatedAt)
            .ToArray();
        var deltas = applications.Select(application =>
        {
            var historical = application.DecisionTrace!;
            var candidate = rulesEngine.Evaluate(request.CandidateRuleset, application.Facts);
            var flipped = historical.Decision != candidate.Decision;
            return new WhatIfApplicationDelta(
                application.Id,
                historical.Decision,
                candidate.Decision,
                flipped,
                flipped
                    ? $"Decision changes from {historical.Decision} to {candidate.Decision}."
                    : "Decision remains unchanged.");
        }).ToArray();
        return new WhatIfReport(
            request.CandidateRuleset.Id,
            request.CandidateRuleset.Version,
            deltas.Length,
            deltas.Count(delta => delta.Flipped),
            deltas);
    }
}
