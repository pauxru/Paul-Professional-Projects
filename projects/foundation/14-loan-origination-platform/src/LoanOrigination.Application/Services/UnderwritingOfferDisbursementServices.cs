using LoanOrigination.Application.Contracts;
using LoanOrigination.Application.Ports;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Workflow;

namespace LoanOrigination.Application.Services;

public sealed class UnderwritingService(ILoanRepository repository, IClock clock, IAuditWriter auditWriter)
{
    public async Task<PagedResult<UnderwritingQueueItem>> GetQueueAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var items = (await repository.ListQueueItemsAsync(cancellationToken))
            .Select(item => item with
            {
                Priority = UnderwritingAuthority.CalculatePriority(
                    item.Exposure,
                    item.RiskBand,
                    item.QueuedAt,
                    item.SlaDueAt,
                    clock.UtcNow)
            })
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.SlaDueAt)
            .ToArray();
        return Pagination.Page(items, page, pageSize);
    }

    public async Task<UnderwritingQueueItem> ClaimAsync(
        Guid applicationId,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var item = await repository.GetQueueItemAsync(applicationId, cancellationToken)
            ?? throw new DomainException("Underwriting work item was not found.");
        var claimed = UnderwritingAuthority.Claim(item, actor, clock.UtcNow, TimeSpan.FromMinutes(20));
        await repository.UpsertQueueItemAsync(claimed, cancellationToken);
        await auditWriter.WriteAsync(actor, "underwriting.claimed", $"underwriting/{applicationId}", item, claimed, correlationId, sourceIp, userAgent, cancellationToken);
        return claimed;
    }

    public async Task<LoanApplication> DecideAsync(
        Guid applicationId,
        UnderwritingDecisionRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var application = await repository.GetApplicationAsync(applicationId, cancellationToken)
            ?? throw new DomainException("Application was not found.");
        if (application.Stage != ApplicationStage.Underwriting)
        {
            throw new DomainException("Only applications in underwriting can receive an underwriting decision.");
        }

        var normalizedDecision = request.Decision.Trim().ToUpperInvariant();
        if (normalizedDecision is not ("APPROVE" or "DECLINE" or "REFER"))
        {
            throw new DomainException("Decision must be APPROVE, DECLINE, or REFER.");
        }

        var now = clock.UtcNow;
        LoanApplication next;
        if (normalizedDecision == "APPROVE")
        {
            if (request.ApprovedPrincipal <= 0m || request.AnnualRate < 0m || request.TermMonths <= 0)
            {
                throw new DomainException("Approved principal, rate, and term are invalid.");
            }

            UnderwritingAuthority.ValidateApproval(
                request.ApprovedPrincipal,
                actor,
                request.DecisionMakerRole,
                request.SecondApprover,
                request.SecondApproverRole);
            var decision = new UnderwritingDecision(
                normalizedDecision,
                request.ApprovedPrincipal,
                request.AnnualRate,
                request.TermMonths,
                request.Reason,
                actor,
                request.DecisionMakerRole,
                request.SecondApprover,
                now);
            next = application with { UnderwritingDecision = decision };
            next = ApplicationWorkflow.Transition(next, ApplicationStage.Offered, actor, request.Reason, correlationId, now);
        }
        else if (normalizedDecision == "DECLINE")
        {
            var decision = new UnderwritingDecision(
                normalizedDecision,
                0m,
                0m,
                0,
                request.Reason,
                actor,
                request.DecisionMakerRole,
                null,
                now);
            next = application with { UnderwritingDecision = decision };
            next = ApplicationWorkflow.Transition(next, ApplicationStage.Declined, actor, request.Reason, correlationId, now);
        }
        else
        {
            var decision = new UnderwritingDecision(
                normalizedDecision,
                0m,
                0m,
                0,
                request.Reason,
                actor,
                request.DecisionMakerRole,
                null,
                now);
            next = application with { UnderwritingDecision = decision };
            next = ApplicationWorkflow.Transition(next, ApplicationStage.DocumentsPending, actor, request.Reason, correlationId, now);
        }

        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await auditWriter.WriteAsync(actor, "underwriting.decided", $"applications/{applicationId}", application, next, correlationId, sourceIp, userAgent, cancellationToken);
        return next;
    }
}

