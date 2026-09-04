using System.Diagnostics;
using LoanOrigination.Api.Configuration;
using LoanOrigination.Application.Contracts;
using LoanOrigination.Application.Ports;
using LoanOrigination.Application.Services;
using LoanOrigination.Domain.Models;

namespace LoanOrigination.Api.Endpoints;

public static class ApplicationWorkflowEndpoints
{
    public static IEndpointRouteBuilder MapApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/applications")
            .RequireRateLimiting("api")
            .WithTags("Loan applications");

        group.MapPost("/", async (
            CreateApplicationRequest request,
            LoanApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (request.RequestedPrincipal <= 0m || request.RequestedTermMonths <= 0)
            {
                return ApiValidation.Invalid(
                    request.RequestedPrincipal <= 0m ? "requestedPrincipal" : "requestedTermMonths",
                    "Requested principal and term must be greater than zero.");
            }

            var application = await service.CreateAsync(
                request,
                RequestMetadata.Actor(context),
                RequestMetadata.CorrelationId(context),
                RequestMetadata.SourceIp(context),
                RequestMetadata.UserAgent(context),
                cancellationToken);
            return Results.Created($"/api/v1/applications/{application.Id}", application);
        }).RequireAuthorization(ScopePolicies.Apply);

        group.MapGet("/", async (int page, int pageSize, LoanApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(page, pageSize, cancellationToken)))
            .RequireAuthorization(ScopePolicies.Apply);

        group.MapGet("/{applicationId:guid}", async (Guid applicationId, LoanApplicationService service, CancellationToken cancellationToken) =>
        {
            var application = await service.GetAsync(applicationId, cancellationToken);
            return application is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Application was not found.")
                : Results.Ok(application);
        }).RequireAuthorization(ScopePolicies.Apply);

        group.MapPost("/{applicationId:guid}/submit", async (
            Guid applicationId,
            LoanApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.SubmitAsync(applicationId, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)))
            .RequireAuthorization(ScopePolicies.Apply);

        group.MapPost("/{applicationId:guid}/withdraw", async (
            Guid applicationId,
            string? reason,
            LoanApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.WithdrawAsync(applicationId, reason ?? string.Empty, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)))
            .RequireAuthorization(ScopePolicies.Apply);

        group.MapPost("/{applicationId:guid}/documents", async (
            Guid applicationId,
            UploadDocumentRequest request,
            LoanApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.UploadDocumentAsync(applicationId, request, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)))
            .RequireAuthorization(ScopePolicies.Apply);

        group.MapPost("/{applicationId:guid}/documents/{documentId:guid}/verify", async (
            Guid applicationId,
            Guid documentId,
            VerifyDocumentRequest request,
            LoanApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.VerifyDocumentAsync(applicationId, documentId, request, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)))
            .RequireAuthorization(ScopePolicies.Underwrite);

        group.MapPost("/{applicationId:guid}/documents/complete", async (
            Guid applicationId,
            LoanApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ContinueAfterDocumentsAsync(applicationId, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)))
            .RequireAuthorization(ScopePolicies.Apply);

        group.MapPost("/{applicationId:guid}/kyc", async (
            Guid applicationId,
            LoanApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.RunKycAsync(applicationId, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)))
            .RequireAuthorization(ScopePolicies.Apply);

        group.MapPost("/{applicationId:guid}/kyc/override", async (
            Guid applicationId,
            KycOverrideRequest request,
            LoanApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.OverrideKycAsync(applicationId, request, true, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)))
            .RequireAuthorization(ScopePolicies.Admin);

        group.MapPost("/{applicationId:guid}/decision", async (
            Guid applicationId,
            LoanApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var stopwatch = Stopwatch.StartNew();
            var prior = await service.GetAsync(applicationId, cancellationToken);
            var application = await service.EvaluateDecisionAsync(applicationId, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken);
            LoanTelemetry.DecisionLatencyMilliseconds.Record(stopwatch.Elapsed.TotalMilliseconds);
            if (prior?.IsSlaBreached(DateTimeOffset.UtcNow) == true)
            {
                LoanTelemetry.SlaBreachCount.Add(1);
            }

            LoanTelemetry.RecordScreeningOutcome(application.Stage == ApplicationStage.Underwriting);

            return Results.Ok(application);
        }).RequireAuthorization(ScopePolicies.Underwrite);