public sealed class OfferService(ILoanRepository repository, IClock clock, IAuditWriter auditWriter)
{
    public async Task<LoanOffer> CreateAsync(
        CreateOfferRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var application = await repository.GetApplicationAsync(request.ApplicationId, cancellationToken)
            ?? throw new DomainException("Application was not found.");
        if (application.Stage != ApplicationStage.Offered || application.UnderwritingDecision?.Decision != "APPROVE")
        {
            throw new DomainException("An offer can only be generated after an approved underwriting decision.");
        }

        var approved = application.UnderwritingDecision;
        if (request.Principal != approved.ApprovedPrincipal ||
            request.AnnualRate != approved.AnnualRate ||
            request.TermMonths != approved.TermMonths)
        {
            throw new DomainException("Offer terms must exactly match the recorded approved terms.");
        }

        var product = await repository.GetProductAsync(application.ProductCode, application.ProductVersion, cancellationToken)
            ?? throw new DomainException("Bound product version was not found.");
        var priorOffers = await repository.ListOffersForApplicationAsync(application.Id, cancellationToken);
        foreach (var prior in priorOffers.Where(offer => offer.Status == OfferStatus.Issued))
        {
            await repository.SaveOfferAsync(prior with { Status = OfferStatus.Superseded }, cancellationToken);
        }

        var offer = OfferLifecycle.Create(
            application.Id,
            priorOffers.Select(prior => prior.Version).DefaultIfEmpty(0).Max() + 1,
            request.Principal,
            request.AnnualRate,
            request.TermMonths,
            product.InterestMethod,
            product.Currency,
            product.Fees,
            clock.UtcNow,
            TimeSpan.FromDays(Math.Clamp(request.ValidityDays, 1, 30)));
        await repository.AddOfferAsync(offer, cancellationToken);
        var next = application with { OfferId = offer.Id, Version = application.Version + 1 };
        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await auditWriter.WriteAsync(actor, "offer.issued", $"offers/{offer.Id}", null, offer, correlationId, sourceIp, userAgent, cancellationToken);
        return offer;
    }

    public async Task<LoanOffer> AcceptAsync(
        Guid offerId,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var offer = await repository.GetOfferAsync(offerId, cancellationToken)
            ?? throw new DomainException("Offer was not found.");
        var current = OfferLifecycle.ExpireIfNecessary(offer, clock.UtcNow);
        if (current.Status == OfferStatus.Expired)
        {
            await repository.SaveOfferAsync(current, cancellationToken);
            var expiredApplication = await repository.GetApplicationAsync(offer.ApplicationId, cancellationToken);
            if (expiredApplication?.Stage == ApplicationStage.Offered)
            {
                var transitioned = ApplicationWorkflow.Transition(
                    expiredApplication,
                    ApplicationStage.Expired,
                    actor,
                    "Offer reached its expiry timestamp.",
                    correlationId,
                    clock.UtcNow);
                await repository.SaveApplicationAsync(transitioned, expiredApplication.Version, cancellationToken);
            }

            await auditWriter.WriteAsync(actor, "offer.expired", $"offers/{offer.Id}", offer, current, correlationId, sourceIp, userAgent, cancellationToken);
            throw new DomainException("The offer has expired.");
        }

        var accepted = OfferLifecycle.Accept(current, actor, clock.UtcNow);
        var application = await repository.GetApplicationAsync(offer.ApplicationId, cancellationToken)
            ?? throw new DomainException("Application was not found.");
        var next = ApplicationWorkflow.Transition(application, ApplicationStage.Accepted, actor, "Offer accepted.", correlationId, clock.UtcNow);
        await repository.SaveOfferAsync(accepted, cancellationToken);
        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await auditWriter.WriteAsync(actor, "offer.accepted", $"offers/{offer.Id}", offer, accepted, correlationId, sourceIp, userAgent, cancellationToken);
        return accepted;
    }

    public async Task<LoanOffer> CounterAsync(
        Guid priorOfferId,
        CounterOfferRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var prior = await repository.GetOfferAsync(priorOfferId, cancellationToken)
            ?? throw new DomainException("Offer was not found.");
        var application = await repository.GetApplicationAsync(prior.ApplicationId, cancellationToken)
            ?? throw new DomainException("Application was not found.");
        if (application.Stage != ApplicationStage.Offered)
        {
            throw new DomainException("A counter-offer can only be made while the application is offered.");
        }

        var counter = OfferLifecycle.Counter(prior, request.Principal, request.AnnualRate, request.TermMonths, clock.UtcNow);
        await repository.SaveOfferAsync(prior with { Status = OfferStatus.Superseded }, cancellationToken);
        await repository.AddOfferAsync(counter, cancellationToken);
        var next = application with { OfferId = counter.Id, Version = application.Version + 1 };
        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await auditWriter.WriteAsync(actor, "offer.countered", $"offers/{counter.Id}", prior, counter, correlationId, sourceIp, userAgent, cancellationToken);
        return counter;
    }

    public Task<LoanOffer?> GetAsync(Guid offerId, CancellationToken cancellationToken) =>
        repository.GetOfferAsync(offerId, cancellationToken);
}

public sealed class DisbursementService(
    ILoanRepository repository,
    IDisbursementProvider provider,
    IClock clock,
    IAuditWriter auditWriter)
{
    public async Task<DisbursementRecord> RequestAsync(
        DisbursementRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetDisbursementByReferenceAsync(request.ProviderReference, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var offer = await repository.GetOfferAsync(request.OfferId, cancellationToken)
            ?? throw new DomainException("Offer was not found.");
        if (offer.Status != OfferStatus.Accepted)
        {
            throw new DomainException("Only an accepted offer can be disbursed.");
        }

        var application = await repository.GetApplicationAsync(offer.ApplicationId, cancellationToken)
            ?? throw new DomainException("Application was not found.");
        if (application.Stage != ApplicationStage.Accepted)
        {
            throw new DomainException("Application is not ready for disbursement.");
        }

        var result = await provider.SendAsync(request.ProviderReference, offer.Principal, request.Rail, cancellationToken);
        var now = clock.UtcNow;
        var record = new DisbursementRecord(
            Guid.NewGuid(),
            application.Id,
            offer.Id,
            result.ProviderReference,
            offer.Principal,
            result.Status == DisbursementStatus.Succeeded ? result.Amount : 0m,
            request.Rail,
            result.Status,
            1,
            now,
            now,
            result.Reason);
        await repository.AddDisbursementAsync(record, cancellationToken);
        if (record.Status == DisbursementStatus.Succeeded)
        {
            var next = ApplicationWorkflow.Transition(application, ApplicationStage.Disbursed, actor, "Disbursement provider confirmed transfer.", correlationId, now);
            await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        }

        await auditWriter.WriteAsync(actor, "disbursement.requested", $"disbursements/{record.Id}", null, record, correlationId, sourceIp, userAgent, cancellationToken);
        return record;
    }

    public async Task<DisbursementRecord> HandleCallbackAsync(
        DisbursementCallbackRequest callback,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetDisbursementByReferenceAsync(callback.ProviderReference, cancellationToken)
            ?? throw new DomainException("Disbursement reference was not found.");
        var updated = existing with
        {
            Status = callback.Status,
            DisbursedAmount = callback.Status == DisbursementStatus.Succeeded ? callback.Amount : existing.DisbursedAmount,
            FailureReason = callback.Reason,
            UpdatedAt = clock.UtcNow,
            AttemptCount = existing.AttemptCount + 1
        };
        await repository.SaveDisbursementAsync(updated, cancellationToken);
        if (callback.Status == DisbursementStatus.Succeeded)
        {
            var application = await repository.GetApplicationAsync(updated.ApplicationId, cancellationToken)
                ?? throw new DomainException("Application was not found.");
            if (application.Stage == ApplicationStage.Accepted)
            {
                var next = ApplicationWorkflow.Transition(application, ApplicationStage.Disbursed, actor, "Disbursement callback confirmed transfer.", correlationId, clock.UtcNow);
                await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
            }
        }

        await auditWriter.WriteAsync(actor, "disbursement.callback", $"disbursements/{updated.Id}", existing, updated, correlationId, sourceIp, userAgent, cancellationToken);
        return updated;
    }

    public async Task<DisbursementRecord> RetryAsync(
        string failedProviderReference,
        RetryDisbursementRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var failed = await repository.GetDisbursementByReferenceAsync(failedProviderReference, cancellationToken)
            ?? throw new DomainException("Disbursement reference was not found.");
        if (failed.Status != DisbursementStatus.Failed)
        {
            throw new DomainException("Only a failed disbursement can be retried.");
        }

        var retry = await RequestAsync(
            new DisbursementRequest(failed.OfferId, request.NewProviderReference, failed.Rail),
            actor,
            correlationId,
            sourceIp,
            userAgent,
            cancellationToken);
        await auditWriter.WriteAsync(actor, "disbursement.retried", $"disbursements/{retry.Id}", failed, retry, correlationId, sourceIp, userAgent, cancellationToken);
        return retry;
    }
}