        group.MapGet("/{applicationId:guid}/decision-record", async (
            Guid applicationId,
            ILoanRepository repository,
            CancellationToken cancellationToken) =>
        {
            var decisionRecord = await repository.GetDecisionRecordAsync(applicationId, cancellationToken);
            return decisionRecord is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Decision record was not found.")
                : Results.Ok(decisionRecord);
        }).RequireAuthorization(ScopePolicies.Underwrite);

        return app;
    }

    public static IEndpointRouteBuilder MapUnderwritingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/underwriting")
            .RequireAuthorization(ScopePolicies.Underwrite)
            .RequireRateLimiting("api")
            .WithTags("Underwriting queue");

        group.MapGet("/queue", async (int page, int pageSize, UnderwritingService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetQueueAsync(page, pageSize, cancellationToken)));

        group.MapPost("/queue/{applicationId:guid}/claim", async (
            Guid applicationId,
            UnderwritingService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ClaimAsync(applicationId, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)));

        group.MapPost("/queue/{applicationId:guid}/decision", async (
            Guid applicationId,
            UnderwritingDecisionRequest request,
            UnderwritingService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var application = await service.DecideAsync(applicationId, request, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken);
            LoanTelemetry.RecordUnderwritingOutcome(application.UnderwritingDecision?.Decision);

            if (application.Stage == ApplicationStage.DocumentsPending)
            {
                LoanTelemetry.ReferralCount.Add(1);
            }

            return Results.Ok(application);
        });
        return app;
    }

    public static IEndpointRouteBuilder MapOfferEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/offers")
            .RequireRateLimiting("api")
            .WithTags("Offers");

        group.MapPost("/", async (
            CreateOfferRequest request,
            OfferService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var offer = await service.CreateAsync(request, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken);
            return Results.Created($"/api/v1/offers/{offer.Id}", offer);
        }).RequireAuthorization(ScopePolicies.Underwrite);

        group.MapGet("/{offerId:guid}", async (Guid offerId, OfferService service, CancellationToken cancellationToken) =>
        {
            var offer = await service.GetAsync(offerId, cancellationToken);
            return offer is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Offer was not found.")
                : Results.Ok(offer);
        }).RequireAuthorization(ScopePolicies.Apply);

        group.MapPost("/{offerId:guid}/accept", async (
            Guid offerId,
            OfferService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.AcceptAsync(offerId, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)))
            .RequireAuthorization(ScopePolicies.Apply);

        group.MapPost("/{offerId:guid}/counter", async (
            Guid offerId,
            CounterOfferRequest request,
            OfferService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var offer = await service.CounterAsync(offerId, request, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken);
            return Results.Created($"/api/v1/offers/{offer.Id}", offer);
        }).RequireAuthorization(ScopePolicies.Underwrite);
        return app;
    }

    public static IEndpointRouteBuilder MapDisbursementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/disbursements")
            .RequireAuthorization(ScopePolicies.Approve)
            .RequireRateLimiting("api")
            .WithTags("Disbursements");

        group.MapPost("/", async (
            DisbursementRequest request,
            DisbursementService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var disbursement = await service.RequestAsync(request, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken);
            return Results.Created($"/api/v1/disbursements/{disbursement.Id}", disbursement);
        });

        group.MapPost("/callback", async (
            DisbursementCallbackRequest request,
            DisbursementService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.HandleCallbackAsync(request, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)));

        group.MapPost("/{providerReference}/retry", async (
            string providerReference,
            RetryDisbursementRequest request,
            DisbursementService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.RetryAsync(providerReference, request, RequestMetadata.Actor(context), RequestMetadata.CorrelationId(context), RequestMetadata.SourceIp(context), RequestMetadata.UserAgent(context), cancellationToken)));
        return app;
    }

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/audit", async (
            int page,
            int pageSize,
            ILoanRepository repository,
            CancellationToken cancellationToken) =>
        {
            var all = await repository.ListAuditsAsync(cancellationToken);
            var boundedPage = Math.Max(1, page);
            var boundedPageSize = Math.Clamp(pageSize, 1, 100);
            var totalPages = Math.Max(1, (int)Math.Ceiling(all.Count / (decimal)boundedPageSize));
            return Results.Ok(new
            {
                items = all.OrderByDescending(entry => entry.OccurredAt).Skip((boundedPage - 1) * boundedPageSize).Take(boundedPageSize),
                page = boundedPage,
                pageSize = boundedPageSize,
                totalCount = all.Count,
                totalPages
            });
        })
        .RequireAuthorization(ScopePolicies.Admin)
        .RequireRateLimiting("api")
        .WithTags("Audit");
        return app;
    }
}
